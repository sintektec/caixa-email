using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.DTOs;

public sealed record AccountDto(
    Guid Id,
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
    OAuthProvider? OAuthProvider,
    bool IsActive,
    DateTime? LastSyncAt,
    AccountSyncStatus SyncStatus,
    string? LastSyncError,
    int SyncIntervalMinutes,
    BodyDownloadPolicy BodyDownloadPolicy
);
