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

    /// <summary>
    /// Abandona tudo o que estava pendente de gravação.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe para o caminho de tratamento de falha. Quando um <c>SaveChanges</c> falha, o que
    /// causou a falha <b>continua pendente</b> — e a próxima gravação, mesmo de outra entidade
    /// e por outro motivo, arrasta a entrada ofensora junto e falha de novo.
    /// </para>
    /// <para>
    /// O efeito prático foi cruel: o <c>catch</c> que registra o motivo da falha na conta era
    /// ele próprio derrubado pela falha anterior. O motivo nunca era gravado, o log nem
    /// chegava a sair, e a interface mostrava "a última sincronização falhou" sem dizer por
    /// quê — exatamente a informação que existe para ser dita (D-048).
    /// </para>
    /// <para>
    /// <b>Só no tratamento de falha.</b> Usar isto para "resolver" um conflito no caminho
    /// normal descartaria trabalho do usuário sem ele saber.
    /// </para>
    /// </remarks>
    void DiscardPendingChanges();
}
