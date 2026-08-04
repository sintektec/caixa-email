namespace Sintek.Mail.Application.Abstractions.Persistence;

/// <summary>
/// Delimita a transação local.
/// </summary>
/// <remarks>
/// Existe por causa de uma exigência do modo offline-first: gravar o efeito da ação e
/// enfileirar a operação de sincronização precisam acontecer <b>na mesma transação</b>.
/// Se as duas não forem atômicas, uma queda de energia entre elas produz um estado em
/// que a interface mostra a mensagem movida e o servidor jamais fica sabendo — ou o
/// contrário.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Persiste as alterações pendentes.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa <paramref name="operation"/> dentro de uma transação, confirmando ao final
    /// ou desfazendo tudo se algo falhar.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Executa <paramref name="operation"/> dentro de uma transação.</summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}
