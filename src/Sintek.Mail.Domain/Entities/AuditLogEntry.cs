using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Registro de auditoria de uma decisão tomada pelas regras de domínio ou de uma
/// operação sensível.
/// </summary>
/// <remarks>
/// <b>Nunca gravar conteúdo de mensagem aqui.</b> A especificação exige logs técnicos
/// sem conteúdo sigiloso: esta tabela guarda identificadores, o tipo do evento e o motivo
/// da decisão — nunca assunto, corpo, nome de anexo ou credencial. A regra vale também
/// para <see cref="DetailsJson"/>.
/// </remarks>
public sealed class AuditLogEntry : Entity
{
    private AuditLogEntry(
        Guid id,
        AuditEventType eventType,
        string description,
        DateTimeOffset occurredAt)
        : base(id, occurredAt)
    {
        EventType = eventType;
        Description = description;
        OccurredAt = occurredAt;
    }

    private AuditLogEntry()
    {
    }

    /// <summary>Quando o evento aconteceu.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Tipo do evento.</summary>
    public AuditEventType EventType { get; private set; }

    /// <summary>Gravidade.</summary>
    public AuditSeverity Severity { get; private set; } = AuditSeverity.Information;

    /// <summary>Tipo da entidade afetada (por exemplo, <c>Message</c>).</summary>
    public string? EntityType { get; private set; }

    /// <summary>Identificador da entidade afetada.</summary>
    public Guid? EntityId { get; private set; }

    /// <summary>Conta envolvida, quando aplicável.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>Diretório de Domínio envolvido, quando aplicável.</summary>
    public Guid? DomainDirectoryId { get; private set; }

    /// <summary>Descrição legível do que aconteceu, sem dados sigilosos.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Detalhes estruturados em JSON, sem dados sigilosos.</summary>
    public string? DetailsJson { get; private set; }

    /// <summary>Registra um evento de auditoria.</summary>
    public static AuditLogEntry Record(
        AuditEventType eventType,
        string description,
        DateTimeOffset occurredAt,
        AuditSeverity severity = AuditSeverity.Information,
        string? entityType = null,
        Guid? entityId = null,
        Guid? accountId = null,
        Guid? domainDirectoryId = null,
        string? detailsJson = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return new AuditLogEntry(id ?? Guid.CreateVersion7(), eventType, description, occurredAt)
        {
            Severity = severity,
            EntityType = entityType,
            EntityId = entityId,
            AccountId = accountId,
            DomainDirectoryId = domainDirectoryId,
            DetailsJson = detailsJson,
        };
    }
}
