using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Domains;

/// <summary>Uma conta que deixaria de pertencer ao diretório após a troca de domínio.</summary>
/// <param name="AccountId">Conta afetada.</param>
/// <param name="EmailAddress">Endereço da conta.</param>
/// <param name="CurrentDomain">Domínio atual da conta.</param>
public readonly record struct IncompatibleAccount(Guid AccountId, string EmailAddress, string CurrentDomain);

/// <summary>Uma mensagem que deixaria de satisfazer a regra da pasta restrita.</summary>
/// <param name="MessageId">Mensagem afetada.</param>
/// <param name="FolderId">Pasta restrita em que ela está.</param>
/// <param name="FolderName">Nome da pasta, para exibição.</param>
public readonly record struct IncompatibleMessage(Guid MessageId, Guid FolderId, string FolderName);

/// <summary>
/// Relatório do que a troca de domínio provocaria.
/// </summary>
/// <param name="CurrentDomain">Domínio atual do diretório.</param>
/// <param name="NewDomain">Domínio proposto.</param>
/// <param name="IncompatibleAccounts">Contas que deixariam de pertencer.</param>
/// <param name="IncompatibleMessages">Mensagens que violariam a regra da pasta.</param>
public sealed record ChangeDomainNameImpact(
    string CurrentDomain,
    string NewDomain,
    IReadOnlyList<IncompatibleAccount> IncompatibleAccounts,
    IReadOnlyList<IncompatibleMessage> IncompatibleMessages)
{
    /// <summary>Se a troca pode ser concluída sem deixar nada incompatível para trás.</summary>
    public bool IsClean => IncompatibleAccounts.Count == 0 && IncompatibleMessages.Count == 0;
}

