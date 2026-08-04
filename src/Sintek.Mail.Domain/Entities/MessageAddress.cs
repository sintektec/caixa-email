using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An e-mail address associated with a message (From, To, Cc, Bcc, ReplyTo).
/// The Domain field is persisted in lowercase and indexed for fast domain validation queries.
/// </summary>
public sealed class MessageAddress : Entity
{
    public Guid MessageId { get; set; }
    public AddressKind Kind { get; set; }
    public string Address { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Domain { get; set; } = string.Empty; // lowercase, indexed

    // Navigation
    public Message Message { get; set; } = null!;
}
