using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.DTOs;

public sealed record MessageDto(
    Guid Id,
    Guid AccountId,
    Guid FolderId,
    Guid? ThreadId,
    string? MessageId,
    string Subject,
    string FromAddress,
    DateTime SentAt,
    DateTime ReceivedAt,
    string Preview,
    long Size,
    bool HasAttachments,
    bool IsRead,
    bool IsFlagged,
    Importance Importance,
    bool IsDraft,
    SyncState SyncState,
    DateTime? ScheduledSendAt,
    IReadOnlyList<MessageAddressDto> Addresses
);

public sealed record MessageAddressDto(
    AddressKind Kind,
    string Address,
    string? DisplayName
);
