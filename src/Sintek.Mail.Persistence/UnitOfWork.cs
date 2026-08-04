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
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);

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
