namespace Sintek.Mail.Domain.ValueObjects;

/// <summary>
/// Endereço de e-mail normalizado, com o domínio já extraído e validado.
/// </summary>
/// <remarks>
/// A parte local é preservada como digitada (a RFC 5321 a define como sensível à caixa),
/// mas a comparação entre endereços ignora a caixa: nenhum provedor real trata
/// <c>Contato@</c> e <c>contato@</c> como caixas distintas, e tratá-las como distintas
/// aqui faria a mesma conta ser cadastrada duas vezes.
/// </remarks>
public sealed class EmailAddress : IEquatable<EmailAddress>
{
    private EmailAddress(string localPart, EmailDomain domain)
    {
        LocalPart = localPart;
        Domain = domain;
    }

    /// <summary>Parte antes do '@', preservada como informada.</summary>
    public string LocalPart { get; }

    /// <summary>Domínio do endereço, já normalizado.</summary>
    public EmailDomain Domain { get; }

    /// <summary>Endereço completo, com o domínio em minúsculas.</summary>
    public string Value => $"{LocalPart}@{Domain.Value}";

    /// <summary>
    /// Normaliza e valida <paramref name="raw"/>, lançando quando o valor não é um
    /// endereço utilizável.
    /// </summary>
    /// <exception cref="ArgumentException">O valor é nulo, vazio ou malformado.</exception>
    public static EmailAddress Parse(string? raw)
    {
        if (!TryParse(raw, out var address, out var error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return address;
    }

    /// <summary>Tenta normalizar e validar <paramref name="raw"/> sem lançar exceção.</summary>
    public static bool TryParse(string? raw, out EmailAddress address)
        => TryParse(raw, out address, out _);

    /// <summary>
    /// Tenta normalizar e validar <paramref name="raw"/>, devolvendo a razão da recusa
    /// quando falha.
    /// </summary>
    public static bool TryParse(string? raw, out EmailAddress address, out string? error)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "O endereço de e-mail não pode ser vazio.";
            return false;
        }

        var trimmed = raw.Trim();

        // Separamos pelo ÚLTIMO '@': a parte local pode conter '@' quando vem entre
        // aspas (RFC 5321), e nesse caso só o último separa local de domínio.
        var separator = trimmed.LastIndexOf('@');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            error = $"'{trimmed}' não é um endereço de e-mail válido: use o formato usuario@dominio.";
            return false;
        }

        var localPart = trimmed[..separator];
        if (localPart.AsSpan().ContainsAny(' ', '\t'))
        {
            error = "A parte local do endereço não pode conter espaços.";
            return false;
        }

        if (!EmailDomain.TryParse(trimmed[(separator + 1)..], out var domain, out error))
        {
            return false;
        }

        address = new EmailAddress(localPart, domain);
        error = null;
        return true;
    }

    public bool Equals(EmailAddress? other)
        => other is not null
            && Domain.Equals(other.Domain)
            && string.Equals(LocalPart, other.LocalPart, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as EmailAddress);

    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(LocalPart), Domain);

    public override string ToString() => Value;

    public static bool operator ==(EmailAddress? left, EmailAddress? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EmailAddress? left, EmailAddress? right) => !(left == right);
}
