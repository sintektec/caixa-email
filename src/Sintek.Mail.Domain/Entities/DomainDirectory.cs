using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Diretório de Domínio: a pasta raiz lógica que representa um domínio de e-mail e
/// agrupa as contas que pertencem a ele.
/// </summary>
/// <remarks>
/// É o agregado central do produto. Nenhuma conta entra em um diretório sem passar por
/// <see cref="ValidateAccount"/>, e nenhuma mensagem entra em uma pasta restrita sem
/// passar pela avaliação de pertencimento configurada aqui.
/// </remarks>
public sealed class DomainDirectory : Entity
{
    private readonly List<DomainAlias> _aliases = [];
    private readonly List<Account> _accounts = [];

    private DomainDirectory(
        Guid id,
        EmailDomain domainName,
        string? description,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        DomainName = domainName;
        Description = description;
    }

    private DomainDirectory()
    {
    }

    /// <summary>Domínio representado pelo diretório, normalizado.</summary>
    public EmailDomain DomainName { get; private set; } = null!;

    /// <summary>Descrição livre, exibida na árvore de navegação.</summary>
    public string? Description { get; private set; }

    /// <summary>Quais participantes contam ao decidir se uma mensagem pertence ao domínio.</summary>
    public DomainValidationMode ValidationMode { get; private set; } = DomainValidationMode.AnyParticipant;

    /// <summary>O que fazer com uma mensagem incompatível em uma pasta restrita.</summary>
    public InvalidEmailAction InvalidEmailAction { get; private set; } = InvalidEmailAction.Block;

    /// <summary>
    /// Se contas e mensagens de subdomínios são aceitas. Falso por padrão, como manda a
    /// especificação.
    /// </summary>
    public bool AllowSubdomains { get; private set; }

    /// <summary>Se o diretório está ativo. Diretórios inativos não sincronizam.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Marcado como favorito na árvore de navegação.</summary>
    public bool IsFavorite { get; private set; }

    /// <summary>Posição manual na árvore de navegação.</summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Domínios adicionais aceitos por este diretório, além de <see cref="DomainName"/>.
    /// Atende o caso da especificação em que "o domínio está registrado como domínio
    /// adicional permitido" — útil quando uma empresa opera também sob outro domínio.
    /// </summary>
    public IReadOnlyCollection<DomainAlias> Aliases => _aliases;

    /// <summary>Contas vinculadas a este diretório.</summary>
    public IReadOnlyCollection<Account> Accounts => _accounts;

    /// <summary>Cria um novo Diretório de Domínio.</summary>
    public static DomainDirectory Create(
        EmailDomain domainName,
        DateTimeOffset createdAt,
        string? description = null,
        DomainValidationMode validationMode = DomainValidationMode.AnyParticipant,
        InvalidEmailAction invalidEmailAction = InvalidEmailAction.Block,
        bool allowSubdomains = false,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(domainName);

        return new DomainDirectory(id ?? Guid.CreateVersion7(), domainName, description, createdAt)
        {
            ValidationMode = validationMode,
            InvalidEmailAction = invalidEmailAction,
            AllowSubdomains = allowSubdomains,
        };
    }

    /// <summary>
    /// Verifica se <paramref name="address"/> pode ser vinculado a este diretório.
    /// </summary>
    /// <remarks>
    /// A comparação é exata e ordinal contra <see cref="DomainName"/> e contra os
    /// <see cref="Aliases"/>. Subdomínios só passam quando <see cref="AllowSubdomains"/>
    /// está habilitado.
    /// </remarks>
    public bool Accepts(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return AcceptsDomain(address.Domain);
    }

    /// <summary>Verifica se um domínio pertence a este diretório.</summary>
    public bool AcceptsDomain(EmailDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (domain.IsSameOrSubdomainOf(DomainName, AllowSubdomains))
        {
            return true;
        }

        foreach (var alias in _aliases)
        {
            if (domain.IsSameOrSubdomainOf(alias.DomainName, AllowSubdomains))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Valida <paramref name="address"/> contra este diretório, lançando quando não
    /// pertence.
    /// </summary>
    /// <exception cref="DomainMismatchException">O domínio da conta difere do diretório.</exception>
    public void ValidateAccount(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!Accepts(address))
        {
            throw new DomainMismatchException(address, DomainName, AllowSubdomains);
        }
    }

    /// <summary>Vincula uma conta, validando o domínio antes.</summary>
    /// <exception cref="DomainMismatchException">O domínio da conta difere do diretório.</exception>
    public void AttachAccount(Account account, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);

        ValidateAccount(account.EmailAddress);

        if (_accounts.Any(a => a.Id == account.Id))
        {
            return;
        }

        _accounts.Add(account);
        account.AssignToDomain(this, now);
        Touch(now);
    }

    /// <summary>Registra um domínio adicional aceito por este diretório.</summary>
    public DomainAlias AddAlias(EmailDomain domain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var existing = _aliases.FirstOrDefault(a => a.DomainName.Equals(domain));
        if (existing is not null)
        {
            return existing;
        }

        var alias = DomainAlias.Create(Id, domain, now);
        _aliases.Add(alias);
        Touch(now);
        return alias;
    }

    /// <summary>Remove um domínio adicional.</summary>
    public bool RemoveAlias(EmailDomain domain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var alias = _aliases.FirstOrDefault(a => a.DomainName.Equals(domain));
        if (alias is null)
        {
            return false;
        }

        _aliases.Remove(alias);
        Touch(now);
        return true;
    }

    /// <summary>
    /// Troca o domínio do diretório.
    /// </summary>
    /// <remarks>
    /// Alterar o domínio invalida potencialmente contas e mensagens já vinculadas. Este
    /// método só executa a troca: a revalidação, o relatório de incompatíveis, a
    /// confirmação do usuário e o registro em auditoria são orquestrados pela camada de
    /// Aplicação, que tem acesso às mensagens. O domínio sozinho não consegue — e não
    /// deve — varrer o banco.
    /// </remarks>
    public void ChangeDomainName(EmailDomain newDomainName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newDomainName);

        if (DomainName.Equals(newDomainName))
        {
            return;
        }

        DomainName = newDomainName;
        Touch(now);
    }

    /// <summary>Ajusta as regras de validação do diretório.</summary>
    public void UpdateRules(
        DomainValidationMode validationMode,
        InvalidEmailAction invalidEmailAction,
        bool allowSubdomains,
        DateTimeOffset now)
    {
        ValidationMode = validationMode;
        InvalidEmailAction = invalidEmailAction;
        AllowSubdomains = allowSubdomains;
        Touch(now);
    }

    /// <summary>Atualiza os metadados de exibição.</summary>
    public void UpdateDisplay(string? description, bool isFavorite, int sortOrder, DateTimeOffset now)
    {
        Description = description;
        IsFavorite = isFavorite;
        SortOrder = sortOrder;
        Touch(now);
    }

    /// <summary>Ativa ou desativa o diretório.</summary>
    public void SetActive(bool isActive, DateTimeOffset now)
    {
        IsActive = isActive;
        Touch(now);
    }
}
