using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Persistence.Repositories;

public sealed class SyncQueue : ISyncQueue
{
    private readonly MailDbContext _context;

    public SyncQueue(MailDbContext context)
    {
        _context = context;
    }

    public async Task EnqueueAsync(OutboxOperation operation, CancellationToken ct = default)
    {
        // Get next sequence number
        var maxSeq = await _context.OutboxOperations
            .Where(o => o.AccountId == operation.AccountId)
            .MaxAsync(o => (long?)o.Sequence, ct) ?? 0;

        operation.Sequence = maxSeq + 1;
        operation.Status = OutboxOperationStatus.Pending;
        operation.CreatedAt = DateTime.UtcNow;

        await _context.OutboxOperations.AddAsync(operation, ct);
    }

    public async Task<OutboxOperation?> DequeueAsync(Guid accountId, CancellationToken ct = default)
    {
        return await _context.OutboxOperations
            .Where(o => o.AccountId == accountId && o.Status == OutboxOperationStatus.Pending)
            .OrderBy(o => o.Sequence)
            .FirstOrDefaultAsync(ct);
    }

    public async Task CompleteAsync(Guid operationId, CancellationToken ct = default)
    {
        var op = await _context.OutboxOperations.FindAsync(new object[] { operationId }, ct);
        if (op is not null)
        {
            op.Status = OutboxOperationStatus.Completed;
            op.UpdatedAt = DateTime.UtcNow;
            _context.OutboxOperations.Update(op);
        }
    }

    public async Task FailAsync(Guid operationId, string error, CancellationToken ct = default)
    {
        var op = await _context.OutboxOperations.FindAsync(new object[] { operationId }, ct);
        if (op is not null)
        {
            op.Status = OutboxOperationStatus.Failed;
            op.LastError = error;
            op.AttemptCount++;
            op.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, op.AttemptCount)); // Exponential backoff
            op.UpdatedAt = DateTime.UtcNow;
            _context.OutboxOperations.Update(op);
        }
    }

    public async Task<int> GetPendingCountAsync(Guid accountId, CancellationToken ct = default)
    {
        return await _context.OutboxOperations
            .CountAsync(o => o.AccountId == accountId && o.Status == OutboxOperationStatus.Pending, ct);
    }

    public async Task RetryFailedAsync(Guid accountId, CancellationToken ct = default)
    {
        var failed = await _context.OutboxOperations
            .Where(o => o.AccountId == accountId && o.Status == OutboxOperationStatus.Failed)
            .ToListAsync(ct);

        foreach (var op in failed)
        {
            op.Status = OutboxOperationStatus.Pending;
            op.NextAttemptAt = null;
            op.UpdatedAt = DateTime.UtcNow;
            _context.OutboxOperations.Update(op);
        }
    }
}
