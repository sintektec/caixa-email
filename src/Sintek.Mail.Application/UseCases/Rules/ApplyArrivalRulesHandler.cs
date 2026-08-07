using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.UseCases.Rules;

/// <summary>Resumo do que a chegada de uma mensagem disparou.</summary>
/// <param name="WasBlocked">Se o remetente estava na lista de bloqueados.</param>
/// <param name="AppliedRuleCount">Quantas regras foram satisfeitas e aplicadas.</param>
public readonly record struct ArrivalRulesResult(bool WasBlocked, int AppliedRuleCount);

/// <summary>
/// Aplica a filtragem local a uma mensagem recém-chegada: primeiro a lista de remetentes
/// bloqueados, depois as regras automáticas em ordem de prioridade.
/// </summary>
/// <remarks>
/// <para>
/// O bloqueio vem antes das regras e as substitui: mensagem de remetente bloqueado vai
/// para o lixo eletrônico com os marcadores <c>$Junk</c> — o mesmo caminho de "Marcar como
/// spam" —, e avaliar regras sobre ela seria trabalho sobre algo que o usuário pediu para
/// não ver.
/// </para>
/// <para>
/// Toda movimentação decidida por regra passa por <see cref="MoveMessageHandler"/>. Se a
/// pasta de destino é restrita e a mensagem não pertence ao domínio, a ação é registrada
/// como ignorada em auditoria — não existe usuário para confirmar durante a sincronização,
/// e a regra de domínio prevalece sobre a regra do usuário.
/// </para>
/// <para>
/// Quando alguma regra tem condição de corpo, o corpo é baixado antes da avaliação — a
/// sincronização está conectada nesse momento. Se o download falhar, a avaliação usa a
/// prévia: casar pelo começo do texto é melhor que não casar nunca.
/// </para>
/// </remarks>
public sealed class ApplyArrivalRulesHandler
{
    private readonly IRuleRepository _rules;
    private readonly ISenderReputationRepository _reputations;
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IAccountRepository _accounts;
    private readonly ICategoryRepository _categories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly MoveMessageHandler _moveMessage;
    private readonly MarkAsSpamHandler _markAsSpam;
    private readonly DownloadMessageContentHandler _download;
    private readonly ComposeMessageHandler _compose;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplyArrivalRulesHandler> _logger;

    public ApplyArrivalRulesHandler(
        IRuleRepository rules,
        ISenderReputationRepository reputations,
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        ICategoryRepository categories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        MoveMessageHandler moveMessage,
        MarkAsSpamHandler markAsSpam,
        DownloadMessageContentHandler download,
        ComposeMessageHandler compose,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<ApplyArrivalRulesHandler> logger)
    {
        _rules = rules;
        _reputations = reputations;
        _messages = messages;
        _folders = folders;
        _accounts = accounts;
        _categories = categories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _moveMessage = moveMessage;
        _markAsSpam = markAsSpam;
        _download = download;
        _compose = compose;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Avalia e aplica a filtragem local à mensagem.</summary>
    public async Task<ArrivalRulesResult> HandleAsync(
        Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            return new ArrivalRulesResult(false, 0);
        }

        var account = await _accounts.GetByIdAsync(message.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new ArrivalRulesResult(false, 0);
        }

        if (await TryBlockSenderAsync(message, cancellationToken).ConfigureAwait(false))
        {
            return new ArrivalRulesResult(true, 0);
        }

        var rules = await _rules
            .ListEnabledForAccountAsync(account.Id, account.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return new ArrivalRulesResult(false, 0);
        }

        // Condição de corpo pede o corpo de verdade. A sincronização está conectada
        // agora; se o download falhar mesmo assim, a prévia é o melhor disponível.
        if (rules.Any(r => r.Conditions.Any(c => c.Field == RuleField.Body)))
        {
            var downloaded = await _download.DownloadBodyAsync(message.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!downloaded.Succeeded)
            {
                _logger.LogWarning(
                    "Condição de corpo avaliada sobre a prévia: {Reason}", downloaded.ErrorMessage);
            }
        }

        var facts = BuildFacts(message);
        var now = _timeProvider.GetUtcNow();
        var applied = 0;
        var dirty = false;

        foreach (var rule in rules)
        {
            if (!RuleEvaluator.Matches(rule, facts))
            {
                continue;
            }

            applied++;

            await _audit.RecordAsync(AuditLogEntry.Record(
                AuditEventType.RuleApplied,
                $"Regra '{rule.Name}' aplicada na chegada.",
                now,
                entityType: nameof(Message),
                entityId: message.Id,
                accountId: account.Id,
                domainDirectoryId: account.DomainDirectoryId), cancellationToken).ConfigureAwait(false);

            var stop = rule.StopProcessing;

            foreach (var action in rule.Actions)
            {
                if (action.ActionType == RuleActionType.StopProcessing)
                {
                    stop = true;
                    continue;
                }

                dirty |= await ExecuteActionAsync(rule, action, message, account, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stop)
            {
                break;
            }
        }

        if (applied > 0 || dirty)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ArrivalRulesResult(false, applied);
    }

    /// <summary>
    /// Desvia a mensagem para o lixo eletrônico se o remetente estiver bloqueado.
    /// </summary>
    private async Task<bool> TryBlockSenderAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.FromAddress is null)
        {
            return false;
        }

