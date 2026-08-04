namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Application-wide settings stored as key-value pairs.
/// </summary>
public sealed class AppSettings : Entity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