/// <summary>
/// Troca o domínio de um Diretório de Domínio existente.
/// </summary>
/// <remarks>
/// <para>
/// A especificação exige um roteiro preciso: revalidar todas as contas vinculadas,
/// revalidar todas as mensagens em pastas restritas, listar as incompatíveis, permitir
/// movê-las para pendências, exigir confirmação e registrar em auditoria.
/// </para>
/// <para>
/// Por isso a operação tem duas etapas explícitas. <see cref="AnalyzeAsync"/> não altera
/// nada e devolve o impacto; <see cref="ApplyAsync"/> só executa mediante confirmação.
/// Trocar o domínio em uma etapa só poderia órfãs contas e mensagens sem que o usuário
/// tivesse visto o estrago antes.
/// </para>
/// </remarks>
public sealed class ChangeDomainNameHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChangeDomainNameHandler> _logger;

    public ChangeDomainNameHandler(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<ChangeDomainNameHandler> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _folders = folders;
        _messages = messages;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Simula a troca e devolve o que ficaria incompatível. Não altera nada.
    /// </summary>
    public async Task<ChangeDomainNameImpact> AnalyzeAsync(
        Guid domainDirectoryId, string newDomainName, CancellationToken cancellationToken = default)
    {
        var directory = await _directories.GetByIdAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Diretório de Domínio {domainDirectoryId} não encontrado.");

        var newDomain = EmailDomain.Parse(newDomainName);

        // A simulação é feita sobre um diretório desanexado, com o novo domínio já
        // aplicado. Assim a avaliação reutiliza exatamente a mesma regra que valerá
        // depois — inclusive aliases e permissão de subdomínios — sem tocar no original.
        var probe = DomainDirectory.Create(
            newDomain,
            directory.CreatedAt,
            directory.Description,
            directory.ValidationMode,
            directory.InvalidEmailAction,
            directory.AllowSubdomains,
            directory.Id);

        foreach (var alias in directory.Aliases)
        {
            probe.AddAlias(alias.DomainName, directory.CreatedAt);
        }

        var incompatibleAccounts = await FindIncompatibleAccountsAsync(probe, cancellationToken)
            .ConfigureAwait(false);

        var incompatibleMessages = await FindIncompatibleMessagesAsync(probe, cancellationToken)
            .ConfigureAwait(false);

        return new ChangeDomainNameImpact(
            directory.DomainName.Value,
            newDomain.Value,
            incompatibleAccounts,
            incompatibleMessages);
    }

    /// <summary>
    /// Executa a troca.
    /// </summary>
    /// <param name="confirmed">
    /// Confirmação explícita do usuário. Sem ela, a troca é recusada quando há
    /// incompatíveis — a especificação exige confirmação antes de concluir a alteração.
    /// </param>
    /// <param name="moveIncompatibleToPending">
    /// Se as mensagens incompatíveis devem ser desviadas para a pasta de pendências.
    /// </param>
    public async Task<ChangeDomainNameImpact> ApplyAsync(
        Guid domainDirectoryId,
        string newDomainName,
        bool confirmed,
        bool moveIncompatibleToPending,
        CancellationToken cancellationToken = default)
    {
        var impact = await AnalyzeAsync(domainDirectoryId, newDomainName, cancellationToken).ConfigureAwait(false);

        if (!impact.IsClean && !confirmed)
        {
            throw new InvalidOperationException(
                $"A troca do domínio '{impact.CurrentDomain}' para '{impact.NewDomain}' deixaria " +
                $"{impact.IncompatibleAccounts.Count} conta(s) e {impact.IncompatibleMessages.Count} " +
                "mensagem(ns) incompatíveis. Confirme a operação para prosseguir.");
        }

        var directory = await _directories.GetByIdAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Diretório de Domínio {domainDirectoryId} não encontrado.");

        var newDomain = EmailDomain.Parse(newDomainName);
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            directory.ChangeDomainName(newDomain, now);

            if (moveIncompatibleToPending && impact.IncompatibleMessages.Count > 0)
            {
                await MoveIncompatibleMessagesAsync(impact.IncompatibleMessages, now, ct).ConfigureAwait(false);
            }

            // Contas incompatíveis são desativadas, não excluídas: apagá-las levaria
            // junto todas as mensagens já sincronizadas, o que seria perda de dados
            // causada por uma edição de configuração.
            foreach (var incompatible in impact.IncompatibleAccounts)
            {
                var account = await _accounts.GetByIdAsync(incompatible.AccountId, ct).ConfigureAwait(false);
                account?.SetActive(false, now);
            }

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.DomainNameChanged,
                    $"Domínio do diretório alterado de '{impact.CurrentDomain}' para '{impact.NewDomain}'. " +
                    $"Contas incompatíveis desativadas: {impact.IncompatibleAccounts.Count}. " +
                    $"Mensagens incompatíveis: {impact.IncompatibleMessages.Count}.",
                    now,
                    impact.IsClean ? AuditSeverity.Information : AuditSeverity.Warning,
                    entityType: nameof(DomainDirectory),
                    entityId: directory.Id,
                    domainDirectoryId: directory.Id),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Domínio do diretório {DomainId} alterado; {AccountCount} conta(s) e {MessageCount} mensagem(ns) afetadas.",
            directory.Id, impact.IncompatibleAccounts.Count, impact.IncompatibleMessages.Count);

        return impact;
    }

    private async Task<IReadOnlyList<IncompatibleAccount>> FindIncompatibleAccountsAsync(
        DomainDirectory probe, CancellationToken cancellationToken)
    {
        var accounts = await _accounts.ListByDomainAsync(probe.Id, cancellationToken).ConfigureAwait(false);

        return accounts
            .Where(account => !probe.Accepts(account.EmailAddress))
            .Select(account => new IncompatibleAccount(
                account.Id,
                account.EmailAddress.Value,
                account.EmailAddress.Domain.Value))
            .ToList();
    }

    private async Task<IReadOnlyList<IncompatibleMessage>> FindIncompatibleMessagesAsync(
        DomainDirectory probe, CancellationToken cancellationToken)
    {
        var messages = await _messages.ListInRestrictedFoldersAsync(probe.Id, cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return [];
        }

        // Participantes de todas as mensagens em uma consulta só: avaliá-los mensagem a
        // mensagem transformaria a revalidação de uma caixa grande em milhares de idas
        // ao banco.
        var participantsByMessage = await _messages
            .GetParticipantsAsync(messages.Select(m => m.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var folderNames = new Dictionary<Guid, string>();
        var incompatible = new List<IncompatibleMessage>();

        foreach (var message in messages)
        {
            if (!participantsByMessage.TryGetValue(message.Id, out var participants))
            {
                continue;
            }

            if (DomainMembershipEvaluator.Evaluate(probe, participants).IsMember)
            {
                continue;
            }

            if (!folderNames.TryGetValue(message.FolderId, out var folderName))
            {
                var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
                folderName = folder?.DisplayName ?? "(pasta desconhecida)";
                folderNames[message.FolderId] = folderName;
            }

            incompatible.Add(new IncompatibleMessage(message.Id, message.FolderId, folderName));
        }

        return incompatible;
    }

    private async Task MoveIncompatibleMessagesAsync(
        IReadOnlyList<IncompatibleMessage> incompatible, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pendingByAccount = new Dictionary<Guid, Folder?>();

        foreach (var item in incompatible)
        {
            var message = await _messages.GetByIdAsync(item.MessageId, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                continue;
            }

            if (!pendingByAccount.TryGetValue(message.AccountId, out var pending))
            {
                pending = await _folders
                    .GetByTypeAsync(message.AccountId, FolderType.Pending, cancellationToken)
                    .ConfigureAwait(false);
                pendingByAccount[message.AccountId] = pending;
            }

            if (pending is null)
            {
                _logger.LogWarning(
                    "A conta {AccountId} não tem pasta de pendências; a mensagem {MessageId} permaneceu onde estava.",
                    message.AccountId, message.Id);
                continue;
            }

            var sourceFolderId = message.FolderId;
            message.MoveTo(pending.Id, now);

            await _outbox.EnqueueAsync(
                message.AccountId,
                OutboxOperationType.MoveMessage,
                message.Id,
                new MoveMessagePayload(sourceFolderId, pending.Id),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
