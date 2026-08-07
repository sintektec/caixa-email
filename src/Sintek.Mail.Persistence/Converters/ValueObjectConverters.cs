using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Persistence.Converters;

/// <summary>
/// Conversores que gravam os value objects de domínio como texto e os reconstroem na
/// leitura.
/// </summary>
/// <remarks>
/// Manter <see cref="EmailDomain"/> e <see cref="EmailAddress"/> como tipos próprios nas
/// entidades — em vez de <see cref="string"/> — é o que impede uma comparação de domínio
/// feita por engano com caixa ou espaços diferentes. O custo é este par de conversores.
///
/// Os valores gravados já estão normalizados pelos próprios value objects, então a
/// comparação em SQL (que é ordinal no SQLite para colunas TEXT sem COLLATE NOCASE)
/// coincide com a comparação em memória.
/// </remarks>
public static class ValueObjectConverters
{
    /// <summary>Converte <see cref="EmailDomain"/> para texto e de volta.</summary>
    public static readonly ValueConverter<EmailDomain, string> EmailDomainConverter = new(
        domain => domain.Value,
        value => EmailDomain.Parse(value));

    /// <summary>Converte <see cref="EmailDomain"/> opcional.</summary>
    public static readonly ValueConverter<EmailDomain?, string?> NullableEmailDomainConverter = new(
        domain => domain == null ? null : domain.Value,
        value => value == null ? null : EmailDomain.Parse(value));

    /// <summary>Converte <see cref="EmailAddress"/> para texto e de volta.</summary>
    public static readonly ValueConverter<EmailAddress, string> EmailAddressConverter = new(
        address => address.Value,
        value => EmailAddress.Parse(value));

    /// <summary>Converte <see cref="EmailAddress"/> opcional.</summary>
    public static readonly ValueConverter<EmailAddress?, string?> NullableEmailAddressConverter = new(
        address => address == null ? null : address.Value,
        value => value == null ? null : EmailAddress.Parse(value));

    /// <summary>
    /// Comparador de <see cref="EmailDomain"/> para o rastreamento de alterações.
    /// </summary>
    /// <remarks>
    /// Sem um comparador explícito, o EF Core usaria igualdade por referência e
    /// consideraria alterada toda entidade cujo value object foi reconstruído na leitura,
    /// gerando UPDATEs desnecessários a cada consulta.
    /// </remarks>
    public static readonly ValueComparer<EmailDomain> EmailDomainComparer = new(
        (left, right) => left != null && right != null ? left.Equals(right) : left == right,
        domain => domain.GetHashCode(),
        domain => EmailDomain.Parse(domain.Value));

    /// <summary>Comparador de <see cref="EmailAddress"/>.</summary>
    public static readonly ValueComparer<EmailAddress> EmailAddressComparer = new(
        (left, right) => left != null && right != null ? left.Equals(right) : left == right,
        address => address.GetHashCode(),
        address => EmailAddress.Parse(address.Value));
}
