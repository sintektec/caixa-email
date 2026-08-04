namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Standard folder types plus custom and pending.
/// </summary>
public enum FolderType
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    Custom,

    /// <summary>Folder for messages that failed domain validation.</summary>
    Pending
}
