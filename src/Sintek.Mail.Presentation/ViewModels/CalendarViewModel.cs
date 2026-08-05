using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.UseCases.Calendar;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Amplitude da grade.</summary>
public enum CalendarViewMode
{
    /// <summary>Um dia.</summary>
    Day = 0,

    /// <summary>Sete dias a partir do início da semana.</summary>
    Week = 1,

    /// <summary>O mês inteiro.</summary>
    Month = 2,
}

/// <summary>Uma marcação na grade.</summary>
/// <param name="EventId">Compromisso de origem.</param>
/// <param name="Summary">Assunto.</param>
/// <param name="StartsAt">Início desta ocorrência, no fuso local.</param>
/// <param name="EndsAt">Fim.</param>
/// <param name="IsAllDay">Se ocupa o dia inteiro.</param>
/// <param name="IsCancelled">Se o compromisso foi cancelado.</param>
/// <param name="IsRecurrence">Se é repetição, e não o primeiro encontro.</param>
/// <param name="OrganizerName">Quem organiza, para o usuário saber de quem é a reunião.</param>
/// <param name="MyResponse">Resposta que esta conta já deu.</param>
/// <param name="MeetingUrl">Endereço de entrada da reunião on-line, quando houver.</param>
public sealed record CalendarOccurrenceItem(
    Guid EventId,
    string Summary,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    bool IsCancelled,
    bool IsRecurrence,
    string OrganizerName,
    AttendeeResponse MyResponse,
    string MeetingUrl)
{
    /// <summary>Faixa de horário exibida na marcação.</summary>
    /// <remarks>
    /// Formato explícito com <see cref="CultureInfo.InvariantCulture"/>: pedir a cultura
    /// pt-BR lança em tempo de execução com <c>InvariantGlobalization</c> ligado.
    /// </remarks>
    public string TimeRange => IsAllDay
        ? "Dia inteiro"
        : string.Format(
            CultureInfo.InvariantCulture, "{0:HH:mm} – {1:HH:mm}",
            StartsAt.ToLocalTime(), EndsAt.ToLocalTime());

    /// <summary>Dia a que a marcação pertence, no fuso local.</summary>
    public DateTimeOffset LocalDay => StartsAt.ToLocalTime().Date is var date
        ? new DateTimeOffset(date, StartsAt.ToLocalTime().Offset)
        : StartsAt;

    /// <summary>Se há reunião on-line para entrar.</summary>
    public bool HasMeetingUrl => MeetingUrl.Length > 0;

    /// <summary>Rótulo da resposta já dada, vazio quando ainda não respondeu.</summary>
    public string ResponseLabel => MyResponse switch
    {
        AttendeeResponse.Accepted => "Aceito",
        AttendeeResponse.Declined => "Recusado",
        AttendeeResponse.Tentative => "Provisório",
        AttendeeResponse.Delegated => "Delegado",
        _ => string.Empty,
    };

    /// <summary>Se ainda há uma resposta pendente a dar.</summary>
    public bool NeedsResponse => MyResponse == AttendeeResponse.NeedsAction && !IsCancelled;
}

