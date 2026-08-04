using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An automatic rule for organizing messages.
/// </summary>
public sealed class Rule : Entity
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public RuleMatchType MatchType { get; set; } = RuleMatchType.All;
    public bool StopProcessing { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
    public ICollection<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();
    public ICollection<RuleAction> Actions { get; set; } = new List<RuleAction>();
}
