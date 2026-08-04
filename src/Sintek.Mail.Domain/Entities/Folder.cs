using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A mail folder within an account. Can be restricted to a domain.
/// </summary>
public sealed class Folder : Entity
{
    public Guid AccountId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public FolderType FolderType { get; set; } = FolderType.Custom;
    public string RemotePath { get; set; } = string.Empty;
    public string Delimiter { get; set; } = "/";
    public bool IsFavorite { get; set; }
    public bool IsDomainRestricted { get; set; }
    public Guid? RestrictedToDomainId { get; set; }
    public int UnreadCount { get; set; }
    public int TotalCount { get; set; }
    public long? UidValidity { get; set; }
    public long? HighestModSeq { get; set; }
    public long? LastSeenUid { get; set; }
    public bool SyncEnabled { get; set; } = true;

    // Navigation
    public Account Account { get; set; } = null!;
    public Folder? ParentFolder { get; set; }
    public ICollection<Folder> SubFolders { get; set; } = new List<Folder>();
    public DomainDirectory? RestrictedToDomain { get; set; }
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
