using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Sync;

/// <summary>
/// Laço que mantém as contas sincronizadas enquanto o aplicativo está aberto.
/// </summary>
/// <remarks>
/// <para>
/// A política — quando sincronizar, quanto esperar depois de uma falha, quando nem tentar —
/// vive em <see cref="SyncSchedule"/>, que é função pura e testável. Aqui fica apenas o que
/// depende de relógio real e de escopo de injeção: dormir, acordar e criar um escopo por
/// ciclo.
/// </para>
/// <para>
/// Cada ciclo roda em seu próprio escopo porque o <c>DbContext</c> é scoped. Reaproveitar
/// um só faria o rastreador de mudanças crescer sem limite ao longo de horas de execução,
/// e uma entidade obsoleta em memória sobreviveria a todos os ciclos seguintes.
/// </para>
/// </remarks>
public sealed class AccountSyncWorker
{
    /// <summary>Espera entre varreduras quando nenhuma conta está vencida.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Teto da espera passiva por IDLE em uma volta do laço.</summary>
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountSyncWorker> _logger;

    public AccountSyncWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AccountSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Roda até o cancelamento.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var waited = await RunOnceAsync(cancellationToken).ConfigureAwait(false);

                if (!waited)
                {
                    await Task.Delay(IdlePollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // O laço não pode morrer: um erro em uma conta deixaria o aplicativo sem
                // sincronização nenhuma até ser reiniciado, e o usuário não teria como saber.
                _logger.LogError(ex, "Erro no ciclo de sincronização. O laço continua.");

                await Task.Delay(IdlePollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executa uma varredura de todas as contas.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> quando a volta já consumiu tempo — por sincronização ou por
    /// espera passiva — e o laço não precisa dormir de novo.
    /// </returns>
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var handler = scope.ServiceProvider.GetRequiredService<SyncAccountHandler>();

        var active = await accounts.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var didWork = false;

        foreach (var account in active)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decision = SyncSchedule.Decide(account, now);

            if (decision.Action != SyncAction.SyncNow)
            {
                continue;
            }

            var result = await handler.HandleAsync(account.Id, cancellationToken).ConfigureAwait(false);
            didWork = true;

            if (!result.Succeeded)
            {
                _logger.LogInformation(
                    "Sincronização da conta {AccountId} não concluiu: {Reason}",
                    account.Id, result.ErrorMessage);
            }
        }

        return didWork || await TryIdleAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Espera passivamente por atividade na caixa de entrada da primeira conta em dia.
    /// </summary>
    /// <remarks>
    /// Uma conta só, e não todas: cada IDLE ocupa uma conexão IMAP dedicada, e mantê-las
    /// abertas para dez contas consumiria dez conexões permanentes — quantidade que vários
    /// servidores corporativos recusam por cliente.
    /// </remarks>
    private async Task<bool> TryIdleAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var accounts = services.GetRequiredService<IAccountRepository>();
        var folders = services.GetRequiredService<IFolderRepository>();
        var imap = services.GetRequiredService<IImapClient>();

        var active = await accounts.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var candidate = active.FirstOrDefault(a => a.SyncStatus == AccountSyncStatus.Online);

        if (candidate is null)
        {
            return false;
        }

        var connection = await imap.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);

        if (!connection.Succeeded || !SyncSchedule.ShouldIdle(candidate, imap.SupportsIdle))
        {
            return false;
        }

        var inbox = await folders
            .GetByTypeAsync(candidate.Id, FolderType.Inbox, cancellationToken).ConfigureAwait(false);

        if (inbox is null || inbox.IsLocalOnly)
        {
            return false;
        }

        var changed = await imap
            .WaitForChangesAsync(inbox.RemotePath, IdleWindow, cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            _logger.LogInformation(
                "O servidor anunciou novidade em '{RemotePath}'; sincronizando.", inbox.RemotePath);

            var handler = services.GetRequiredService<SyncAccountHandler>();
            await handler.HandleAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
