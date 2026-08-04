namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Action to take when a message fails domain validation.
/// </summary>
public enum InvalidEmailAction
{
    /// <summary>Block the operation entirely.</summary>
    Block,

    /// <summary>Show a warning and ask for user confirmation.</summary>
    WarnAndConfirm,

    /// <summary>Move the message to a pending folder automatically.</summary>
    MoveToPending,

    /// <summary>Log the occurrence in the audit log only.</summary>
    LogOnly
}
