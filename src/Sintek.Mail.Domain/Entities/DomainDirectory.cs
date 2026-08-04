using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A Domain Directory is a logical root folder representing an e-mail domain.
/// It contains one or more accounts that must belong exactly to this domain.
/// </summary>
public sealed class DomainDirectory : Entity
{
    public string DomainName { get; private set; }
    public string? Description { get; set; }
    public ValidationMode ValidationMode { get; set; } = ValidationMode.SenderOrRecipient;
    public InvalidEmailAction InvalidEmailAction { get; set; } = InvalidEmailAction.Block;
    public bool AllowSubdomains { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsFavorite { get; set; }

    // Navigation
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<DomainAlias> Aliases { get; set; } = new List<DomainAlias>();

    private DomainDirectory() { DomainName = string.Empty; } // EF Core

    public DomainDirectory(string domainName, string? description = null)
    {
        DomainName = EmailDomain.Parse(domainName).Value;
        Description = description;
    }

    /// <summary>
    /// Validates that an account's e-mail domain matches this directory's domain.
    /// Throws DomainMismatchException if validation fails.
    /// </summary>
    public void ValidateAccount(EmailAddress emailAddress)
    {
        var accountDomain = emailAddress.Domain;
        var directoryDomain = EmailDomain.Parse(DomainName);

        if (!accountDomain.Matches(directoryDomain, AllowSubdomains))
        {
            throw new DomainMismatchException(DomainName, accountDomain.Value);
        }
    }

    /// <summary>
    /// Changes the domain name. Returns a list of accounts that would become incompatible.
    /// </summary>
    public IReadOnlyList<Account> GetIncompatibleAccounts(string newDomainName)
    {
        var newDomain = EmailDomain.Parse(newDomainName);
        var incompatible = new List<Account>();

        foreach (var account in Accounts)
        {
            if (!EmailAddress.TryParse(account.EmailAddress, out var email) || email is null)
            {
                incompatible.Add(account);
                continue;
            }

            if (!email.Domain.Matches(newDomain, AllowSubdomains))
            {
                incompatible.Add(account);
            }
        }

        return incompatible;
    }

    /// <summary>
    /// Changes the domain name after validation. Throws if incompatible accounts exist.
    /// </summary>
    public void ChangeDomainName(string newDomainName)
    {
        var incompatible = GetIncompatibleAccounts(newDomainName);
        if (incompatible.Count > 0)
        {
            var emails = string.Join(", ", incompatible.Select(a => a.EmailAddress));
            throw new DomainMismatchException(newDomainName, emails,
                $"Cannot change domain: {incompatible.Count} account(s) are incompatible: {emails}");
        }

        DomainName = EmailDomain.Parse(newDomainName).Value;
        UpdatedAt = DateTime.UtcNow;
    }
}
