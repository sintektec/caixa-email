using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>Resultado da marcação de spam.</summary>
/// <param name="Succeeded">Se a marcação concluiu.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct MarkAsSpamResult(bool Succeeded, string? ErrorMessage);

/// <summary>
/// Marca uma mensagem como spam — ou desfaz a marcação.
/// </summary>
/// <remarks>
/// <para>
/// A operação tem <b>duas</b> metades, e as duas importam. Mover a mensagem para a pasta de
/// lixo eletrônico é a metade visível; aplicar a palavra-chave <c>$Junk</c>/<c>$NotJunk</c>
/// no servidor é a que treina o filtro. Só mover faz o servidor continuar classificando
/// errado indefinidamente — o usuário arquiva o mesmo golpe todos os dias e nunca entende
/// por que ele continua vindo.
/// </para>
/// <para>
/// A movimentação passa por <see cref="MoveMessageHandler"/>, como toda movimentação. Isso
/// tem uma consequência deliberada em "não é spam": se a Caixa de Entrada for restrita por
/// Diretório de Domínio e a mensagem não pertencer, ela vai para pendências — que é
/// exatamente o que a regra de domínio manda fazer com ela.
/// </para>
/// </remarks>
public sealed class MarkAsSpamHandler
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly MoveMessageHandler _moveMessage;
    private readonly OutboxEnqueuer _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MarkAsSpamHandler> _logger;

    public MarkAsSpamHandler(
        IMessageRepository messages,
        IFolderRepository folders,
        MoveMessageHandler moveMessage,
        OutboxEnqueuer outbox,
        IUnitOfWork unitOfWork,
        ILogger<MarkAsSpamHandler> logger)
    {
        _messages = messages;
        _folders = folders;
        _moveMessage = moveMessage;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>Marca ou desmarca a mensagem como spam.</summary>
    public async Task<MarkAsSpamResult> HandleAsync(
        Guid messageId, bool isSpam, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return new MarkAsSpamResult(false, "A mensagem não existe mais.");
        }

        var targetType = isSpam ? FolderType.Junk : FolderType.Inbox;
        var target = await _folders.GetByTypeAsync(message.AccountId, targetType, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new MarkAsSpamResult(
                false,
                isSpam
                    ? "A conta não tem pasta de lixo eletrônico."
                    : "A conta não tem Caixa de Entrada configurada.");
        }

        // A palavra-chave é enfileirada ANTES da movimentação. A fila é estritamente
        // sequencial, e o marcador precisa ser aplicado enquanto o servidor ainda encontra a
        // mensagem na pasta atual — depois do MOVE, o UID antigo já não aponta para nada.
        await _outbox.EnqueueAsync(
            message.AccountId,
            OutboxOperationType.SetFlag,
            message.Id,
            new FlagChangePayload(null, null, null, Junk: isSpam),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var moved = await _moveMessage
            .HandleAsync(new MoveMessageCommand(message.Id, target.Id), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            isSpam
                ? "Mensagem {MessageId} marcada como spam."
                : "Mensagem {MessageId} desmarcada como spam.",
            message.Id);

        return moved.Outcome switch
        {
            MoveMessageOutcome.Moved or MoveMessageOutcome.MovedToPending
                => new MarkAsSpamResult(true, moved.UserMessage),
            _ => new MarkAsSpamResult(false, moved.UserMessage),
        };
    }
}
