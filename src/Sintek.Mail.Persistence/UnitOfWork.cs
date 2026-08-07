using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Abstractions.Persistence;

namespace Sintek.Mail.Persistence;

/// <summary>Transação sobre o <see cref="MailDbContext"/>.</summary>
/// <remarks>
/// A reentrância é tratada de propósito: vários casos de uso chamam
/// <see cref="ExecuteInTransactionAsync{TResult}"/> e, dentro dele, invocam outro que faz
/// o mesmo. Sem a verificação de transação corrente, a segunda chamada falharia — o
/// SQLite não tem transações aninhadas.
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly MailDbContext _context;

    public UnitOfWork(MailDbContext context) => _context = context;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// O conflito de concorrência é traduzido para <see cref="ConcurrentModificationException"/>
    /// aqui, na fronteira. A camada de Aplicação precisa tratá-lo — o painel de leitura passa
    /// segundos na rede entre carregar a mensagem e gravar o corpo, e o laço de sincronização
    /// escreve nas mesmas linhas nesse intervalo — e ela não conhece o EF Core, nem deve.
    /// </para>
    /// <para>
    /// A tradução preserva a exceção original como <c>InnerException</c>: o diagnóstico
    /// completo continua disponível em log, e o que sobe é um tipo que a Aplicação sabe
    /// nomear.
    /// </para>
    /// </remarks>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrentModificationException(
                "O registro foi alterado ou removido por outra operação enquanto esta era feita.",
                ex);
        }
    }

    /// <inheritdoc />
    public void DiscardPendingChanges() => _context.ChangeTracker.Clear();

    /// <inheritdoc />
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Já dentro de uma transação: participamos dela em vez de abrir outra.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteInTransactionAsync<object?>(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }
}
