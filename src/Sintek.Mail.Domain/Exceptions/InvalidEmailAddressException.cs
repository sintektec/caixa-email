namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Thrown when an e-mail address cannot be parsed.
/// </summary>
public sealed class InvalidEmailAddressException : DomainException
{
    public InvalidEmailAddressException(string message) : base(message) { }
}
