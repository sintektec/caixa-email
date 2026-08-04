using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Domínio adicional aceito por um Diretório de Domínio, além do domínio principal.
/// </summary>
/// <remarks>
/// Atende ao caso previsto na especificação em que uma mensagem pertence ao domínio
/// porque "o domínio está registrado como domínio adicional permitido" — por exemplo,
/// uma empresa que também recebe por um domínio de marca antigo.
/// </remarks>
public sealed class DomainAlias : Entity
{
    private DomainAlias(Guid id, Guid domainDirectoryId, EmailDomain domainName, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        DomainDirectoryId = domainDirectoryId;
        DomainName = domainName;
    }

    private DomainAlias()
    {
    }

    /// <summary>Diretório ao qual este domínio adicional pertence.</summary>
    public Guid DomainDirectoryId { get; private set; }

    /// <summary>Domínio adicional aceito.</summary>
    public EmailDomain DomainName { get; private set; } = null!;

    /// <summary>Diretório dono deste alias.</summary>
    public DomainDirectory? DomainDirectory { get; private set; }

    internal static DomainAlias Create(Guid domainDirectoryId, EmailDomain domainName, DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), domainDirectoryId, domainName, createdAt);
}
