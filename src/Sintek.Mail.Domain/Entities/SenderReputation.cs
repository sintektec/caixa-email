using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Entrada das listas de remetentes: bloqueado ou confiável, por endereço exato ou por
/// domínio inteiro.
/// </summary>
/// <remarks>
/// <para>
/// Bloqueado vai direto para o lixo eletrônico na chegada; confiável libera o conteúdo
/// remoto sem perguntar. Nenhuma das duas listas é um classificador de spam — o veredito
/// de spam continua sendo o do servidor, por decisão registrada no roadmap.
/// </para>
/// <para>
/// Exatamente um dos alvos é preenchido: <see cref="Address"/> ou <see cref="Domain"/>.
/// Uma entrada com os dois seria ambígua; sem nenhum, não diria nada.
/// </para>
/// </remarks>
public sealed class SenderReputation : Entity
{
    private SenderReputation(Guid id, SenderReputationKind kind, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Kind = kind;
    }

    private SenderReputation()
    {
    }

    /// <summary>Se a entrada bloqueia ou confia.</summary>
    public SenderReputationKind Kind { get; private set; }

    /// <summary>Endereço exato, quando a entrada mira um remetente específico.</summary>
    public EmailAddress? Address { get; private set; }

    /// <summary>Domínio inteiro, quando a entrada mira todos os remetentes dele.</summary>
    public EmailDomain? Domain { get; private set; }

    /// <summary>Conta à qual a entrada se aplica. Nulo significa todas.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>Cria uma entrada por endereço exato.</summary>
    public static SenderReputation ForAddress(
        SenderReputationKind kind,
        EmailAddress address,
        DateTimeOffset createdAt,
        Guid? accountId = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new SenderReputation(id ?? Guid.CreateVersion7(), kind, createdAt)
        {
            Address = address,
            AccountId = accountId,
        };
    }

    /// <summary>Cria uma entrada por domínio.</summary>
    public static SenderReputation ForDomain(
        SenderReputationKind kind,
        EmailDomain domain,
        DateTimeOffset createdAt,
        Guid? accountId = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(domain);

        return new SenderReputation(id ?? Guid.CreateVersion7(), kind, createdAt)
        {
            Domain = domain,
            AccountId = accountId,
        };
    }

    /// <summary>O alvo da entrada, para exibição na lista.</summary>
    public string Target => Address?.Value ?? Domain?.Value ?? string.Empty;

    /// <summary>
    /// Decide se a entrada alcança o remetente.
    /// </summary>
    /// <remarks>
    /// Entrada por domínio cobre os subdomínios: quem bloqueia "promo.com" espera que
    /// "mail.promo.com" caia junto — é assim que os remetentes de massa se espalham.
    /// </remarks>
    public bool AppliesTo(EmailAddress sender, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(sender);

        if (AccountId is { } scoped && scoped != accountId)
        {
            return false;
        }

        if (Address is not null)
        {
            return Address == sender;
        }

        return Domain is not null && sender.Domain.IsSameOrSubdomainOf(Domain, allowSubdomains: true);
    }
}
