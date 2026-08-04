namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Audit log entry. Never contains sensitive message content.
/// </summary>
public sealed class AuditLog : Entity
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public string Severity { get; set; } = "Info";
}
