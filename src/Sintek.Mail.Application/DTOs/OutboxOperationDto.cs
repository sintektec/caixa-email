using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.DTOs;

public sealed record OutboxOperationDto(
    Guid Id,
    Guid AccountId,
    OutboxOperationType OperationType,
    Guid EntityId,
    OutboxOperationStatus Status,
    int AttemptCount,
    DateTime? NextAttemptAt,
    string? LastError,
    long Sequence
);
