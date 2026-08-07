using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Sync;

/// <summary>O que fazer com uma conta no próximo passo do ciclo automático.</summary>
public enum SyncAction
{
    /// <summary>Sincronizar agora.</summary>
    SyncNow,

    /// <summary>Esperar antes de sincronizar de novo.</summary>
    Wait,

    /// <summary>Não sincronizar: a conta está desativada ou aguardando reautenticação.</summary>
    Skip,
}

/// <summary>Decisão do agendador para uma conta.</summary>
/// <param name="Action">O que fazer.</param>
/// <param name="Delay">Quanto esperar, quando a ação é <see cref="SyncAction.Wait"/>.</param>
/// <param name="Reason">Motivo, para log e para a interface.</param>
public readonly record struct SyncDecision(SyncAction Action, TimeSpan Delay, string Reason);

/// <summary>
/// Decide quando cada conta deve sincronizar.
/// </summary>
/// <remarks>
/// É função pura de propósito. O laço que dorme e acorda vive na infraestrutura; a política
/// — quanto esperar depois de uma falha, quando desistir, quando nem tentar — fica aqui,
/// onde pode ser verificada sem relógio real nem servidor.
/// </remarks>
public static class SyncSchedule
{
    /// <summary>Espera após uma falha de rede, antes do primeiro recuo.</summary>
    private static readonly TimeSpan OfflineRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Teto da espera entre tentativas de uma conta com problema.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>Decide o que fazer com a conta neste instante.</summary>
    public static SyncDecision Decide(Account account, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!account.IsActive)
        {
            return new SyncDecision(SyncAction.Skip, TimeSpan.Zero, "A conta está desativada.");
        }

        // Credencial recusada não melhora com insistência, e tentar de novo a cada minuto é
        // a forma mais rápida de ganhar um bloqueio temporário no provedor. A conta volta
        // ao ciclo quando o usuário reautenticar, o que muda o estado dela.
        if (account.SyncStatus == AccountSyncStatus.AuthenticationFailed)
        {
            return new SyncDecision(
                SyncAction.Skip,
                TimeSpan.Zero,
                "As credenciais foram recusadas. A conta volta ao ciclo após nova autenticação.");
        }

        if (account.LastSyncAt is not { } lastSync)
        {
            return new SyncDecision(SyncAction.SyncNow, TimeSpan.Zero, "A conta nunca sincronizou.");
        }

        var interval = account.SyncStatus switch
        {
            // Sem conexão, o intervalo configurado não faz sentido: o que interessa é
            // perceber a rede voltando, e um minuto é curto o bastante sem ser agressivo.
            AccountSyncStatus.Offline => OfflineRetryDelay,
            AccountSyncStatus.Error => ErrorBackoff(account),
            _ => TimeSpan.FromMinutes(account.SyncIntervalMinutes),
        };

        var elapsed = now - lastSync;

        // Relógio para trás — fuso, NTP, hibernação — deixaria a conta esperando o tempo
        // andar de novo. Tratar como vencida é o comportamento seguro.
        if (elapsed < TimeSpan.Zero || elapsed >= interval)
        {
            return new SyncDecision(SyncAction.SyncNow, TimeSpan.Zero, "O intervalo de sincronização venceu.");
        }

        return new SyncDecision(SyncAction.Wait, interval - elapsed, "Sincronizada recentemente.");
    }

    /// <summary>
    /// Espera crescente depois de erro, com teto.
    /// </summary>
    /// <remarks>
    /// O intervalo configurado pelo usuário é o piso: uma conta que já falha não deve ser
    /// consultada com mais frequência do que uma saudável.
    /// </remarks>
    private static TimeSpan ErrorBackoff(Account account)
    {
        var configured = TimeSpan.FromMinutes(account.SyncIntervalMinutes);
        var doubled = configured * 2;

        return doubled > MaxRetryDelay ? MaxRetryDelay : doubled;
    }

    /// <summary>
    /// Indica se vale manter uma espera passiva por IDLE em vez de sondar.
    /// </summary>
    /// <remarks>
    /// Só para a conta em dia: com falha pendente ou fila acumulada, o que se quer é voltar
    /// a sincronizar, não ficar parado esperando o servidor avisar de algo novo.
    /// </remarks>
    public static bool ShouldIdle(Account account, bool serverSupportsIdle)
    {
        ArgumentNullException.ThrowIfNull(account);

        return serverSupportsIdle
            && account.IsActive
            && account.SyncStatus == AccountSyncStatus.Online;
    }
}
