using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An operation in the offline-first sync queue.
/// </summary>
public sealed class OutboxOperation : Entity
{
    public Guid AccountId { get; set; }
    public OutboxOperationType OperationType { get; set; }
    public Guid EntityId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public OutboxOperationStatus Status { get; set; } = OutboxOperationStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public long Sequence { get; set; }
    public Guid? DependsOnId { get; set; }

    // Navigation
    public Account Account { get; set; } = null!;
    public OutboxOperation? DependsOn { get; set; }
}
