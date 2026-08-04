using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Uma ação do usuário registrada localmente, aguardando propagação para o servidor.
/// </summary>
/// <remarks>
/// <para>
/// É a peça central do modo offline-first. Toda ação — marcar como lida, mover, excluir,
/// enviar — grava o efeito no banco local e enfileira aqui, <b>na mesma transação</b>.
/// Ou as duas coisas acontecem, ou nenhuma: nunca há um estado em que a interface mostra
/// a mensagem movida mas o servidor jamais ficará sabendo.
/// </para>
/// <para>
/// <see cref="Sequence"/> é atribuído por um contador monotônico e dá a ordem de
/// execução. Sem ele, "mover para Arquivados" e depois "marcar como lida" poderiam ser
/// aplicados na ordem inversa e a segunda operação falharia por não achar a mensagem na
/// pasta esperada.
/// </para>
/// </remarks>
public sealed class OutboxOperation : Entity
{
    /// <summary>Tentativas antes de considerar a operação definitivamente perdida.</summary>
    public const int DefaultMaxAttempts = 5;

    private OutboxOperation(
        Guid id,
        Guid accountId,
        OutboxOperationType operationType,
        Guid entityId,
        string payloadJson,
        long sequence,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        OperationType = operationType;
        EntityId = entityId;
        PayloadJson = payloadJson;
        Sequence = sequence;
        NextAttemptAt = createdAt;
    }

    private OutboxOperation()
    {
    }

    /// <summary>Conta em que a operação será executada.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Conta em que a operação será executada.</summary>
    public Account? Account { get; private set; }

    /// <summary>O que fazer.</summary>
    public OutboxOperationType OperationType { get; private set; }

    /// <summary>Entidade afetada (mensagem, pasta…).</summary>
    public Guid EntityId { get; private set; }

    /// <summary>
    /// Parâmetros da operação em JSON. Nunca contém corpo de mensagem nem credencial —
    /// apenas identificadores e marcadores.
    /// </summary>
    public string PayloadJson { get; private set; } = "{}";

    /// <summary>Situação atual.</summary>
    public OutboxOperationStatus Status { get; private set; } = OutboxOperationStatus.Pending;

    /// <summary>Ordem determinística de execução dentro da conta.</summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// Operação que precisa ser concluída antes desta. Encadeia, por exemplo, o APPEND de
    /// um rascunho e a alteração de marcador que veio depois dele.
    /// </summary>
    public Guid? DependsOnId { get; private set; }

    /// <summary>Quantas tentativas já foram feitas.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Limite de tentativas.</summary>
    public int MaxAttempts { get; private set; } = DefaultMaxAttempts;

    /// <summary>Quando tentar de novo. Nulo quando não há nova tentativa prevista.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Erro da última tentativa, em texto exibível.</summary>
    public string? LastError { get; private set; }

    /// <summary>Quando a operação foi concluída.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Enfileira uma operação.</summary>
    public static OutboxOperation Enqueue(
        Guid accountId,
        OutboxOperationType operationType,
        Guid entityId,
        string payloadJson,
        long sequence,
        DateTimeOffset createdAt,
        Guid? dependsOnId = null,
        int maxAttempts = DefaultMaxAttempts,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        return new OutboxOperation(
            id ?? Guid.CreateVersion7(),
            accountId,
            operationType,
            entityId,
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            sequence,
            createdAt)
        {
            DependsOnId = dependsOnId,
            MaxAttempts = maxAttempts,
        };
    }

    /// <summary>Marca a operação como em execução.</summary>
    public void MarkInProgress(DateTimeOffset now)
    {
        Status = OutboxOperationStatus.InProgress;
        AttemptCount++;
        Touch(now);
    }

    /// <summary>Marca a operação como concluída.</summary>
    public void MarkCompleted(DateTimeOffset now)
    {
        Status = OutboxOperationStatus.Completed;
        CompletedAt = now;
        NextAttemptAt = null;
        LastError = null;
        Touch(now);
    }

    /// <summary>
    /// Registra uma falha e agenda nova tentativa, ou desiste ao esgotar o limite.
    /// </summary>
    /// <param name="isPermanent">
    /// Verdadeiro quando o servidor recusou de forma definitiva (mensagem inexistente,
    /// pasta removida). Nesse caso não adianta tentar de novo.
    /// </param>
    public void MarkFailed(string error, DateTimeOffset nextAttemptAt, DateTimeOffset now, bool isPermanent = false)
    {
        LastError = error;

        if (isPermanent || AttemptCount >= MaxAttempts)
        {
            Status = OutboxOperationStatus.Dead;
            NextAttemptAt = null;
        }
        else
        {
            Status = OutboxOperationStatus.Failed;
            NextAttemptAt = nextAttemptAt;
        }

        Touch(now);
    }

    /// <summary>Cancela a operação antes que ela seja executada.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is OutboxOperationStatus.Completed or OutboxOperationStatus.InProgress)
        {
            throw new InvalidOperationException(
                "Só é possível cancelar operações que ainda não foram concluídas nem estão em execução.");
        }

        Status = OutboxOperationStatus.Cancelled;
        NextAttemptAt = null;
        Touch(now);
    }

    /// <summary>Recoloca na fila uma operação que havia falhado em definitivo.</summary>
    public void Retry(DateTimeOffset now)
    {
        Status = OutboxOperationStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        LastError = null;
        Touch(now);
    }

    /// <summary>Indica se a operação está pronta para ser executada agora.</summary>
    public bool IsReady(DateTimeOffset now)
        => Status is OutboxOperationStatus.Pending or OutboxOperationStatus.Failed
            && (NextAttemptAt is null || NextAttemptAt <= now);
}
