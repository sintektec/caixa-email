using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Calendar;

/// <summary>Uma ocorrência de um compromisso dentro da janela consultada.</summary>
/// <param name="EventId">Evento de origem.</param>
/// <param name="StartsAt">Início desta ocorrência.</param>
/// <param name="EndsAt">Fim desta ocorrência.</param>
/// <param name="IsRecurrence">
/// Se é repetição de um evento recorrente, e não o primeiro encontro.
/// </param>
public readonly record struct EventOccurrence(
    Guid EventId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsRecurrence);

/// <summary>Dados de um compromisso a criar ou editar.</summary>
public sealed record CalendarEventCommand
{
    /// <summary>Conta dona.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Compromisso existente, quando for edição.</summary>
    public Guid? EventId { get; init; }

    /// <summary>Assunto.</summary>
    public required string Summary { get; init; }

    /// <summary>Descrição.</summary>
    public string? Description { get; init; }

    /// <summary>Local.</summary>
    public string? Location { get; init; }

    /// <summary>Endereço de entrada da reunião on-line.</summary>
    public string? MeetingUrl { get; init; }

    /// <summary>Início.</summary>
    public required DateTimeOffset StartsAt { get; init; }

    /// <summary>Fim.</summary>
    public required DateTimeOffset EndsAt { get; init; }

    /// <summary>Se ocupa o dia inteiro.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>Regra de recorrência, como texto <c>RRULE</c>.</summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>Se o usuário quer lembrete.</summary>
    public bool HasReminder { get; init; }

    /// <summary>Antecedência do lembrete, em minutos.</summary>
    public int ReminderMinutesBefore { get; init; } = 15;

    /// <summary>Participantes convidados.</summary>
    public IReadOnlyList<EmailAddress> Attendees { get; init; } = [];
}

/// <summary>Resultado da gravação de um compromisso.</summary>
/// <param name="Succeeded">Se concluiu.</param>
/// <param name="EventId">Identificador do compromisso.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct CalendarEventResult(bool Succeeded, Guid? EventId, string? ErrorMessage);

/// <summary>
/// Cria, edita, remove e lista compromissos da agenda.
/// </summary>
/// <remarks>
/// A grade pede uma janela de tempo e recebe ocorrências, não eventos: um evento semanal é
/// uma linha no banco e doze marcações na tela de um trimestre. Quem sabe expandir a
/// recorrência é o <see cref="ICalendarSerializer"/>, que já implementa a norma.
/// </remarks>
public sealed class ManageEventsHandler
{
    private readonly ICalendarRepository _calendar;
    private readonly IAccountRepository _accounts;
    private readonly ICalendarSerializer _serializer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManageEventsHandler> _logger;

