namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Type of operation in the outbox sync queue.
/// </summary>
public enum OutboxOperationType
{
    SendMessage,
    MoveMessage,
    CopyMessage,
    DeleteMessage,
    MarkRead,
    MarkUnread,
    FlagMessage,
    UnflagMessage,
    CreateFolder,
    DeleteFolder,
    RenameFolder,
    UpdateAccount,
    SyncFolder
}
