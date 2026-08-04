namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A condition within a rule.
/// </summary>
public sealed class RuleCondition : Entity
{
    public Guid RuleId { get; set; }
    public string Field { get; set; } = string.Empty; // e.g., "From", "Subject", "To"
    public string Operator { get; set; } = string.Empty; // e.g., "Contains", "Equals", "StartsWith"
    public string Value { get; set; } = string.Empty;

    // Navigation
    public Rule Rule { get; set; } = null!;
}
