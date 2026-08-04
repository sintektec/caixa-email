using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.ValueObjects;

/// <summary>
/// Value object representing an e-mail domain (the part after '@').
/// Always stored in lowercase, trimmed. Comparison is ordinal exact.
/// </summary>
public sealed record EmailDomain
{
    public string Value { get; }

    private EmailDomain(string value)
    {
        Value = value;
    }

    public static EmailDomain Parse(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidEmailDomainException("Domain cannot be null or empty.");

        var normalized = domain.Trim().ToLowerInvariant();

        if (normalized.Length == 0)
            throw new InvalidEmailDomainException("Domain cannot be empty after normalization.");

        if (normalized.Contains('@'))
            throw new InvalidEmailDomainException($"Domain cannot contain '@': '{domain}'.");

        return new EmailDomain(normalized);
    }

    public static bool TryParse(string domain, out EmailDomain? result)
    {
        result = null;
        try
        {
            result = Parse(domain);
            return true;
        }
        catch (InvalidEmailDomainException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if this domain is a subdomain of the given parent domain.
    /// E.g., "vendas.empresa.com" is a subdomain of "empresa.com".
    /// </summary>
    public bool IsSubdomainOf(EmailDomain parent)
    {
        if (Value == parent.Value)
            return false;

        return Value.EndsWith('.' + parent.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks exact match (ordinal) or subdomain match if allowSubdomains is true.
    /// </summary>
    public bool Matches(EmailDomain other, bool allowSubdomains = false)
    {
        if (Value == other.Value)
            return true;

        return allowSubdomains && IsSubdomainOf(other);
    }

    public override string ToString() => Value;
}