        var blocked = await _reputations.ListAsync(SenderReputationKind.Blocked, cancellationToken)
            .ConfigureAwait(false);

        if (!blocked.Any(entry => entry.AppliesTo(message.FromAddress, message.AccountId)))
        {
            return false;
        }

        // O mesmo caminho de "Marcar como spam": mover E aplicar $Junk, para o servidor
        // aprender junto. Só mover deixaria o filtro dele classificando errado para sempre.
        var result = await _markAsSpam.HandleAsync(message.Id, isSpam: true, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        await _audit.RecordAsync(AuditLogEntry.Record(
            AuditEventType.SenderBlocked,
            result.Succeeded
                ? "Mensagem de remetente bloqueado desviada para o lixo eletrônico."
                : $"Remetente bloqueado, mas o desvio falhou: {result.ErrorMessage}",
            now,
            severity: result.Succeeded ? AuditSeverity.Information : AuditSeverity.Warning,
            entityType: nameof(Message),
            entityId: message.Id,
            accountId: message.AccountId), cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result.Succeeded;
    }

    /// <summary>Executa uma ação; devolve se alterou estado local ainda não persistido.</summary>
    private async Task<bool> ExecuteActionAsync(
        Rule rule,
        RuleAction action,
        Message message,
        Account account,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (action.ActionType)
        {
            case RuleActionType.MoveToFolder when action.TargetFolderId is { } targetFolderId:
                await MoveAsync(rule, message, targetFolderId, cancellationToken).ConfigureAwait(false);
                return false;

            case RuleActionType.MoveToPending:
                await MoveToTypeAsync(rule, message, account.Id, FolderType.Pending, cancellationToken)
                    .ConfigureAwait(false);
                return false;

            case RuleActionType.Delete:
                await MoveToTypeAsync(rule, message, account.Id, FolderType.Trash, cancellationToken)
                    .ConfigureAwait(false);
                return false;

            case RuleActionType.ApplyCategory when action.TargetCategoryId is { } categoryId:
                if (!await _categories.IsAssignedAsync(message.Id, categoryId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await _categories.AssignAsync(
                        MessageCategory.Create(message.Id, categoryId, now), cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                return false;

            case RuleActionType.MarkAsRead:
                if (!message.IsRead)
                {
                    message.SetRead(true, now);
                    await _outbox.EnqueueAsync(
                        message.AccountId,
                        OutboxOperationType.MarkAsRead,
                        message.Id,
                        new FlagChangePayload(Seen: true, null, null),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                return false;

            case RuleActionType.MarkAsImportant:
                // Importância é metadado local: não existe marcador IMAP correspondente.
                message.SetImportance(MessageImportance.High, now);
                return true;

            case RuleActionType.Flag:
                if (!message.IsFlagged)
                {
                    message.SetFlagged(true, now);
                    await _outbox.EnqueueAsync(
                        message.AccountId,
                        OutboxOperationType.SetFlag,
                        message.Id,
                        new FlagChangePayload(null, Flagged: true, null),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                return false;

            case RuleActionType.CopyToFolder when action.TargetFolderId is { } copyTargetId:
                await CopyAsync(rule, message, copyTargetId, cancellationToken).ConfigureAwait(false);
                return false;

            case RuleActionType.Forward:
                await ForwardAsync(rule, action, message, account, cancellationToken)
                    .ConfigureAwait(false);
                return false;

            default:
                return false;
        }
    }

    private async Task MoveAsync(
        Rule rule, Message message, Guid targetFolderId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _moveMessage
                .HandleAsync(new MoveMessageCommand(message.Id, targetFolderId), cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome is MoveMessageOutcome.RequiresConfirmation or MoveMessageOutcome.Blocked)
            {
                await RecordSkippedAsync(
                    rule, message,
                    "A movimentação da regra foi recusada pela regra de domínio da pasta de destino.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Domain.Exceptions.FolderDomainRestrictionException)
        {
            // Não há usuário para confirmar durante a sincronização: a regra de domínio
            // prevalece e a ação é registrada como ignorada.
            await RecordSkippedAsync(
                rule, message,
                "A movimentação da regra foi bloqueada pela regra de domínio da pasta de destino.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(
        Rule rule, Message message, Guid targetFolderId, CancellationToken cancellationToken)
    {
        var result = await _moveMessage
            .HandleCopyAsync(message.Id, targetFolderId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome == MoveMessageOutcome.Blocked)
        {
            await RecordSkippedAsync(
                rule, message,
                result.UserMessage ?? "A cópia foi recusada pela regra de domínio da pasta de destino.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Encaminha a mensagem para o endereço configurado na regra.
    /// </summary>
    /// <remarks>
    /// O corpo e os anexos são baixados antes — a sincronização está conectada — e o envio
    /// entra na fila como qualquer outro (D-014): funciona offline dali em diante e aparece
    /// na fila visível. Se algum conteúdo não puder ser baixado, o encaminhamento inteiro é
    /// registrado como ignorado: encaminhar pela metade entregaria ao destinatário algo
    /// diferente do que o remetente mandou.
    /// </remarks>
    private async Task ForwardAsync(
        Rule rule, RuleAction action, Message message, Account account,
        CancellationToken cancellationToken)
    {
        if (!Domain.ValueObjects.EmailAddress.TryParse(action.Value, out var target))
        {
            await RecordSkippedAsync(
                rule, message,
                $"O endereço de encaminhamento '{action.Value}' não é válido.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = await _download.DownloadBodyAsync(message.Id, cancellationToken).ConfigureAwait(false);

        if (!body.Succeeded)
        {
            await RecordSkippedAsync(
                rule, message,
                $"O corpo não pôde ser baixado para o encaminhamento: {body.ErrorMessage}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var attachment in message.Attachments.Where(a => !a.IsInline && !a.IsDownloaded))
        {
            var downloaded = await _download
                .DownloadAttachmentAsync(message.Id, attachment.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!downloaded.Succeeded)
            {
                await RecordSkippedAsync(
                    rule, message,
                    $"O anexo '{attachment.FileName}' não pôde ser baixado para o encaminhamento.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var draft = DraftComposer.Compose(
            DraftKind.Forward, message, message.Body, account.EmailAddress, account.Signature);

        var result = await _compose.SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = draft.Subject,
            TextBody = draft.TextBody ?? string.Empty,
            HtmlBody = draft.HtmlBody,
            Recipients = [new DraftRecipient(AddressKind.To, target, null)],
            Attachments = message.Attachments
                .Where(a => !a.IsInline && a.StoragePath is not null)
                .Select(a => new ComposedAttachment(a.FileName, a.StoragePath!, a.ContentType, a.Size))
                .ToList(),
            InReplyTo = draft.InReplyTo,
            References = draft.References,
            ThreadId = draft.ThreadId,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await RecordSkippedAsync(
                rule, message,
                $"O encaminhamento não pôde ser enfileirado: {result.ErrorMessage}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MoveToTypeAsync(
        Rule rule, Message message, Guid accountId, FolderType folderType,
        CancellationToken cancellationToken)
    {
        var target = await _folders.GetByTypeAsync(accountId, folderType, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            await RecordSkippedAsync(
                rule, message,
                $"A conta não tem pasta do tipo {folderType} para receber a mensagem.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await MoveAsync(rule, message, target.Id, cancellationToken).ConfigureAwait(false);
    }

    private Task RecordSkippedAsync(
        Rule rule, Message message, string reason, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Ação da regra {RuleName} ignorada: {Reason}", rule.Name, reason);

        return _audit.RecordAsync(AuditLogEntry.Record(
            AuditEventType.RuleActionSkipped,
            $"Regra '{rule.Name}': {reason}",
            _timeProvider.GetUtcNow(),
            severity: AuditSeverity.Warning,
            entityType: nameof(Message),
            entityId: message.Id,
            accountId: message.AccountId), cancellationToken);
    }

    /// <summary>Monta o instantâneo que o avaliador puro consome.</summary>
    private static RuleMessageFacts BuildFacts(Message message) => new()
    {
        AccountId = message.AccountId,
        Subject = message.Subject,
        BodyText = message.Body?.TextBody ?? message.Preview,
        FromAddress = message.FromAddress?.Value,
        FromDomain = message.FromAddress?.Domain,
        Participants = message.Addresses
            .Select(a => new RuleParticipant(a.Kind, a.Address.Value, a.Domain))
            .ToList(),
        AttachmentNames = message.Attachments.Select(a => a.FileName).ToList(),
        HasAttachments = message.HasAttachments,
        Size = message.Size,
        ReceivedAt = message.ReceivedAt,
        Importance = message.Importance,
    };
}
