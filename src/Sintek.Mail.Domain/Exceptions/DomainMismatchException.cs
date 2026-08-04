namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Thrown when an account or message does not belong to the expected domain.
/// </summary>
public sealed class DomainMismatchException : DomainException
{
    public string ExpectedDomain { get; }
    public string ActualDomain { get; }

    public DomainMismatchException(string expectedDomain, string actualDomain)
        : base($"Domain mismatch: expected '{expectedDomain}', but got '{actualDomain}'.")
    {
        ExpectedDomain = expectedDomain;
        ActualDomain = actualDomain;
    }

    public DomainMismatchException(string expectedDomain, string actualDomain, string message)
        : base(message)
    {
        ExpectedDomain = expectedDomain;
        ActualDomain = actualDomain;
    }
}
