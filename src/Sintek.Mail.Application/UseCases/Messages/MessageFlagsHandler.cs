using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>Resultado de uma operação sobre marcadores.</summary>
/// <param name="Succeeded">Se a operação foi aplicada.</param>
/// <param name="ErrorMessage">Explicação exibível quando não foi.</param>
public readonly record struct MessageFlagResult(bool Succeeded, string? ErrorMessage);

/// <summary>
/// Alterações de marcador disparadas pelos gestos rápidos da interface: lida, não lida,
/// sinalizador e envio para a lixeira.
/// </summary>
/// <remarks>
/// Existe para que os atalhos de teclado não falem com repositório direto. A gravação
/// local e o enfileiramento acontecem na mesma transação, como manda o modo offline-first
/// — a alternativa seria a interface mostrar "lida" e o servidor nunca ficar sabendo.
/// </remarks>
public sealed class MessageFlagsHandler
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly MoveMessageHandler _moveMessage;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MessageFlagsHandler> _logger;

    public MessageFlagsHandler(
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        MoveMessageHandler moveMessage,
        TimeProvider timeProvider,
        ILogger<MessageFlagsHandler> logger)
    {
        _messages = messages;
        _folders = folders;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _moveMessage = moveMessage;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Marca como lida ou não lida.</summary>
    public Task<bool> SetReadAsync(
        Guid messageId, bool isRead, CancellationToken cancellationToken = default)
        => ApplyFlagAsync(
            messageId,
            (message, now) =>
            {
                if (message.IsRead == isRead)
                {
                    return false;
                }

                message.SetRead(isRead, now);
                return true;
            },
            isRead ? OutboxOperationType.MarkAsRead : OutboxOperationType.MarkAsUnread,
            new FlagChangePayload(Seen: isRead, null, null),
            cancellationToken);

    /// <summary>Aplica ou remove o sinalizador.</summary>
    public Task<bool> SetFlaggedAsync(
        Guid messageId, bool isFlagged, CancellationToken cancellationToken = default)
        => ApplyFlagAsync(
            messageId,
            (message, now) =>
            {
                if (message.IsFlagged == isFlagged)
                {
                    return false;
                }

                message.SetFlagged(isFlagged, now);
                return true;
            },
            isFlagged ? OutboxOperationType.SetFlag : OutboxOperationType.ClearFlag,
            new FlagChangePayload(null, Flagged: isFlagged, null),
            cancellationToken);

    /// <summary>
    /// Move a mensagem para a lixeira da conta.
    /// </summary>
    /// <remarks>
    /// Passa pelo <see cref="MoveMessageHandler"/> como qualquer movimentação: a lixeira
    /// também pode ser uma pasta restrita, e a regra de domínio vale lá igual.
    /// </remarks>
    public async Task<MessageFlagResult> MoveToTrashAsync(
        Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return new MessageFlagResult(false, "A mensagem não existe mais.");
        }

        var trash = await _folders
            .GetByTypeAsync(message.AccountId, FolderType.Trash, cancellationToken)
            .ConfigureAwait(false);

        if (trash is null)
        {
            return new MessageFlagResult(false, "A conta não tem pasta de lixeira configurada.");
        }

        if (message.FolderId == trash.Id)
        {
            return new MessageFlagResult(true, null);
        }

        try
        {
            var result = await _moveMessage
                .HandleAsync(new MoveMessageCommand(messageId, trash.Id), cancellationToken)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                MoveMessageOutcome.Blocked or MoveMessageOutcome.RequiresConfirmation
                    => new MessageFlagResult(false, result.UserMessage),
                _ => new MessageFlagResult(true, null),
            };
        }
        catch (Domain.Exceptions.FolderDomainRestrictionException ex)
        {
            return new MessageFlagResult(false, ex.UserMessage);
        }
    }

    private async Task<bool> ApplyFlagAsync(
        Guid messageId,
        Func<Domain.Entities.Message, DateTimeOffset, bool> mutate,
        OutboxOperationType operationType,
        FlagChangePayload payload,
        CancellationToken cancellationToken)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var changed = false;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (!mutate(message, now))
            {
                return;
            }

            await _outbox.EnqueueAsync(message.AccountId, operationType, message.Id, payload, ct)
                .ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            changed = true;
        }, cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            _logger.LogDebug("Marcador {Operation} aplicado à mensagem {MessageId}.", operationType, messageId);
        }

        return changed;
    }
}
