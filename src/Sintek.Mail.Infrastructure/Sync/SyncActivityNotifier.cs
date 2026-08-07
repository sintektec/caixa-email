using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Sync;

namespace Sintek.Mail.Infrastructure.Sync;

/// <summary>
/// Notificador em processo, um só para toda a aplicação.
/// </summary>
/// <remarks>
/// Singleton porque as duas pontas são de vida longa: o laço de sincronização e a janela.
/// Registrado com escopo, cada uma veria uma instância diferente e o aviso nunca chegaria —
/// sem erro nenhum, que é o pior jeito de essa ligação falhar.
/// </remarks>
public sealed class SyncActivityNotifier : ISyncActivityNotifier
{
    private readonly ILogger<SyncActivityNotifier> _logger;

    public SyncActivityNotifier(ILogger<SyncActivityNotifier> logger) => _logger = logger;

    /// <inheritdoc />
    public event EventHandler? CycleCompleted;

    /// <inheritdoc />
    /// <remarks>
    /// A exceção do assinante é capturada aqui e não sobe. Quem chama é o laço de
    /// sincronização, e deixá-la escapar faria uma falha de <i>redesenho</i> derrubar a
    /// volta inteira — a correspondência pararia de chegar por causa de um defeito de tela.
    /// </remarks>
    public void NotifyCycleCompleted()
    {
        try
        {
            CycleCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Um assinante do aviso de sincronização falhou.");
        }
    }
}
