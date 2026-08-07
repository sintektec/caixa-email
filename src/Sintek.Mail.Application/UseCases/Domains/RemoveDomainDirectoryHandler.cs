using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Domains;

/// <summary>O que a remoção de um Diretório de Domínio levaria junto.</summary>
/// <param name="DomainName">Domínio do diretório.</param>
/// <param name="AccountCount">Contas vinculadas.</param>
/// <param name="FolderCount">Pastas locais somadas.</param>
/// <param name="MessageCount">Mensagens armazenadas localmente.</param>
public sealed record RemoveDomainDirectoryImpact(
    string DomainName,
    int AccountCount,
    int FolderCount,
    int MessageCount)
{
    /// <summary>Se nada de fato seria perdido — nenhuma conta vinculada.</summary>
    public bool IsEmpty => AccountCount == 0;
}

/// <summary>Resultado da remoção.</summary>
/// <param name="Succeeded">Se o diretório foi removido.</param>
/// <param name="Impact">O que foi (ou seria) removido.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct RemoveDomainDirectoryResult(
    bool Succeeded, RemoveDomainDirectoryImpact? Impact, string? ErrorMessage);

/// <summary>
/// Remove um Diretório de Domínio e, com ele, as contas vinculadas.
/// </summary>
/// <remarks>
/// <para>
/// Nunca em uma etapa só quando há contas. <see cref="AnalyzeAsync"/> devolve o que seria
/// perdido e <see cref="HandleAsync"/> exige confirmação explícita — a remoção apaga
/// mensagens já sincronizadas, e um clique sem aviso não pode custar a caixa postal
/// inteira.
/// </para>
/// <para>
/// A ordem é deliberada: contas primeiro, diretório depois. O banco declara a relação como
/// <c>Restrict</c> justamente para que apagar um diretório não arraste contas em cascata
/// sem ninguém decidir.
/// </para>
/// </remarks>
public sealed class RemoveDomainDirectoryHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAccountRepository _accounts;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AccountRemover _accountRemover;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoveDomainDirectoryHandler> _logger;

    public RemoveDomainDirectoryHandler(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        AccountRemover accountRemover,
        TimeProvider timeProvider,
        ILogger<RemoveDomainDirectoryHandler> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _accountRemover = accountRemover;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Mede o que seria perdido. Não altera nada.</summary>
    public async Task<RemoveDomainDirectoryImpact?> AnalyzeAsync(
        Guid domainDirectoryId, CancellationToken cancellationToken = default)
    {
        var directory = await _directories.GetByIdAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        var accounts = await _accounts.ListByDomainAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false);

        var folderCount = 0;
        var messageCount = 0;

        foreach (var account in accounts)
        {
            var impact = await _accountRemover.MeasureAsync(account.Id, cancellationToken).ConfigureAwait(false);
            folderCount += impact.FolderCount;
            messageCount += impact.MessageCount;
        }

        return new RemoveDomainDirectoryImpact(
            directory.DomainName.Value, accounts.Count, folderCount, messageCount);
    }

    /// <summary>Executa a remoção.</summary>
    /// <param name="confirmed">
    /// Confirmação explícita do usuário. Obrigatória quando há contas vinculadas.
    /// </param>
    public async Task<RemoveDomainDirectoryResult> HandleAsync(
        Guid domainDirectoryId, bool confirmed, CancellationToken cancellationToken = default)
    {
        var impact = await AnalyzeAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false);
        if (impact is null)
        {
            return new RemoveDomainDirectoryResult(false, null, "O Diretório de Domínio informado não existe.");
        }

        if (!impact.IsEmpty && !confirmed)
        {
            return new RemoveDomainDirectoryResult(
                false,
                impact,
                $"Remover o Diretório de Domínio '{impact.DomainName}' apagará {impact.AccountCount} conta(s), " +
                $"{impact.FolderCount} pasta(s) e {impact.MessageCount} mensagem(ns) guardadas neste computador. " +
                "Confirme a operação para prosseguir.");
        }

        var directory = await _directories.GetByIdAsync(domainDirectoryId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new RemoveDomainDirectoryResult(false, impact, "O Diretório de Domínio informado não existe.");
        }

        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var accounts = await _accounts.ListByDomainAsync(domainDirectoryId, ct).ConfigureAwait(false);

            foreach (var account in accounts)
            {
                await _accountRemover.RemoveAsync(account, ct).ConfigureAwait(false);
            }

            _directories.Remove(directory);

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.DomainDirectoryDeleted,
                    $"Diretório de Domínio '{impact.DomainName}' removido com {impact.AccountCount} conta(s), " +
                    $"{impact.FolderCount} pasta(s) e {impact.MessageCount} mensagem(ns) locais.",
                    now,
                    impact.IsEmpty ? AuditSeverity.Information : AuditSeverity.Warning,
                    entityType: nameof(DomainDirectory),
                    entityId: domainDirectoryId),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Diretório de Domínio {DomainId} removido com {AccountCount} conta(s).",
            domainDirectoryId, impact.AccountCount);

        return new RemoveDomainDirectoryResult(true, impact, null);
    }
}
