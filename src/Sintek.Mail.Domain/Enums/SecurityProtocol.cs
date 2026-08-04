namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Security protocol for IMAP/SMTP connections.
/// </summary>
public enum SecurityProtocol
{
    None,
    Ssl,
    StartTls,
    Auto
}
