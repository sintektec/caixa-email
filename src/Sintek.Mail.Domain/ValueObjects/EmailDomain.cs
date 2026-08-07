namespace Sintek.Mail.Domain.ValueObjects;

/// <summary>
/// Nome de domínio de e-mail normalizado (por exemplo, <c>sintek.com.br</c>).
/// </summary>
/// <remarks>
/// Este tipo é a base de toda a regra de Diretório de Domínio. Ele existe para que seja
/// impossível comparar domínios por engano usando <see cref="string"/> crua, com
/// diferença de caixa ou espaços — a causa mais provável de um e-mail acabar no
/// diretório errado.
///
/// A normalização segue a especificação: remover espaços indevidos e converter para
/// letras minúsculas. A comparação é sempre ordinal e exata; subdomínios só entram
/// quando explicitamente permitidos (ver <see cref="IsSameOrSubdomainOf"/>).
/// </remarks>
public sealed class EmailDomain : IEquatable<EmailDomain>, IComparable<EmailDomain>
{
    /// <summary>Limite total de um nome de domínio, conforme RFC 1035.</summary>
    public const int MaxLength = 253;

    /// <summary>Limite de cada rótulo (o trecho entre dois pontos), conforme RFC 1035.</summary>
    public const int MaxLabelLength = 63;

    private EmailDomain(string value) => Value = value;

    /// <summary>Domínio já normalizado: sem espaços e em letras minúsculas.</summary>
    public string Value { get; }

    /// <summary>
    /// Normaliza e valida <paramref name="raw"/>, lançando quando o valor não é um
    /// domínio utilizável.
    /// </summary>
    /// <exception cref="ArgumentException">O valor é nulo, vazio ou malformado.</exception>
    public static EmailDomain Parse(string? raw)
    {
        if (!TryParse(raw, out var domain, out var error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return domain;
    }

    /// <summary>
    /// Tenta normalizar e validar <paramref name="raw"/> sem lançar exceção.
    /// </summary>
    public static bool TryParse(string? raw, out EmailDomain domain)
        => TryParse(raw, out domain, out _);

    /// <summary>
    /// Tenta normalizar e validar <paramref name="raw"/>, devolvendo a razão da recusa
    /// quando falha — usada para exibir mensagens de erro compreensíveis na interface.
    /// </summary>
    public static bool TryParse(string? raw, out EmailDomain domain, out string? error)
    {
        domain = null!;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "O domínio não pode ser vazio.";
            return false;
        }

        // Um domínio digitado pelo usuário costuma chegar com espaços nas pontas e, às
        // vezes, com o ponto final absoluto do DNS ("sintek.com.br."). Ambos são ruído.
        var normalized = raw.Trim().TrimEnd('.').ToLowerInvariant();

        if (normalized.Length == 0)
        {
            error = "O domínio não pode ser vazio.";
            return false;
        }

        if (normalized.Length > MaxLength)
        {
            error = $"O domínio excede o limite de {MaxLength} caracteres.";
            return false;
        }

        if (normalized.Contains('@', StringComparison.Ordinal))
        {
            error = "Informe apenas o domínio, sem a parte local do endereço nem o caractere '@'.";
            return false;
        }

        foreach (var label in normalized.Split('.'))
        {
            if (!IsValidLabel(label, out error))
            {
                return false;
            }
        }

        domain = new EmailDomain(normalized);
        error = null;
        return true;
    }

    private static bool IsValidLabel(string label, out string? error)
    {
        if (label.Length == 0)
        {
            error = "O domínio contém um trecho vazio entre pontos.";
            return false;
        }

        if (label.Length > MaxLabelLength)
        {
            error = $"O trecho '{label}' excede o limite de {MaxLabelLength} caracteres.";
            return false;
        }

        if (label[0] == '-' || label[^1] == '-')
        {
            error = $"O trecho '{label}' não pode começar nem terminar com hífen.";
            return false;
        }

        foreach (var c in label)
        {
            // Aceitamos apenas LDH (letras, dígitos, hífen). Domínios internacionalizados
            // devem ser convertidos para punycode (xn--...) antes de chegar aqui, o que
            // os mantém dentro deste conjunto.
            var isAllowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
            if (!isAllowed)
            {
                error = $"O trecho '{label}' contém o caractere inválido '{c}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Indica se este domínio é exatamente <paramref name="other"/> ou — quando
    /// <paramref name="allowSubdomains"/> é <see langword="true"/> — um subdomínio dele.
    /// </summary>
    /// <remarks>
    /// Por padrão a especificação bloqueia subdomínios: <c>usuario@vendas.empresa.com</c>
    /// NÃO pertence ao diretório <c>empresa.com</c> a menos que o diretório habilite
    /// explicitamente essa permissão.
    ///
    /// O sufixo é testado com o ponto incluído (<c>".empresa.com"</c>) de propósito: sem
    /// ele, <c>malempresa.com</c> passaria por subdomínio de <c>empresa.com</c>.
    /// </remarks>
    public bool IsSameOrSubdomainOf(EmailDomain other, bool allowSubdomains)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Equals(other))
        {
            return true;
        }

        return allowSubdomains
            && Value.EndsWith('.' + other.Value, StringComparison.Ordinal);
    }

    public bool Equals(EmailDomain? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EmailDomain);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(EmailDomain? other)
        => other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator ==(EmailDomain? left, EmailDomain? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EmailDomain? left, EmailDomain? right) => !(left == right);
}
