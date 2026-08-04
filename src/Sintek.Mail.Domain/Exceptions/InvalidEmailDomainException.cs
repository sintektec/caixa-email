namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Thrown when an e-mail domain cannot be parsed.
/// </summary>
public sealed class InvalidEmailDomainException : DomainException
{
    public InvalidEmailDomainException(string message) : base(message) { }
}
