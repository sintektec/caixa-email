namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Join table for Message-Category many-to-many relationship.
/// </summary>
public sealed class MessageCategory
{
    public Guid MessageId { get; set; }
    public Guid CategoryId { get; set; }

    // Navigation
    public Message Message { get; set; } = null!;
    public Category Category { get; set; } = null!;
}
