namespace Sintek.Mail.Application.Abstractions.Sync;

/// <summary>
/// Avisa a interface de que o laço de sincronização terminou uma volta.
/// </summary>
/// <remarks>
/// <para>
/// O laço roda em segundo plano, no escopo dele, e escreve direto no banco. A interface lê o
/// banco quando alguém manda — e ninguém mandava. O resultado era que mensagem nova só
/// aparecia depois de o usuário clicar em sincronizar ou trocar de pasta, e <b>falha de conta
/// não aparecia nunca</b>: o motivo ficava gravado em <c>Account.LastSyncError</c>, sem
/// ninguém para lê-lo.
/// </para>
/// <para>
/// O contrato é deliberadamente magro — "algo mudou, releia" —, sem dizer o quê. Carregar o
/// que mudou obrigaria a decidir aqui o que a interface precisa, e cada tela nova mudaria esta
/// porta. Reler é barato: a árvore já é montada por consulta.
/// </para>
/// <para>
/// <b>O evento chega em thread de segundo plano.</b> Quem assina é responsável por levá-lo à
/// thread da interface — no WinUI, pelo <c>DispatcherQueue</c>. Colocar essa responsabilidade
/// aqui obrigaria esta camada a conhecer o despachante do WinUI, e ela é multiplataforma de
/// propósito.
/// </para>
/// </remarks>
public interface ISyncActivityNotifier
{
    /// <summary>Disparado ao fim de uma volta que alterou alguma coisa.</summary>
    event EventHandler? CycleCompleted;

    /// <summary>Anuncia o fim de uma volta.</summary>
    void NotifyCycleCompleted();
}
