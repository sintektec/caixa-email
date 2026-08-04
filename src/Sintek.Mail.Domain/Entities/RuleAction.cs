using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An action within a rule.
/// </summary>
public sealed class RuleAction : Entity
{
    public Guid RuleId { get; set; }
    public RuleActionType ActionType { get; set; }
    public string? TargetFolderId { get; set; }
    public string? CategoryName { get; set; }
    public string? ForwardTo { get; set; }

    // Navigation
    public Rule Rule { get; set; } = null!;
}
