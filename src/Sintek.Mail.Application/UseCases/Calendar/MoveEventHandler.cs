using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.UseCases.Calendar;

/// <summary>Resultado de arrastar um compromisso para outra data.</summary>
/// <param name="Succeeded">Se o compromisso foi movido.</param>
/// <param name="Outcome">O que a regra decidiu.</param>
/// <param name="Message">Explicação exibível quando a operação é recusada.</param>
public readonly record struct MoveEventResult(
    bool Succeeded, EventMoveOutcome Outcome, string Message)
{
    /// <summary>Se a recusa tem "propor novo horário" como alternativa.</summary>
    public bool CanProposeNewTime => Outcome == EventMoveOutcome.ProposeNewTimeInstead;
}

/// <summary>
/// Move um compromisso na grade, respeitando o papel de quem move.
/// </summary>
/// <remarks>
/// <para>
/// A decisão é do <see cref="EventMoveEvaluator"/>, no domínio. Este handler executa o que
/// ela permitir e cuida do efeito colateral que ela indica: reenviar o convite quando quem
/// move é o organizador.
/// </para>
/// <para>
/// <b>É o mesmo desenho de <c>MoveMessageHandler</c>:</b> um único caminho para a operação,
/// com a regra em um avaliador puro. Reimplementar a verificação na grade faria as duas
/// versões divergirem, e a divergência sempre termina com a interface permitindo o que o
/// domínio proíbe.
/// </para>
/// </remarks>
public sealed class MoveEventHandler
{
    private readonly ICalendarRepository _calendar;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly ICalendarSerializer _serializer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MoveEventHandler> _logger;

    public MoveEventHandler(
        ICalendarRepository calendar,
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        ICalendarSerializer serializer,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<MoveEventHandler> logger)
    {
        _calendar = calendar;
        _accounts = accounts;
        _folders = folders;
        _messages = messages;
        _serializer = serializer;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Move o compromisso para o novo início, preservando a duração.</summary>
    public async Task<MoveEventResult> MoveAsync(
        Guid eventId, DateTimeOffset newStart, CancellationToken cancellationToken = default)
    {
        var target = await _calendar.GetByIdAsync(eventId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return new MoveEventResult(
                false, EventMoveOutcome.RefusedCancelled, "O compromisso não existe mais.");
        }

        var account = await _accounts.GetByIdAsync(target.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new MoveEventResult(
                false, EventMoveOutcome.RefusedCancelled, "A conta do compromisso não existe mais.");
        }

        var decision = EventMoveEvaluator.Evaluate(target, account.EmailAddress);

        if (!decision.IsAllowed)
        {
            _logger.LogInformation(
                "Movimentação do compromisso {EventId} recusada: {Outcome}.", eventId, decision.Outcome);

            return new MoveEventResult(false, decision.Outcome, decision.Reason);
        }

        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // O SEQUENCE só sobe quando há convite a reemitir: mover um compromisso próprio
            // não é uma nova versão do convite de ninguém.
            target.MoveTo(newStart, now, decision.RequiresNotification);

            if (decision.RequiresNotification)
            {
                await EnqueueUpdatedRequestAsync(account, target, ct).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new MoveEventResult(true, decision.Outcome, string.Empty);
    }

    /// <summary>Reenvia o convite atualizado a todos os participantes.</summary>
    private async Task EnqueueUpdatedRequestAsync(
        Account account, CalendarEvent target, CancellationToken cancellationToken)
    {
        var outboxFolder = await _folders
            .GetByTypeAsync(account.Id, FolderType.Outbox, cancellationToken)
            .ConfigureAwait(false);

        if (outboxFolder is null)
        {
            _logger.LogWarning(
                "Convite atualizado do compromisso {EventId} não foi enfileirado: conta sem Caixa de Saída.",
                target.Id);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var payload = _serializer.WriteRequest(RespondToInvitationHandler.ToData(target));

        var message = Message.Create(
            account.Id, outboxFolder.Id, $"<remarcacao-{Guid.CreateVersion7():N}@sintek.local>",
            now, now, now);

        message.SetHeaders(
            $"Remarcado: {target.Summary}", account.EmailAddress, account.DisplayName, null, null, now);

        foreach (var attendee in target.Attendees.Where(a => a.Address != account.EmailAddress))
        {
            message.AddAddress(MessageAddress.Create(
                message.Id, AddressKind.To, attendee.Address, now, attendee.DisplayName));
        }

        var text = $"O compromisso \"{target.Summary}\" foi remarcado.";

        message.SetContentMetadata(
            text, text.Length, hasAttachments: false, MessageImportance.Normal, false, now);

        var body = MessageBody.Create(message.Id, now);
        body.SetContent(null, text, null, false, now);
        body.SetCalendar(payload, "REQUEST", now);
        message.SetBody(body, now);

        await _messages.AddAsync(message, cancellationToken).ConfigureAwait(false);

        await _outbox.EnqueueAsync(
            account.Id,
            OutboxOperationType.SendMessage,
            message.Id,
            new SendMessagePayload(),
            cancellationToken).ConfigureAwait(false);
    }
}
