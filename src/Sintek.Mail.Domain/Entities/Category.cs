namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A colored category that can be applied to messages.
/// </summary>
public sealed class Category : Entity
{
    public string Name { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public string? Shortcut { get; set; }

    // Navigation
    public ICollection<MessageCategory> MessageCategories { get; set; } = new List<MessageCategory>();
}
