using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>Pedido para mover uma mensagem entre pastas.</summary>
/// <param name="MessageId">Mensagem a mover.</param>
/// <param name="TargetFolderId">Pasta de destino.</param>
/// <param name="UserConfirmed">
/// Se o usuário já confirmou uma movimentação incompatível. Só tem efeito quando o
/// Diretório de Domínio está configurado como
/// <see cref="InvalidEmailAction.WarnAndConfirm"/>.
/// </param>
public readonly record struct MoveMessageCommand(Guid MessageId, Guid TargetFolderId, bool UserConfirmed = false);

/// <summary>O que aconteceu com a movimentação.</summary>
public enum MoveMessageOutcome
{
    /// <summary>A mensagem foi movida para a pasta pedida.</summary>
    Moved = 0,

    /// <summary>A movimentação foi impedida pela regra de domínio.</summary>
    Blocked = 1,

    /// <summary>
    /// A mensagem é incompatível e o diretório exige confirmação. Nada foi alterado;
    /// reenviar o comando com <c>UserConfirmed</c> conclui a operação.
    /// </summary>
    RequiresConfirmation = 2,

    /// <summary>A mensagem foi desviada para a pasta de pendências.</summary>
    MovedToPending = 3,
}

/// <summary>Resultado da movimentação.</summary>
/// <param name="Outcome">O que aconteceu.</param>
/// <param name="UserMessage">Texto a exibir ao usuário, quando houver.</param>
/// <param name="ActualFolderId">Pasta em que a mensagem efetivamente ficou.</param>
public readonly record struct MoveMessageResult(
    MoveMessageOutcome Outcome,
    string? UserMessage,
    Guid? ActualFolderId);

