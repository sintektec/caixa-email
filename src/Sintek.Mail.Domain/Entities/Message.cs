using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An e-mail message.
/// </summary>
public sealed class Message : Entity
{
    public Guid AccountId { get; set; }
    public Guid FolderId { get; set; }
    public Guid? ThreadId { get; set; }
    public string? MessageId { get; set; } // RFC 5322 Message-ID
    public string? InReplyTo { get; set; }
    public string? ReferencesRaw { get; set; }
    public long? Uid { get; set; }
    public long? ModSeq { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string SubjectNormalized { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Preview { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool HasAttachments { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public Importance Importance { get; set; } = Importance.Normal;
    public bool IsDraft { get; set; }
    public bool IsDeleted { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Synced;
    public DateTime? ScheduledSendAt { get; set; }
    public bool ReadReceiptRequested { get; set; }

    // Navigation
    public Account Account { get; set; } = null!;
    public Folder Folder { get; set; } = null!;
    public MessageBody? Body { get; set; }
    public ICollection<MessageAddress> Addresses { get; set; } = new List<MessageAddress>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    public ICollection<MessageCategory> MessageCategories { get; set; } = new List<MessageCategory>();
}
