using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Accounts;

/// <summary>O que a remoção de uma conta levaria junto.</summary>
/// <param name="EmailAddress">Endereço da conta.</param>
/// <param name="FolderCount">Pastas locais.</param>
/// <param name="MessageCount">Mensagens guardadas localmente.</param>
/// <param name="PendingOperationCount">Operações ainda não sincronizadas com o servidor.</param>
public sealed record RemoveAccountImpact(
    string EmailAddress,
    int FolderCount,
    int MessageCount,
    int PendingOperationCount);

/// <summary>Resultado da remoção.</summary>
/// <param name="Succeeded">Se a conta foi removida.</param>
/// <param name="Impact">O que foi (ou seria) removido.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct RemoveAccountResult(
    bool Succeeded, RemoveAccountImpact? Impact, string? ErrorMessage);

/// <summary>
/// Remove uma conta, seus dados locais e suas credenciais.
/// </summary>
/// <remarks>
/// <para>
/// A confirmação é obrigatória, sempre. Diferente de desativar — que preserva tudo —, esta
/// operação apaga mensagens que podem só existir aqui, no caso de pastas locais que nunca
/// foram espelhadas no servidor.
/// </para>
/// <para>
/// O relatório de impacto conta separadamente as operações pendentes na fila de saída: são
/// alterações que o usuário fez e que ainda não chegaram ao servidor. Perdê-las sem aviso
/// significaria descartar trabalho que ele acredita ter feito.
/// </para>
/// </remarks>
public sealed class RemoveAccountHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IOutboxRepository _outbox;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AccountRemover _accountRemover;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoveAccountHandler> _logger;

    public RemoveAccountHandler(
        IAccountRepository accounts,
        IOutboxRepository outbox,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        AccountRemover accountRemover,
        TimeProvider timeProvider,
        ILogger<RemoveAccountHandler> logger)
    {
        _accounts = accounts;
        _outbox = outbox;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _accountRemover = accountRemover;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Mede o que seria perdido. Não altera nada.</summary>
    public async Task<RemoveAccountImpact?> AnalyzeAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return null;
        }

        var impact = await _accountRemover.MeasureAsync(accountId, cancellationToken).ConfigureAwait(false);
        var pending = await _outbox.ListPendingAsync(accountId, cancellationToken).ConfigureAwait(false);

        return new RemoveAccountImpact(
            account.EmailAddress.Value, impact.FolderCount, impact.MessageCount, pending.Count);
    }

    /// <summary>Executa a remoção.</summary>
    /// <param name="confirmed">Confirmação explícita do usuário. Sempre obrigatória.</param>
    public async Task<RemoveAccountResult> HandleAsync(
        Guid accountId, bool confirmed, CancellationToken cancellationToken = default)
    {
        var impact = await AnalyzeAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (impact is null)
        {
            return new RemoveAccountResult(false, null, "A conta informada não existe.");
        }

        if (!confirmed)
        {
            var pendingWarning = impact.PendingOperationCount > 0
                ? $" Há {impact.PendingOperationCount} operação(ões) aguardando sincronização que " +
                  "serão descartadas."
                : string.Empty;

            return new RemoveAccountResult(
                false,
                impact,
                $"Remover a conta '{impact.EmailAddress}' apagará {impact.FolderCount} pasta(s) e " +
                $"{impact.MessageCount} mensagem(ns) guardadas neste computador.{pendingWarning} " +
                "Confirme a operação para prosseguir.");
        }

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new RemoveAccountResult(false, impact, "A conta informada não existe.");
        }

        var domainDirectoryId = account.DomainDirectoryId;
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _accountRemover.RemoveAsync(account, ct).ConfigureAwait(false);

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.AccountRemoved,
                    $"Conta '{impact.EmailAddress}' removida com {impact.FolderCount} pasta(s), " +
                    $"{impact.MessageCount} mensagem(ns) locais e {impact.PendingOperationCount} " +
                    "operação(ões) pendentes descartadas.",
                    now,
                    impact.PendingOperationCount > 0 ? AuditSeverity.Warning : AuditSeverity.Information,
                    entityType: nameof(Account),
                    entityId: accountId,
                    domainDirectoryId: domainDirectoryId),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Conta {AccountId} removida.", accountId);

        return new RemoveAccountResult(true, impact, null);
    }
}
