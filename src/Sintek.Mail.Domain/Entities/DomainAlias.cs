using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Additional domains allowed in a Domain Directory (spec 5.3).
/// </summary>
public sealed class DomainAlias : Entity
{
    public Guid DomainId { get; set; }
    public string DomainName { get; set; } = string.Empty;

    // Navigation
    public DomainDirectory Domain { get; set; } = null!;

    public EmailDomain GetEmailDomain() => EmailDomain.Parse(DomainName);
}
