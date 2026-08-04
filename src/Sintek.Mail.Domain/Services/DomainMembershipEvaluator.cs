using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>
/// Evaluates whether a message belongs to a domain-restricted folder.
/// This is the single entry point for all domain validation — UI, drag & drop, rules, etc.
/// </summary>
public sealed class DomainMembershipEvaluator
{
    private readonly DomainDirectory _domain;
    private readonly IReadOnlyList<DomainAlias> _aliases;

    public DomainMembershipEvaluator(DomainDirectory domain, IReadOnlyList<DomainAlias>? aliases = null)
    {
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _aliases = aliases ?? domain.Aliases.ToList().AsReadOnly();
    }

    /// <summary>
    /// Validates whether a message can be placed in a folder restricted to this domain.
    /// Returns true if the message passes validation.
    /// Throws MessageDomainViolationException if the action is Block and validation fails.
    /// </summary>
    public bool ValidateMessage(Message message, Folder folder)
    {
        if (!folder.IsDomainRestricted || folder.RestrictedToDomainId != _domain.Id)
            return true; // Not restricted to this domain

        var result = EvaluateMessage(message);

        if (!result && _domain.InvalidEmailAction == InvalidEmailAction.Block)
        {
            throw new MessageDomainViolationException();
        }

        return result;
    }

    /// <summary>
    /// Evaluates whether a message belongs to this domain based on the configured ValidationMode.
    /// Does not throw — returns true/false.
    /// </summary>
    public bool EvaluateMessage(Message message)
    {
        var targetDomain = EmailDomain.Parse(_domain.DomainName);
        var allowSubdomains = _domain.AllowSubdomains;

        // Check explicit user rules first (spec 5.3: "atende a uma regra explícita criada pelo usuário")
        // This is handled by the caller — the evaluator only checks domain membership.

        return _domain.ValidationMode switch
        {
            ValidationMode.SenderOnly => EvaluateSenderOnly(message, targetDomain, allowSubdomains),
            ValidationMode.RecipientOnly => EvaluateRecipientOnly(message, targetDomain, allowSubdomains),
            ValidationMode.SenderOrRecipient => EvaluateSenderOrRecipient(message, targetDomain, allowSubdomains),
            ValidationMode.SenderAndRecipient => EvaluateSenderAndRecipient(message, targetDomain, allowSubdomains),
            ValidationMode.AnyParticipant => EvaluateAnyParticipant(message, targetDomain, allowSubdomains),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a specific address matches the domain (exact or subdomain if allowed).
    /// Also checks domain aliases.
    /// </summary>
    public bool IsAddressInDomain(string address)
    {
        if (!EmailAddress.TryParse(address, out var email) || email is null)
            return false;

        var targetDomain = EmailDomain.Parse(_domain.DomainName);

        // Check main domain
        if (email.Domain.Matches(targetDomain, _domain.AllowSubdomains))
            return true;

        // Check aliases
        foreach (var alias in _aliases)
        {
            var aliasDomain = alias.GetEmailDomain();
            if (email.Domain.Matches(aliasDomain, _domain.AllowSubdomains))
                return true;
        }

        return false;
    }

    private bool EvaluateSenderOnly(Message message, EmailDomain targetDomain, bool allowSubdomains)
    {
        return IsAddressInDomain(message.FromAddress);
    }

    private bool EvaluateRecipientOnly(Message message, EmailDomain targetDomain, bool allowSubdomains)
    {
        return message.Addresses
            .Where(a => a.Kind is AddressKind.To or AddressKind.Cc or AddressKind.Bcc)
            .Any(a => IsAddressInDomain(a.Address));
    }

    private bool EvaluateSenderOrRecipient(Message message, EmailDomain targetDomain, bool allowSubdomains)
    {
        return IsAddressInDomain(message.FromAddress)
            || message.Addresses
                .Where(a => a.Kind is AddressKind.To or AddressKind.Cc or AddressKind.Bcc)
                .Any(a => IsAddressInDomain(a.Address));
    }

    private bool EvaluateSenderAndRecipient(Message message, EmailDomain targetDomain, bool allowSubdomains)
    {
        var senderMatches = IsAddressInDomain(message.FromAddress);
        var recipientMatches = message.Addresses
            .Where(a => a.Kind is AddressKind.To or AddressKind.Cc or AddressKind.Bcc)
            .Any(a => IsAddressInDomain(a.Address));

        return senderMatches && recipientMatches;
    }

    private bool EvaluateAnyParticipant(Message message, EmailDomain targetDomain, bool allowSubdomains)
    {
        // Check From
        if (IsAddressInDomain(message.FromAddress))
            return true;

        // Check all addresses (To, Cc, Bcc, ReplyTo)
        return message.Addresses.Any(a => IsAddressInDomain(a.Address));
    }
}
