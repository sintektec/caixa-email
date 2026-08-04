namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Kind of e-mail address in a message.
/// </summary>
public enum AddressKind
{
    From,
    To,
    Cc,
    Bcc,
    ReplyTo
}
