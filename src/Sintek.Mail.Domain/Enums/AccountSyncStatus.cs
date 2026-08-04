namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Current synchronization status of an account.
/// </summary>
public enum AccountSyncStatus
{
    Offline,
    Online,
    Syncing,
    Error
}
