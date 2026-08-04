namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A message attachment. Files are stored on disk, not as BLOBs.
/// </summary>
public sealed class Attachment : Entity
{
    public Guid MessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
    public string? StoragePath { get; set; }
    public string? PartSpecifier { get; set; }
    public bool IsDownloaded { get; set; }
    public bool IsSuspicious { get; set; }

    // Navigation
    public Message Message { get; set; } = null!;
}