/// <summary>
/// A agenda: grade de dia, semana e mês, com as ações de responder e remarcar.
/// </summary>
/// <remarks>
/// <para>
/// A grade trabalha em ocorrências, não em compromissos: um evento semanal é uma linha no
/// banco e doze marcações na tela de um trimestre. A expansão fica no caso de uso, que
/// delega a quem implementa a norma.
/// </para>
/// <para>
/// <b>Arrastar não decide nada aqui.</b> Quem decide é o <c>EventMoveEvaluator</c>, no
/// domínio, e o resultado da recusa chega como texto para exibir junto com a oferta de
/// propor novo horário. Reimplementar a regra na tela faria as duas versões divergirem.
/// </para>
/// </remarks>
public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly ManageEventsHandler _events;
    private readonly MoveEventHandler _move;
    private readonly RespondToInvitationHandler _respond;
    private readonly TimeProvider _timeProvider;

    public CalendarViewModel(
        ManageEventsHandler events,
        MoveEventHandler move,
        RespondToInvitationHandler respond,
        TimeProvider timeProvider)
    {
        _events = events;
        _move = move;
        _respond = respond;
        _timeProvider = timeProvider;
    }

    /// <summary>Conta cuja agenda está aberta. Nulo mostra todas.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Dia de referência da grade.</summary>
    [ObservableProperty]
    private DateTimeOffset _anchorDate;

    /// <summary>Amplitude exibida.</summary>
    [ObservableProperty]
    private CalendarViewMode _viewMode = CalendarViewMode.Week;

    /// <summary>Marcação selecionada.</summary>
    [ObservableProperty]
    private CalendarOccurrenceItem? _selectedOccurrence;

    /// <summary>Mensagem de erro ou explicação.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Se a recusa de movimentação oferece propor novo horário.
    /// </summary>
    /// <remarks>
    /// A alternativa é o que resolve o problema de verdade: mover a própria cópia deixaria
    /// o usuário fora do horário combinado sem que ninguém soubesse.
    /// </remarks>
    [ObservableProperty]
    private bool _canProposeNewTime;

    /// <summary>Horário que o usuário tentou aplicar e foi recusado.</summary>
    [ObservableProperty]
    private DateTimeOffset? _refusedStart;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Marcações da janela atual.</summary>
    public ObservableCollection<CalendarOccurrenceItem> Occurrences { get; } = [];

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>Início da janela exibida, no fuso local.</summary>
    public DateTimeOffset RangeStart => ViewMode switch
    {
        CalendarViewMode.Day => StartOfDay(AnchorDate),
        CalendarViewMode.Week => StartOfWeek(AnchorDate),
        _ => StartOfMonth(AnchorDate),
    };

    /// <summary>Fim da janela exibida — exclusivo.</summary>
    public DateTimeOffset RangeEnd => ViewMode switch
    {
        CalendarViewMode.Day => RangeStart.AddDays(1),
        CalendarViewMode.Week => RangeStart.AddDays(7),
        _ => RangeStart.AddMonths(1),
    };

    /// <summary>Título da janela, para o cabeçalho da grade.</summary>
    public string RangeLabel => ViewMode switch
    {
        CalendarViewMode.Day => string.Format(
            CultureInfo.InvariantCulture, "{0:dd/MM/yyyy}", RangeStart),
        CalendarViewMode.Week => string.Format(
            CultureInfo.InvariantCulture, "{0:dd/MM} a {1:dd/MM/yyyy}",
            RangeStart, RangeEnd.AddDays(-1)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:MM/yyyy}", RangeStart),
    };

    /// <summary>Abre a agenda no dia de hoje.</summary>
    public async Task InitializeAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        AccountId = accountId;
        AnchorDate = _timeProvider.GetUtcNow().ToLocalTime();

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Recarrega as marcações da janela atual.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Occurrences.Clear();

        var eventsInRange = await _events
            .ListAsync(AccountId, RangeStart, RangeEnd, cancellationToken)
            .ConfigureAwait(true);

        var byId = eventsInRange.ToDictionary(e => e.Id);

        var occurrences = await _events
            .ListOccurrencesAsync(AccountId, RangeStart, RangeEnd, cancellationToken)
            .ConfigureAwait(true);

        foreach (var occurrence in occurrences)
        {
            if (!byId.TryGetValue(occurrence.EventId, out var source))
            {
                continue;
            }

            Occurrences.Add(ToItem(source, occurrence));
        }

        OnPropertyChanged(nameof(RangeLabel));
    }

    /// <summary>Avança a janela.</summary>
    [RelayCommand]
    public Task NextRangeAsync(CancellationToken cancellationToken = default)
    {
        AnchorDate = ViewMode switch
        {
            CalendarViewMode.Day => AnchorDate.AddDays(1),
            CalendarViewMode.Week => AnchorDate.AddDays(7),
            _ => AnchorDate.AddMonths(1),
        };

        return RefreshAsync(cancellationToken);
    }

    /// <summary>Recua a janela.</summary>
    [RelayCommand]
    public Task PreviousRangeAsync(CancellationToken cancellationToken = default)
    {
        AnchorDate = ViewMode switch
        {
            CalendarViewMode.Day => AnchorDate.AddDays(-1),
            CalendarViewMode.Week => AnchorDate.AddDays(-7),
            _ => AnchorDate.AddMonths(-1),
        };

        return RefreshAsync(cancellationToken);
    }

    /// <summary>Volta para hoje.</summary>
    [RelayCommand]
    public Task GoToTodayAsync(CancellationToken cancellationToken = default)
    {
        AnchorDate = _timeProvider.GetUtcNow().ToLocalTime();
        return RefreshAsync(cancellationToken);
    }

    /// <summary>Troca a amplitude exibida.</summary>
    public Task SetViewModeAsync(CalendarViewMode mode, CancellationToken cancellationToken = default)
    {
        ViewMode = mode;
        return RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Move um compromisso para outro início.
    /// </summary>
    /// <remarks>
    /// A recusa não é um erro a esconder: ela vem com a explicação e com a oferta de propor
    /// novo horário, que é o caminho que o usuário queria de verdade.
    /// </remarks>
    public async Task MoveAsync(
        Guid eventId, DateTimeOffset newStart, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        CanProposeNewTime = false;
        RefusedStart = null;

        try
        {
            var result = await _move.MoveAsync(eventId, newStart, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.Message;
                CanProposeNewTime = result.CanProposeNewTime;
                RefusedStart = result.CanProposeNewTime ? newStart : null;
                return;
            }

            StatusMessage = result.Outcome == Domain.Services.EventMoveOutcome.MoveAndNotify
                ? "Compromisso remarcado. O convite atualizado foi para a fila de saída."
                : string.Empty;

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Aceita, recusa ou marca como provisório o compromisso selecionado.</summary>
    public async Task RespondAsync(
        Guid eventId, AttendeeResponse response, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _respond.RespondAsync(eventId, response, cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = result.Succeeded
                ? "Resposta entregue à fila de saída."
                : result.ErrorMessage ?? string.Empty;

            if (result.Succeeded)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Propõe ao organizador o horário que a movimentação recusou.</summary>
    [RelayCommand]
    public async Task ProposeRefusedTimeAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedOccurrence is not { } selected || RefusedStart is not { } start || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _respond
                .ProposeNewTimeAsync(selected.EventId, start, cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = result.Succeeded
                ? "Proposta de novo horário entregue à fila de saída."
                : result.ErrorMessage ?? string.Empty;

            CanProposeNewTime = false;
            RefusedStart = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Remove o compromisso selecionado.</summary>
    [RelayCommand]
    public async Task RemoveSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedOccurrence is not { } selected || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _events.RemoveAsync(selected.EventId, cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = result.Succeeded ? string.Empty : result.ErrorMessage ?? string.Empty;
            SelectedOccurrence = null;

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CalendarOccurrenceItem ToItem(CalendarEvent source, EventOccurrence occurrence)
    {
        var mine = AccountAddressOf(source);

        return new CalendarOccurrenceItem(
            source.Id,
            source.Summary.Length > 0 ? source.Summary : "(sem assunto)",
            occurrence.StartsAt,
            occurrence.EndsAt,
            source.IsAllDay,
            source.Status == CalendarEventStatus.Cancelled,
            occurrence.IsRecurrence,
            source.OrganizerDisplayName ?? source.OrganizerAddress?.Value ?? string.Empty,
            mine?.Response ?? AttendeeResponse.NeedsAction,
            source.MeetingUrl ?? string.Empty);
    }

    /// <summary>
    /// O participante que corresponde à conta dona do evento.
    /// </summary>
    /// <remarks>
    /// Achado pelo organizador quando a conta é quem organiza — nesse caso não há resposta
    /// pendente a dar. Nos demais, pelo endereço que aparece na lista.
    /// </remarks>
    private static EventAttendee? AccountAddressOf(CalendarEvent source)
        => source.OrganizerAddress is { } organizer
            ? source.AttendeeFor(organizer) is { } asOrganizer && source.Attendees.Count == 1
                ? asOrganizer
                : source.Attendees.FirstOrDefault(a => a.Address != organizer)
            : source.Attendees.FirstOrDefault();

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
        => new(value.Date, value.Offset);

    /// <summary>
    /// Segunda-feira da semana do dia informado.
    /// </summary>
    /// <remarks>
    /// Segunda fixa, e não o primeiro dia da semana da cultura: com
    /// <c>InvariantGlobalization</c> ligado a cultura invariante começa no domingo, e a
    /// agenda de trabalho brasileira começa na segunda.
    /// </remarks>
    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var start = StartOfDay(value);
        var offset = ((int)start.DayOfWeek + 6) % 7;

        return start.AddDays(-offset);
    }

    private static DateTimeOffset StartOfMonth(DateTimeOffset value)
        => new(new DateTime(value.Year, value.Month, 1), value.Offset);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnAnchorDateChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(RangeLabel));
    }

    partial void OnViewModeChanged(CalendarViewMode value)
    {
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(RangeLabel));
    }
}
