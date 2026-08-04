using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Handlers;

public sealed record AddAccountCommand(
    Guid DomainId,
    string EmailAddress,
    string DisplayName,
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    bool UseSsl,
    SecurityProtocol ImapSecurity,
    SecurityProtocol SmtpSecurity,
    AuthenticationType AuthenticationType,
    OAuthProvider? OAuthProvider = null,
    string? CredentialKey = null
);

public sealed class AddAccountHandler
{
    private readonly IMailRepository _repository;

    public AddAccountHandler(IMailRepository repository)
    {
        _repository = repository;
    }

    public async Task<AccountDto> HandleAsync(AddAccountCommand command, CancellationToken ct = default)
    {
        var domain = await _repository.GetDomainByIdAsync(command.DomainId, ct)
            ?? throw new InvalidOperationException($"Domain directory '{command.DomainId}' not found.");

        // Validate domain match (spec 5.2)
        var email = EmailAddress.Parse(command.EmailAddress);
        domain.ValidateAccount(email);

        var account = new Account
        {
            DomainId = command.DomainId,
            EmailAddress = email.FullAddress,
            DisplayName = command.DisplayName,
            ImapHost = command.ImapHost,
            ImapPort = command.ImapPort,
            SmtpHost = command.SmtpHost,
            SmtpPort = command.SmtpPort,
            UseSsl = command.UseSsl,
            ImapSecurity = command.ImapSecurity,
            SmtpSecurity = command.SmtpSecurity,
            AuthenticationType = command.AuthenticationType,
            OAuthProvider = command.OAuthProvider,
            CredentialKey = command.CredentialKey
        };

        await _repository.AddAccountAsync(account, ct);
        await _repository.SaveChangesAsync(ct);

        return new AccountDto(
            account.Id,
            account.DomainId,
            account.EmailAddress,
            account.DisplayName,
            account.ImapHost,
            account.ImapPort,
            account.SmtpHost,
            account.SmtpPort,
            account.UseSsl,
            account.ImapSecurity,
            account.SmtpSecurity,
            account.AuthenticationType,
            account.OAuthProvider,
            account.IsActive,
            account.LastSyncAt,
            account.SyncStatus,
            account.LastSyncError,
            account.SyncIntervalMinutes,
            account.BodyDownloadPolicy
        );
    }
}
