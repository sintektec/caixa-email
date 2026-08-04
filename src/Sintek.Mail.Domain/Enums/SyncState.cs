namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Synchronization state of a message or entity.
/// </summary>
public enum SyncState
{
    /// <summary>Fully synchronized with the server.</summary>
    Synced,

    /// <summary>Created or modified locally, pending sync.</summary>
    PendingCreate,

    /// <summary>Modified locally, pending sync.</summary>
    PendingUpdate,

    /// <summary>Deleted locally, pending sync.</summary>
    PendingDelete,

    /// <summary>Sync failed, will retry.</summary>
    SyncFailed,

    /// <summary>Conflict detected, needs resolution.</summary>
    Conflict
}
