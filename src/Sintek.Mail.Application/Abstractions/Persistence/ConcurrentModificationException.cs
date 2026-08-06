namespace Sintek.Mail.Application.Abstractions.Persistence;

/// <summary>
/// A gravação encontrou a linha alterada ou removida por outra operação.
/// </summary>
/// <remarks>
/// <para>
/// Existe para que a camada de Aplicação possa tratar o conflito <b>sem conhecer o EF Core</b>.
/// A exceção original é <c>DbUpdateConcurrencyException</c>, do provedor relacional, e
/// capturá-la aqui exigiria uma referência que esta camada não tem nem deve ter.
/// </para>
/// <para>
/// Não é hipótese remota: o painel de leitura carrega a mensagem, passa segundos na rede
/// conectando e baixando o corpo, e só então grava. Nessa janela o laço de sincronização —
/// que roda em escopo próprio, com outro contexto — escreve nas mesmas linhas. Sem tratamento,
/// o conflito subia pelo manipulador <c>async void</c> da seleção de mensagem e
/// <b>derrubava a aplicação</b>: um clique fechava o programa (D-041).
/// </para>
/// </remarks>
public sealed class ConcurrentModificationException : Exception
{
    public ConcurrentModificationException()
        : this("O registro foi alterado ou removido por outra operação.")
    {
    }

    public ConcurrentModificationException(string message)
        : base(message)
    {
    }

    public ConcurrentModificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
