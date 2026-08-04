namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Message body stored separately to avoid inflating the message list.
/// </summary>
public sealed class MessageBody : Entity
{
    public Guid MessageId { get; set; }
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public string? SanitizedHtml { get; set; }
    public bool HasRemoteContent { get; set; }
    public DateTime? DownloadedAt { get; set; }

    // Navigation
    public Message Message { get; set; } = null!;
}
