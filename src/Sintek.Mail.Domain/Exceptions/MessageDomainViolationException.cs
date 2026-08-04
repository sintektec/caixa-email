namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Thrown when a message cannot be moved to a domain-restricted folder.
/// The message text matches the spec exactly.
/// </summary>
public sealed class MessageDomainViolationException : DomainException
{
    public const string SpecMessage = "Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local.";

    public MessageDomainViolationException() : base(SpecMessage) { }
    public MessageDomainViolationException(string message) : base(message) { }
}