    public ManageEventsHandler(
        ICalendarRepository calendar,
        IAccountRepository accounts,
        ICalendarSerializer serializer,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        IFolderRepository folders,
        IMessageRepository messages,
        TimeProvider timeProvider,
        ILogger<ManageEventsHandler> logger)
    {
        _calendar = calendar;
        _accounts = accounts;
        _serializer = serializer;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _folders = folders;
        _messages = messages;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Carrega um compromisso.</summary>
    public Task<CalendarEvent?> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
        => _calendar.GetByIdAsync(eventId, cancellationToken);

    /// <summary>Lista os compromissos que tocam a janela.</summary>
    public Task<IReadOnlyList<CalendarEvent>> ListAsync(
        Guid? accountId,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
        => _calendar.ListInRangeAsync(accountId, from, until, cancellationToken);

    /// <summary>
    /// Lista as ocorrências que caem na janela, já com a recorrência expandida.
    /// </summary>
    /// <remarks>
    /// Compromisso cancelado continua na lista, marcado: quem reservou o horário precisa
    /// ver que a reunião caiu. Sumir da grade é indistinguível de erro de sincronização.
    /// </remarks>
    public async Task<IReadOnlyList<EventOccurrence>> ListOccurrencesAsync(
        Guid? accountId,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var events = await _calendar
            .ListInRangeAsync(accountId, from, until, cancellationToken)
            .ConfigureAwait(false);

        var occurrences = new List<EventOccurrence>();

        foreach (var source in events)
        {
            var duration = source.EndsAt - source.StartsAt;

            if (!source.IsRecurring)
            {
                if (source.EndsAt > from && source.StartsAt < until)
                {
                    occurrences.Add(new EventOccurrence(
                        source.Id, source.StartsAt, source.EndsAt, IsRecurrence: false));
                }

                continue;
            }

            foreach (var start in _serializer.ExpandOccurrences(
                source.RecurrenceRule!, source.StartsAt, from, until))
            {
                occurrences.Add(new EventOccurrence(
                    source.Id, start, start + duration, start != source.StartsAt));
            }
        }

        return [.. occurrences.OrderBy(o => o.StartsAt).ThenBy(o => o.EventId)];
    }

    /// <summary>Cria ou edita um compromisso.</summary>
    public async Task<CalendarEventResult> SaveAsync(
        CalendarEventCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Summary))
        {
            return new CalendarEventResult(false, null, "Informe o assunto do compromisso.");
        }

        if (command.EndsAt < command.StartsAt)
        {
            return new CalendarEventResult(
                false, null, "O fim do compromisso não pode ser anterior ao início.");
        }

        var account = await _accounts.GetByIdAsync(command.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new CalendarEventResult(false, null, "A conta informada não existe.");
        }

        var now = _timeProvider.GetUtcNow();

        var target = command.EventId is { } id
            ? await _calendar.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

        if (command.EventId is not null && target is null)
        {
            return new CalendarEventResult(false, null, "O compromisso não existe mais.");
        }

        var isNew = target is null;

        if (target is null)
        {
            // UID gerado aqui, no formato que a norma pede: quem criou o compromisso é
            // este cliente, e é ele quem passa a ser o organizador.
            target = CalendarEvent.Create(
                account.Id,
                $"{Guid.CreateVersion7():N}@sintek.mail",
                command.Summary,
                command.StartsAt,
                command.EndsAt,
                now);

            await _calendar.AddAsync(target, cancellationToken).ConfigureAwait(false);
            target.SetOrganizer(account.EmailAddress, account.DisplayName, now);
        }

        target.ApplyUpdate(
            isNew ? 0 : target.Sequence,
            command.Summary,
            command.Description,
            command.Location,
            command.MeetingUrl,
            command.StartsAt,
            command.EndsAt,
            command.IsAllDay,
            target.TimeZoneId,
            target.Status,
            command.RecurrenceRule,
            now);

        target.SetReminder(command.HasReminder, command.ReminderMinutesBefore, now);

        target.SyncAttendees(
            [.. command.Attendees.Select(a => new AttendeeSnapshot(
                a, null, AttendeeRole.Required, AttendeeResponse.NeedsAction))],
            now);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Compromisso {EventId} gravado.", target.Id);

        return new CalendarEventResult(true, target.Id, null);
    }

    /// <summary>
    /// Remove um compromisso.
    /// </summary>
    /// <remarks>
    /// Quem organiza a reunião não a apaga em silêncio: o cancelamento sai pela fila de
    /// saída para todos os participantes, e só então o registro local vai embora. Um
    /// compromisso próprio, sem participantes, some direto — não há a quem avisar.
    /// </remarks>
    public async Task<CalendarEventResult> RemoveAsync(
        Guid eventId, CancellationToken cancellationToken = default)
    {
        var target = await _calendar.GetByIdAsync(eventId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return new CalendarEventResult(false, null, "O compromisso não existe mais.");
        }

        var account = await _accounts.GetByIdAsync(target.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var mustNotify = account is not null
            && target.IsOrganizedBy(account.EmailAddress)
            && target.OtherAttendeeCount(account.EmailAddress) > 0;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (mustNotify && account is not null)
            {
                await EnqueueCancelAsync(account, target, ct).ConfigureAwait(false);
            }

            _calendar.Remove(target);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new CalendarEventResult(true, eventId, null);
    }

    /// <summary>Envia o cancelamento aos participantes.</summary>
    internal async Task EnqueueCancelAsync(
        Account account, CalendarEvent target, CancellationToken cancellationToken)
    {
        var outboxFolder = await _folders
            .GetByTypeAsync(account.Id, FolderType.Outbox, cancellationToken)
            .ConfigureAwait(false);

        if (outboxFolder is null)
        {
            _logger.LogWarning(
                "Cancelamento do compromisso {EventId} não foi enfileirado: conta sem Caixa de Saída.",
                target.Id);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var data = RespondToInvitationHandler.ToData(target);
        var payload = _serializer.WriteCancel(data);

        var message = Message.Create(
            account.Id, outboxFolder.Id, $"<cancelamento-{Guid.CreateVersion7():N}@sintek.local>",
            now, now, now);

        message.SetHeaders(
            $"Cancelado: {target.Summary}", account.EmailAddress, account.DisplayName, null, null, now);

        foreach (var attendee in target.Attendees.Where(a => a.Address != account.EmailAddress))
        {
            message.AddAddress(MessageAddress.Create(
                message.Id, AddressKind.To, attendee.Address, now, attendee.DisplayName));
        }

        var text = $"O compromisso \"{target.Summary}\" foi cancelado.";

        message.SetContentMetadata(
            text, text.Length, hasAttachments: false, MessageImportance.Normal, false, now);

        var body = MessageBody.Create(message.Id, now);
        body.SetContent(null, text, null, false, now);
        body.SetCalendar(payload, "CANCEL", now);
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
