using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An e-mail account linked to a Domain Directory.
/// </summary>
public sealed class Account : Entity
{
    public Guid DomainId { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public SecurityProtocol ImapSecurity { get; set; } = SecurityProtocol.Auto;
    public SecurityProtocol SmtpSecurity { get; set; } = SecurityProtocol.Auto;
    public AuthenticationType AuthenticationType { get; set; } = AuthenticationType.Basic;
    public OAuthProvider? OAuthProvider { get; set; }
    public string? CredentialKey { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastSyncAt { get; set; }
    public AccountSyncStatus SyncStatus { get; set; } = AccountSyncStatus.Offline;
    public string? LastSyncError { get; set; }
    public int SyncIntervalMinutes { get; set; } = 5;
    public BodyDownloadPolicy BodyDownloadPolicy { get; set; } = BodyDownloadPolicy.Full;

    // Navigation
    public DomainDirectory Domain { get; set; } = null!;
    public ICollection<Folder> Folders { get; set; } = new List<Folder>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Signature> Signatures { get; set; } = new List<Signature>();
}
