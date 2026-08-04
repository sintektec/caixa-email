using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Sync;

/// <summary>
/// Drena a fila de saída, aplicando no servidor as ações que o usuário já executou
/// localmente.
/// </summary>
/// <remarks>
/// <para>
/// É a metade "reconcilia depois" do modo offline-first. A outra metade — gravar já — está
/// nos casos de uso, que persistem o efeito e enfileiram a operação na mesma transação.
/// </para>
/// <para>
/// O processamento é estritamente sequencial por conta, na ordem de
/// <see cref="OutboxOperation.Sequence"/>. Paralelizar aqui pareceria uma otimização
/// óbvia e quebraria a semântica: "mover para Arquivados" seguido de "marcar como lida"
/// aplicados fora de ordem fariam a segunda operação procurar a mensagem na pasta errada.
/// </para>
/// <para>
/// Uma falha interrompe o lote daquela conta em vez de pular para a próxima operação —
/// pelo mesmo motivo. As demais contas seguem normalmente.
/// </para>
/// </remarks>
public sealed class OutboxProcessor
{
    /// <summary>Quantas operações processar por ciclo, por conta.</summary>
    private const int BatchSize = 50;

    /// <summary>Espera base do recuo exponencial entre tentativas.</summary>
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Teto do recuo, para que uma conta com problema não pare de tentar.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IOutboxRepository _outbox;
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IImapClient _imapClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IOutboxRepository outbox,
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        TimeProvider timeProvider,
        ILogger<OutboxProcessor> logger)
    {
        _outbox = outbox;
        _messages = messages;
        _folders = folders;
        _unitOfWork = unitOfWork;
        _imapClient = imapClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Processa as operações pendentes de uma conta.
    /// </summary>
    /// <returns>Quantas operações foram concluídas com sucesso.</returns>
    public async Task<int> DrainAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var now = _timeProvider.GetUtcNow();
        var pending = await _outbox.ListReadyAsync(account.Id, now, BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        var connection = await _imapClient.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        if (!connection.Succeeded)
        {
            _logger.LogInformation(
                "Fila da conta {AccountId} adiada: sem conexão com o servidor.", account.Id);
            return 0;
        }

        var completed = 0;

        foreach (var operation in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                operation.MarkInProgress(_timeProvider.GetUtcNow());
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);

                operation.MarkCompleted(_timeProvider.GetUtcNow());
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(operation, ex, cancellationToken).ConfigureAwait(false);

                // Interrompe o lote desta conta: as operações seguintes dependem da ordem,
                // e aplicá-las sobre um estado inconsistente pioraria a divergência.
                break;
            }
        }

        return completed;
    }

    private async Task ExecuteAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        switch (operation.OperationType)
        {
            case OutboxOperationType.MarkAsRead:
            case OutboxOperationType.MarkAsUnread:
            case OutboxOperationType.SetFlag:
            case OutboxOperationType.ClearFlag:
                await ApplyFlagsAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.MoveMessage:
                await ApplyMoveAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.DeleteMessage:
                await ApplyDeleteAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.ExpungeFolder:
                await ApplyExpungeAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Cada tipo novo precisa de tratamento explícito. Ignorar em silêncio
                // faria a operação ser marcada como concluída sem nunca ter sido aplicada.
                throw new NotSupportedException(
                    $"A operação {operation.OperationType} ainda não é aplicada pelo processador da fila.");
        }
    }

    private async Task ApplyFlagsAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var (message, folder) = await ResolveMessageAsync(operation, cancellationToken).ConfigureAwait(false);
        if (message is null || folder is null || message.Uid is null)
        {
            return;
        }

        var payload = Deserialize<FlagChangePayload>(operation.PayloadJson);

        await _imapClient.SetFlagsAsync(
            folder.RemotePath,
            [message.Uid.Value],
            new MessageFlagChange(payload.Seen, payload.Flagged, payload.Answered),
            cancellationToken).ConfigureAwait(false);

        message.MarkSynced(_timeProvider.GetUtcNow());
    }

    private async Task ApplyMoveAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var payload = Deserialize<MoveMessagePayload>(operation.PayloadJson);

        var message = await _messages.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Uid is null)
        {
            return;
        }

        var source = await _folders.GetByIdAsync(payload.SourceFolderId, cancellationToken).ConfigureAwait(false);
        var target = await _folders.GetByIdAsync(payload.TargetFolderId, cancellationToken).ConfigureAwait(false);

        if (source is null || target is null)
        {
            return;
        }

        // Pastas locais — pendências, caixa de saída — não existem no servidor. Mover
        // para elas é uma decisão puramente local e não gera comando IMAP.
        if (target.IsLocalOnly || source.IsLocalOnly)
        {
            message.MarkSynced(_timeProvider.GetUtcNow());
            return;
        }

        var moved = await _imapClient
            .MoveAsync(source.RemotePath, target.RemotePath, [message.Uid.Value], cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        if (moved.TryGetValue(message.Uid.Value, out var newUid))
        {
            message.SetRemoteIdentity(newUid, null, now);
        }
        else
        {
            // Sem UIDPLUS o servidor não informa o novo UID. Zerá-lo faz a próxima
            // sincronização da pasta de destino reconciliar a mensagem pelo Message-ID.
            message.SetRemoteIdentity(0, null, now);
        }

        message.MarkSynced(now);
    }

    private async Task ApplyDeleteAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var (message, folder) = await ResolveMessageAsync(operation, cancellationToken).ConfigureAwait(false);
        if (message is null || folder is null || message.Uid is null)
        {
            return;
        }

        await _imapClient.SetFlagsAsync(
            folder.RemotePath,
            [message.Uid.Value],
            new MessageFlagChange(Deleted: true),
            cancellationToken).ConfigureAwait(false);

        var payload = Deserialize<DeleteMessagePayload>(operation.PayloadJson);
        if (payload.Permanent)
        {
            await _imapClient.ExpungeAsync(folder.RemotePath, cancellationToken).ConfigureAwait(false);
        }

        message.MarkSynced(_timeProvider.GetUtcNow());
    }

    private async Task ApplyExpungeAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var folder = await _folders.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (folder is null || folder.IsLocalOnly)
        {
            return;
        }

        await _imapClient.ExpungeAsync(folder.RemotePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Message? Message, Folder? Folder)> ResolveMessageAsync(
        OutboxOperation operation, CancellationToken cancellationToken)
    {
        var message = await _messages.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return (null, null);
        }

        var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
        return (message, folder);
    }

    private async Task HandleFailureAsync(
        OutboxOperation operation, Exception exception, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // NotSupportedException indica falha de programação, não instabilidade de rede:
        // repetir nunca vai resolver e só gastaria as tentativas restantes.
        var isPermanent = exception is NotSupportedException or JsonException;

        operation.MarkFailed(exception.Message, now + ComputeBackoff(operation.AttemptCount), now, isPermanent);

        _logger.LogWarning(
            exception,
            "Operação {OperationType} da conta {AccountId} falhou na tentativa {Attempt}.",
            operation.OperationType, operation.AccountId, operation.AttemptCount);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calcula o recuo exponencial com dispersão aleatória.
    /// </summary>
    /// <remarks>
    /// A dispersão evita que várias contas que perderam a conexão ao mesmo tempo voltem a
    /// tentar exatamente no mesmo instante, derrubando de novo o servidor que acabou de se
    /// recuperar.
    /// </remarks>
    internal static TimeSpan ComputeBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount, 10);
        var delay = BaseRetryDelay * Math.Pow(2, Math.Max(exponent - 1, 0));

        if (delay > MaxRetryDelay)
        {
            delay = MaxRetryDelay;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(delay.TotalMilliseconds * 0.2)));
        return delay + jitter;
    }

    private static TPayload Deserialize<TPayload>(string json)
        where TPayload : struct
        => JsonSerializer.Deserialize<TPayload>(json);
}
