namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Type of action a rule can perform.
/// </summary>
public enum RuleActionType
{
    MoveToFolder,
    CopyToFolder,
    ApplyCategory,
    MarkAsRead,
    Flag,
    Delete,
    MoveToPending,
    Forward
}
