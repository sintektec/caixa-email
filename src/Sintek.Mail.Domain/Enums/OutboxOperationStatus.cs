namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Status of an outbox operation in the sync queue.
/// </summary>
public enum OutboxOperationStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Cancelled
}