/// <summary>
/// Move uma mensagem entre pastas aplicando as regras de Diretório de Domínio.
/// </summary>
/// <remarks>
/// <para>
/// Este é o <b>único</b> caminho para mover uma mensagem. Arrastar e soltar, o menu de
/// contexto, as regras automáticas e a classificação durante a sincronização passam
/// todos por aqui — é o que impede a interface de contornar a restrição, como a
/// especificação exige ao mandar aplicar as regras "em todas as operações de arrastar e
/// soltar".
/// </para>
/// <para>
/// A gravação local e o enfileiramento da operação acontecem na mesma transação, o que
/// mantém a promessa do modo offline-first: ou a interface e a fila concordam, ou nada
/// aconteceu.
/// </para>
/// </remarks>
public sealed class MoveMessageHandler
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MoveMessageHandler> _logger;

    public MoveMessageHandler(
        IMessageRepository messages,
        IFolderRepository folders,
        IDomainDirectoryRepository directories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<MoveMessageHandler> logger)
    {
        _messages = messages;
        _folders = folders;
        _directories = directories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa a movimentação.</summary>
    /// <exception cref="FolderDomainRestrictionException">
    /// A pasta de destino é restrita, a mensagem não pertence ao domínio e o diretório
    /// está configurado para bloquear.
    /// </exception>
    public async Task<MoveMessageResult> HandleAsync(
        MoveMessageCommand command, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(command.MessageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Mensagem {command.MessageId} não encontrada.");

        var targetFolder = await _folders.GetByIdAsync(command.TargetFolderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Pasta {command.TargetFolderId} não encontrada.");

        if (message.FolderId == targetFolder.Id)
        {
            return new MoveMessageResult(MoveMessageOutcome.Moved, null, targetFolder.Id);
        }

        // Pasta sem restrição: a regra de domínio não se aplica e a movimentação segue.
        if (!targetFolder.IsDomainRestricted)
        {
            return await CommitMoveAsync(message, targetFolder.Id, MoveMessageOutcome.Moved, cancellationToken)
                .ConfigureAwait(false);
        }

        var directoryId = targetFolder.EffectiveRestrictionDomainDirectoryId!.Value;
        var directory = await _directories.GetByIdAsync(directoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"A pasta '{targetFolder.DisplayName}' aponta para o Diretório de Domínio {directoryId}, " +
                "que não existe mais.");

        var participants = await _messages.GetParticipantsAsync(message.Id, cancellationToken).ConfigureAwait(false);
        var membership = DomainMembershipEvaluator.Evaluate(directory, participants);

        if (membership.IsMember)
        {
            return await CommitMoveAsync(message, targetFolder.Id, MoveMessageOutcome.Moved, cancellationToken)
                .ConfigureAwait(false);
        }

        return directory.InvalidEmailAction switch
        {
            InvalidEmailAction.Block
                => await BlockAsync(message, targetFolder, directory, cancellationToken).ConfigureAwait(false),

            InvalidEmailAction.WarnAndConfirm when !command.UserConfirmed
                => new MoveMessageResult(
                    MoveMessageOutcome.RequiresConfirmation,
                    FolderDomainRestrictionException.RestrictionMessage,
                    null),

            InvalidEmailAction.WarnAndConfirm
                => await OverrideAsync(message, targetFolder, directory, cancellationToken).ConfigureAwait(false),

            InvalidEmailAction.MoveToPending
                => await RedirectToPendingAsync(message, targetFolder, directory, cancellationToken)
                    .ConfigureAwait(false),

            InvalidEmailAction.LogOnly
                => await OverrideAsync(message, targetFolder, directory, cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                directory.InvalidEmailAction,
                "Ação para e-mail incompatível desconhecida."),
        };
    }

    /// <summary>
    /// Avalia uma mensagem que a sincronização acabou de trazer para dentro de uma pasta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vive aqui, e não no motor de sincronização, porque este é o único lugar autorizado a
    /// consultar o <see cref="DomainMembershipEvaluator"/>. Uma segunda avaliação escrita
    /// no motor divergiria da desta classe, e a divergência sempre termina com um dos dois
    /// caminhos permitindo o que o outro proíbe.
    /// </para>
    /// <para>
    /// <b>A tabela de decisão é diferente da movimentação iniciada pelo usuário</b>, e
    /// precisa ser. Uma chegada não pode ser "bloqueada": a mensagem já existe no servidor,
    /// dentro daquela pasta, e recusá-la localmente apenas a esconderia do usuário sem
    /// mudar nada do outro lado. Por isso <see cref="InvalidEmailAction.Block"/> e
    /// <see cref="InvalidEmailAction.MoveToPending"/> desviam para pendências, e os dois
    /// modos permissivos apenas registram.
    /// </para>
    /// </remarks>
    public async Task<MoveMessageResult> ClassifyArrivalAsync(
        Message message, Folder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(folder);

        if (!folder.IsDomainRestricted)
        {
            return new MoveMessageResult(MoveMessageOutcome.Moved, null, folder.Id);
        }

        var directoryId = folder.EffectiveRestrictionDomainDirectoryId!.Value;
        var directory = await _directories.GetByIdAsync(directoryId, cancellationToken).ConfigureAwait(false);

        if (directory is null)
        {
            _logger.LogWarning(
                "A pasta {FolderId} aponta para o Diretório de Domínio {DomainId}, que não existe mais.",
                folder.Id, directoryId);

            return new MoveMessageResult(MoveMessageOutcome.Moved, null, folder.Id);
        }

        var participants = await _messages.GetParticipantsAsync(message.Id, cancellationToken).ConfigureAwait(false);

        if (DomainMembershipEvaluator.Evaluate(directory, participants).IsMember)
        {
            return new MoveMessageResult(MoveMessageOutcome.Moved, null, folder.Id);
        }

        if (directory.InvalidEmailAction is InvalidEmailAction.WarnAndConfirm or InvalidEmailAction.LogOnly)
        {
            await RecordAuditAsync(
                AuditEventType.MessageMoveOverridden,
                $"Mensagem recebida na pasta '{folder.DisplayName}' não pertence ao domínio " +
                $"'{directory.DomainName.Value}' (ação configurada: {directory.InvalidEmailAction}).",
                AuditSeverity.Warning,
                message,
                directory,
                cancellationToken).ConfigureAwait(false);

            return new MoveMessageResult(MoveMessageOutcome.Moved, null, folder.Id);
        }

        var pending = await _folders
            .GetByTypeAsync(message.AccountId, FolderType.Pending, cancellationToken)
            .ConfigureAwait(false);

        if (pending is null)
        {
            // Sem pasta de pendências não há para onde desviar, e apagar a mensagem seria
            // perda de dados causada por uma configuração incompleta. Fica onde está, com
            // registro para que a incoerência apareça na auditoria.
            _logger.LogWarning(
                "A conta {AccountId} não tem pasta de pendências; a mensagem incompatível permaneceu em {FolderId}.",
                message.AccountId, folder.Id);

            return new MoveMessageResult(MoveMessageOutcome.Moved, null, folder.Id);
        }

        // Movimentação puramente local: a mensagem continua onde está no servidor, e é isso
        // que se quer — desviá-la lá mudaria a caixa postal de quem talvez use outro cliente.
        message.MoveTo(pending.Id, _timeProvider.GetUtcNow());

        await RecordAuditAsync(
            AuditEventType.MessageMovedToPending,
            $"Mensagem recebida incompatível com o domínio '{directory.DomainName.Value}' " +
            "desviada para a pasta de pendências.",
            AuditSeverity.Information,
            message,
            directory,
            cancellationToken).ConfigureAwait(false);

        return new MoveMessageResult(MoveMessageOutcome.MovedToPending, null, pending.Id);
    }

    /// <summary>
    /// Copia uma mensagem para outra pasta — a cópia é feita pelo servidor quando a fila
    /// drenar, e aparece localmente na sincronização seguinte da pasta de destino.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vive aqui, e não em um handler próprio, porque a regra de Diretório de Domínio da
    /// pasta de destino se aplica à cópia tanto quanto à movimentação, e este é o único
    /// lugar autorizado a consultar o avaliador de pertencimento.
    /// </para>
    /// <para>
    /// A tabela de decisão é mais simples que a da movimentação: cópia incompatível é
    /// recusada em qualquer modo. Não existe "desviar a cópia para pendências" — seria
    /// criar no servidor uma cópia que ninguém pediu em outra pasta — e o chamador
    /// automático (regra) não tem usuário para confirmar.
    /// </para>
    /// </remarks>
    public async Task<MoveMessageResult> HandleCopyAsync(
        Guid messageId, Guid targetFolderId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Mensagem {messageId} não encontrada.");

        var targetFolder = await _folders.GetByIdAsync(targetFolderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Pasta {targetFolderId} não encontrada.");

        if (message.FolderId == targetFolder.Id)
        {
            return new MoveMessageResult(MoveMessageOutcome.Moved, null, targetFolder.Id);
        }

        if (targetFolder.IsLocalOnly)
        {
            // O servidor não conhece pastas locais; não há onde criar a cópia.
            return new MoveMessageResult(
                MoveMessageOutcome.Blocked, "Não é possível copiar para uma pasta local.", null);
        }

        if (targetFolder.IsDomainRestricted)
        {
            var directoryId = targetFolder.EffectiveRestrictionDomainDirectoryId!.Value;
            var directory = await _directories.GetByIdAsync(directoryId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"A pasta '{targetFolder.DisplayName}' aponta para o Diretório de Domínio " +
                    $"{directoryId}, que não existe mais.");

            var participants = await _messages.GetParticipantsAsync(message.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!DomainMembershipEvaluator.Evaluate(directory, participants).IsMember)
            {
                await RecordAuditAsync(
                    AuditEventType.MessageMoveBlockedByDomainRule,
                    $"Cópia recusada: a mensagem não pertence ao domínio '{directory.DomainName.Value}' " +
                    $"exigido pela pasta '{targetFolder.DisplayName}'.",
                    AuditSeverity.Warning,
                    message,
                    directory,
                    cancellationToken).ConfigureAwait(false);

                return new MoveMessageResult(
                    MoveMessageOutcome.Blocked,
                    FolderDomainRestrictionException.RestrictionMessage,
                    null);
            }
        }

        var sourceFolderId = message.FolderId;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _outbox.EnqueueAsync(
                message.AccountId,
                OutboxOperationType.CopyMessage,
                message.Id,
                new MoveMessagePayload(sourceFolderId, targetFolder.Id),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new MoveMessageResult(MoveMessageOutcome.Moved, null, targetFolder.Id);
    }

    private async Task<MoveMessageResult> CommitMoveAsync(
        Message message, Guid targetFolderId, MoveMessageOutcome outcome, CancellationToken cancellationToken)
    {
        var sourceFolderId = message.FolderId;
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            message.MoveTo(targetFolderId, now);

            await _outbox.EnqueueAsync(
                message.AccountId,
                OutboxOperationType.MoveMessage,
                message.Id,
                new MoveMessagePayload(sourceFolderId, targetFolderId),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new MoveMessageResult(outcome, null, targetFolderId);
    }

    private async Task<MoveMessageResult> BlockAsync(
        Message message, Folder targetFolder, DomainDirectory directory, CancellationToken cancellationToken)
    {
        await RecordAuditAsync(
            AuditEventType.MessageMoveBlockedByDomainRule,
            $"Movimentação recusada: a mensagem não pertence ao domínio '{directory.DomainName.Value}' " +
            $"exigido pela pasta '{targetFolder.DisplayName}'.",
            AuditSeverity.Warning,
            message,
            directory,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Movimentação da mensagem {MessageId} para a pasta {FolderId} recusada pela regra do domínio {DomainId}.",
            message.Id, targetFolder.Id, directory.Id);

        throw new FolderDomainRestrictionException(message.Id, targetFolder.Id, directory.DomainName);
    }

    private async Task<MoveMessageResult> OverrideAsync(
        Message message, Folder targetFolder, DomainDirectory directory, CancellationToken cancellationToken)
    {
        var result = await CommitMoveAsync(message, targetFolder.Id, MoveMessageOutcome.Moved, cancellationToken)
            .ConfigureAwait(false);

        await RecordAuditAsync(
            AuditEventType.MessageMoveOverridden,
            $"Mensagem incompatível com o domínio '{directory.DomainName.Value}' movida para a pasta " +
            $"'{targetFolder.DisplayName}' (ação configurada: {directory.InvalidEmailAction}).",
            AuditSeverity.Warning,
            message,
            directory,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task<MoveMessageResult> RedirectToPendingAsync(
        Message message, Folder targetFolder, DomainDirectory directory, CancellationToken cancellationToken)
    {
        var pending = await _folders
            .GetByTypeAsync(message.AccountId, FolderType.Pending, cancellationToken)
            .ConfigureAwait(false);

        // Sem pasta de pendências configurada, desviar é impossível. Bloquear é o
        // comportamento seguro: o contrário colocaria a mensagem justamente na pasta
        // restrita que a regra queria proteger.
        if (pending is null)
        {
            _logger.LogWarning(
                "A conta {AccountId} não tem pasta de pendências; a movimentação foi recusada.",
                message.AccountId);

            return await BlockAsync(message, targetFolder, directory, cancellationToken).ConfigureAwait(false);
        }

        var result = await CommitMoveAsync(message, pending.Id, MoveMessageOutcome.MovedToPending, cancellationToken)
            .ConfigureAwait(false);

        await RecordAuditAsync(
            AuditEventType.MessageMovedToPending,
            $"Mensagem incompatível com o domínio '{directory.DomainName.Value}' desviada para a pasta de pendências.",
            AuditSeverity.Information,
            message,
            directory,
            cancellationToken).ConfigureAwait(false);

        return result with
        {
            UserMessage = FolderDomainRestrictionException.RestrictionMessage,
        };
    }

    /// <summary>
    /// Registra a decisão na auditoria.
    /// </summary>
    /// <remarks>
    /// Grava apenas identificadores e o motivo. Assunto, corpo e participantes ficam de
    /// fora: a especificação exige logs sem conteúdo sigiloso.
    /// </remarks>
    private async Task RecordAuditAsync(
        AuditEventType eventType,
        string description,
        AuditSeverity severity,
        Message message,
        DomainDirectory directory,
        CancellationToken cancellationToken)
    {
        var entry = AuditLogEntry.Record(
            eventType,
            description,
            _timeProvider.GetUtcNow(),
            severity,
            entityType: nameof(Message),
            entityId: message.Id,
            accountId: message.AccountId,
            domainDirectoryId: directory.Id);

        await _audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
