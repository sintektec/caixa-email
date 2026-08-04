namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A reusable message template.
/// </summary>
public sealed class MessageTemplate : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? TextBody { get; set; }
}
