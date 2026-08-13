using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.ValueObjects;

/// <summary>
/// Value object representing an e-mail address.
/// Parses by the last '@', normalizes the local part to UPPERCASE and the
/// domain to lowercase, trims whitespace.
/// </summary>
/// <remarks>
/// A normalizacao da parte local existe para que a igualdade do record seja
/// insensivel a caixa: sem ela <c>USER@example.com</c> e <c>user@example.com</c>
/// eram dois valores distintos, e o mesmo endereco viraria duas entidades ao
/// deduplicar contas ou destinatarios. Desvio consciente da RFC 5321, que
/// declara a parte local sensivel a caixa -- ver D-009.
/// </remarks>
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

        var localPart = trimmed[..atIndex].Trim().ToUpperInvariant();
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
        // DomainException, nao InvalidEmailAddressException: Parse delega a
        // EmailDomain.Parse, que lanca InvalidEmailDomainException.
        //
        // Hoje esse caminho e inalcancavel -- toda entrada que produziria
        // dominio vazio ja cai na checagem de formato acima, e o dominio nunca
        // contem '@' porque a divisao usa o ULTIMO '@'. A captura larga e
        // defesa barata contra a fragilidade da captura estreita, nao correcao
        // de um bug observavel. Sem teste, porque nao ha entrada que o prove.
        catch (DomainException)
        {
            return false;
        }
    }

    public override string ToString() => FullAddress;
}
