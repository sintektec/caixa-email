namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Defines how messages are validated against a domain-restricted folder.
/// </summary>
public enum ValidationMode
{
    /// <summary>Validate only the sender.</summary>
    SenderOnly,

    /// <summary>Validate only recipients (To, Cc, Bcc).</summary>
    RecipientOnly,

    /// <summary>Accept if sender OR any recipient matches.</summary>
    SenderOrRecipient,

    /// <summary>Require both sender AND at least one recipient to match.</summary>
    SenderAndRecipient,

    /// <summary>Accept if any participant (From, To, Cc, Bcc, ReplyTo) matches.</summary>
    AnyParticipant
}
