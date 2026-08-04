using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.DTOs;

public sealed record FolderDto(
    Guid Id,
    Guid AccountId,
    Guid? ParentFolderId,
    string Name,
    FolderType FolderType,
    string RemotePath,
    bool IsFavorite,
    bool IsDomainRestricted,
    Guid? RestrictedToDomainId,
    int UnreadCount,
    int TotalCount,
    bool SyncEnabled
);
