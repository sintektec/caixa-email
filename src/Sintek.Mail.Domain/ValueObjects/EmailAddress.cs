using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.ValueObjects;

/// <summary>
/// Value object representing an e-mail address.
/// Parses by the last '@', normalizes domain to lowercase, trims whitespace.
/// </summary>
public sealed record EmailAddress
{
    public string LocalPart { get; }
    public EmailDomain Domain { get; }
    public string FullAddress => $"{LocalPart}@{Domain.Value}";

    private EmailAddress(string localPart, EmailDomain domain)
    {
        LocalPart = localPart;
        Domain = domain;
    }

    public static EmailAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidEmailAddressException("E-mail address cannot be null or empty.");

        var trimmed = address.Trim();
        var atIndex = trimmed.LastIndexOf('@');

        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            throw new InvalidEmailAddressException($"Invalid e-mail address format: '{address}'.");

        var localPart = trimmed[..atIndex].Trim();
        var domainPart = trimmed[(atIndex + 1)..].Trim();

        if (localPart.Length == 0)
            throw new InvalidEmailAddressException($"Local part cannot be empty: '{address}'.");

        var domain = EmailDomain.Parse(domainPart);
        return new EmailAddress(localPart, domain);
    }

    public static bool TryParse(string address, out EmailAddress? result)
    {
        result = null;
        try
        {
            result = Parse(address);
            return true;
        }
        catch (InvalidEmailAddressException)
        {
            return false;
        }
    }

    public override string ToString() => FullAddress;
}
