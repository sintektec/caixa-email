using System.Text.Json;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Services;

/// <summary>
/// Enfileira operações de sincronização.
/// </summary>
/// <remarks>
/// Centraliza a atribuição do número de sequência e a serialização do payload. Sem um
/// ponto único, cada caso de uso montaria o seu JSON e a fila acabaria com formatos
/// divergentes para a mesma operação.
/// </remarks>
public sealed class OutboxEnqueuer
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        // Sem indentação: o payload é lido por código, não por humanos, e a fila pode
        // acumular milhares de linhas.
        WriteIndented = false,
    };

    private readonly IOutboxRepository _outbox;
    private readonly TimeProvider _timeProvider;

    public OutboxEnqueuer(IOutboxRepository outbox, TimeProvider timeProvider)
    {
        _outbox = outbox;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Enfileira uma operação para a conta indicada.
    /// </summary>
    /// <remarks>
    /// Precisa ser chamado dentro da mesma transação que grava o efeito local da ação.
    /// </remarks>
    public async Task<OutboxOperation> EnqueueAsync<TPayload>(
        Guid accountId,
        OutboxOperationType operationType,
        Guid entityId,
        TPayload payload,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var sequence = await _outbox.NextSequenceAsync(accountId, cancellationToken).ConfigureAwait(false);

        var operation = OutboxOperation.Enqueue(
            accountId,
            operationType,
            entityId,
            JsonSerializer.Serialize(payload, PayloadOptions),
            sequence,
            now);

        await _outbox.AddAsync(operation, cancellationToken).ConfigureAwait(false);
        return operation;
    }
}

/// <summary>Payload da operação de mover mensagem.</summary>
/// <param name="SourceFolderId">Pasta de origem.</param>
/// <param name="TargetFolderId">Pasta de destino.</param>
public readonly record struct MoveMessagePayload(Guid SourceFolderId, Guid TargetFolderId);

/// <summary>Payload da alteração de marcadores.</summary>
/// <param name="Seen">Marcador de lida, quando alterado.</param>
/// <param name="Flagged">Sinalizador, quando alterado.</param>
/// <param name="Answered">Marcador de respondida, quando alterado.</param>
public readonly record struct FlagChangePayload(bool? Seen, bool? Flagged, bool? Answered);

/// <summary>Payload da exclusão de mensagem.</summary>
/// <param name="FolderId">Pasta em que a mensagem estava.</param>
/// <param name="Permanent">Se é expurgo definitivo em vez de envio à lixeira.</param>
public readonly record struct DeleteMessagePayload(Guid FolderId, bool Permanent);
