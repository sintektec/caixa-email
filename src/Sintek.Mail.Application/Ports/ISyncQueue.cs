using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Ports;

/// <summary>
/// Queue for offline-first synchronization operations.
/// </summary>
public interface ISyncQueue
{
    /// <summary>Enqueues an operation for later sync.</summary>
    Task EnqueueAsync(OutboxOperation operation, CancellationToken ct = default);

    /// <summary>Dequeues the next pending operation for an account.</summary>
    Task<OutboxOperation?> DequeueAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Marks an operation as completed.</summary>
    Task CompleteAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Marks an operation as failed with error details.</summary>
    Task FailAsync(Guid operationId, string error, CancellationToken ct = default);

    /// <summary>Gets the count of pending operations for an account.</summary>
    Task<int> GetPendingCountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Retries all failed operations for an account.</summary>
    Task RetryFailedAsync(Guid accountId, CancellationToken ct = default);
}
