using System.Globalization;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Calendar;

/// <summary>Resultado de uma resposta a convite.</summary>
/// <param name="Succeeded">Se a resposta foi registrada e enfileirada.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct InvitationResponseResult(bool Succeeded, string? ErrorMessage);

/// <summary>
/// Aceita, recusa ou marca como provisório um convite, e propõe novo horário.
/// </summary>
/// <remarks>
/// <para>
/// A resposta sai <b>pela fila de saída</b>, como todo envio deste produto. Falar direto com
/// o SMTP criaria um segundo caminho de envio sem ordem, sem retentativa e sem visibilidade
/// — e o usuário que respondeu offline não saberia que a resposta não saiu.
/// </para>
/// <para>
/// A agenda local é atualizada na mesma transação em que a operação é enfileirada. Esperar
/// a confirmação do servidor deixaria o botão "Aceitar" sem efeito visível até a próxima
/// sincronização.
/// </para>
/// </remarks>
public sealed class RespondToInvitationHandler
{
    private readonly ICalendarRepository _calendar;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly ICalendarSerializer _serializer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RespondToInvitationHandler> _logger;

    public RespondToInvitationHandler(
        ICalendarRepository calendar,
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        ICalendarSerializer serializer,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<RespondToInvitationHandler> logger)
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

    /// <summary>Responde ao convite.</summary>
    public Task<InvitationResponseResult> RespondAsync(
        Guid eventId, AttendeeResponse response, CancellationToken cancellationToken = default)
        => SendAsync(eventId, response, proposedStart: null, cancellationToken);

    /// <summary>
    /// Propõe outro horário ao organizador.
    /// </summary>
    /// <remarks>
    /// É a alternativa oferecida a quem tenta arrastar a reunião de outra pessoa na grade —
    /// a operação que o <c>EventMoveEvaluator</c> recusa. Resolve o que arrastar não
    /// resolveria: o organizador fica sabendo.
    /// </remarks>
    public Task<InvitationResponseResult> ProposeNewTimeAsync(
        Guid eventId, DateTimeOffset proposedStart, CancellationToken cancellationToken = default)
        => SendAsync(eventId, AttendeeResponse.Tentative, proposedStart, cancellationToken);

    private async Task<InvitationResponseResult> SendAsync(
        Guid eventId,
        AttendeeResponse response,
        DateTimeOffset? proposedStart,
        CancellationToken cancellationToken)
    {
        var target = await _calendar.GetByIdAsync(eventId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return new InvitationResponseResult(false, "O compromisso não existe mais.");
        }

        if (target.OrganizerAddress is not { } organizer)
        {
            return new InvitationResponseResult(
                false, "Este compromisso não tem organizador para quem responder.");
        }

        var account = await _accounts.GetByIdAsync(target.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new InvitationResponseResult(false, "A conta do compromisso não existe mais.");
        }

        var outbox = await _folders
            .GetByTypeAsync(account.Id, FolderType.Outbox, cancellationToken)
            .ConfigureAwait(false);

        if (outbox is null)
        {
            return new InvitationResponseResult(false, "A conta não tem Caixa de Saída configurada.");
        }

        var now = _timeProvider.GetUtcNow();
        var data = ToData(target);

        var payload = proposedStart is { } start
            ? _serializer.WriteCounter(data, account.EmailAddress, start)
            : _serializer.WriteReply(data, account.EmailAddress, response);

        var method = proposedStart is null ? "REPLY" : "COUNTER";

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var message = Message.Create(
                account.Id,
                outbox.Id,
                $"<resposta-{Guid.CreateVersion7():N}@sintek.local>",
                now,
                now,
                now);

            var subject = BuildSubject(target.Summary, response, proposedStart);

            message.SetHeaders(
                subject, account.EmailAddress, account.DisplayName, null, null, now);

            message.AddAddress(MessageAddress.Create(
                message.Id, AddressKind.To, organizer, now, target.OrganizerDisplayName));

            var text = BuildBody(target, response, proposedStart);

            message.SetContentMetadata(
                text.Length <= 120 ? text : text[..120],
                text.Length,
                hasAttachments: false,
                MessageImportance.Normal,
                readReceiptRequested: false,
                now);

            var body = MessageBody.Create(message.Id, now);
            body.SetContent(null, text, null, false, now);
            body.SetCalendar(payload, method, now);
            message.SetBody(body, now);

            await _messages.AddAsync(message, ct).ConfigureAwait(false);

            await _outbox.EnqueueAsync(
                account.Id,
                OutboxOperationType.SendMessage,
                message.Id,
                new SendMessagePayload(),
                ct).ConfigureAwait(false);

            // A agenda local reflete a decisão agora; a fila cuida de contar ao organizador.
            target.SetAttendeeResponse(account.EmailAddress, response, now);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Resposta {Method} ao compromisso {EventId} entregue à fila.", method, eventId);

        return new InvitationResponseResult(true, null);
    }

    /// <summary>Projeta o evento no formato que o serializador entende.</summary>
    internal static CalendarEventData ToData(CalendarEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new CalendarEventData
        {
            Uid = source.Uid,
            Sequence = source.Sequence,
            Summary = source.Summary,
            Description = source.Description,
            Location = source.Location,
            MeetingUrl = source.MeetingUrl,
            StartsAt = source.StartsAt,
            EndsAt = source.EndsAt,
            IsAllDay = source.IsAllDay,
            TimeZoneId = source.TimeZoneId,
            Status = source.Status,
            OrganizerAddress = source.OrganizerAddress,
            OrganizerDisplayName = source.OrganizerDisplayName,
            RecurrenceRule = source.RecurrenceRule,
            Attendees =
            [
                .. source.Attendees.Select(a => new CalendarAttendeeData(
                    a.Address, a.DisplayName, a.Role, a.Response))
            ],
        };
    }

    private static string BuildSubject(
        string summary, AttendeeResponse response, DateTimeOffset? proposedStart)
    {
        var prefix = proposedStart is not null
            ? "Novo horário proposto"
            : response switch
            {
                AttendeeResponse.Accepted => "Aceito",
                AttendeeResponse.Declined => "Recusado",
                AttendeeResponse.Tentative => "Provisório",
                _ => "Resposta",
            };

        return $"{prefix}: {summary}";
    }

    private static string BuildBody(
        CalendarEvent target, AttendeeResponse response, DateTimeOffset? proposedStart)
    {
        // Formato explícito com InvariantCulture: pedir a cultura pt-BR lança em tempo de
        // execução com InvariantGlobalization ligado.
        if (proposedStart is { } start)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Proponho remarcar \"{0}\" para {1:dd/MM/yyyy HH:mm}.",
                target.Summary,
                start.ToLocalTime());
        }

        var verb = response switch
        {
            AttendeeResponse.Accepted => "aceitou",
            AttendeeResponse.Declined => "recusou",
            AttendeeResponse.Tentative => "marcou como provisório",
            _ => "respondeu",
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            "O convite \"{0}\" de {1:dd/MM/yyyy HH:mm} foi {2}.",
            target.Summary,
            target.StartsAt.ToLocalTime(),
            verb);
    }
}
