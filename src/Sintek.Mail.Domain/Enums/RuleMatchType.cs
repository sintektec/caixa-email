namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// How rule conditions are combined.
/// </summary>
public enum RuleMatchType
{
    /// <summary>All conditions must match.</summary>
    All,

    /// <summary>Any condition can match.</summary>
    Any
}
