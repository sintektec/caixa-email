using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Calendar;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>Agenda em memória para as verificações de tela.</summary>
internal sealed class FakeCalendarRepository : ICalendarRepository
{
    private readonly List<CalendarEvent> _events = [];

    public Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_events.FirstOrDefault(e => e.Id == id));

    public Task<CalendarEvent?> GetByUidAsync(
        Guid accountId, string uid, CancellationToken cancellationToken = default)
        => Task.FromResult(_events.FirstOrDefault(e => e.AccountId == accountId && e.Uid == uid));

    public Task<CalendarEvent?> GetBySourceMessageAsync(
        Guid messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(_events.FirstOrDefault(e => e.SourceMessageId == messageId));

    public Task<IReadOnlyList<CalendarEvent>> ListInRangeAsync(
        Guid? accountId, DateTimeOffset from, DateTimeOffset until,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CalendarEvent>>(
            [.. _events
                .Where(e => (accountId == null || e.AccountId == accountId)
                    && e.StartsAt < until
                    && (e.IsRecurring || e.EndsAt > from))
                .OrderBy(e => e.StartsAt)]);

    public Task<CalendarEvent?> GetByRemoteHrefAsync(
        Guid remoteCalendarId, string href, CancellationToken cancellationToken = default)
        => Task.FromResult(_events.FirstOrDefault(
            e => e.RemoteCalendarId == remoteCalendarId
                && string.Equals(e.RemoteHref, href, StringComparison.Ordinal)));

    public Task<IReadOnlyList<CalendarEvent>> ListPendingAsync(
        Guid remoteCalendarId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CalendarEvent>>(
            [.. _events.Where(e => e.RemoteCalendarId == remoteCalendarId
                && e.SyncState is CalendarSyncState.PendingCreate
                    or CalendarSyncState.PendingUpdate
                    or CalendarSyncState.PendingDelete)]);

    public Task<IReadOnlyList<string>> ListRemoteHrefsAsync(
        Guid remoteCalendarId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            [.. _events
                .Where(e => e.RemoteCalendarId == remoteCalendarId && e.RemoteHref is not null)
                .Select(e => e.RemoteHref!)]);

    public Task<IReadOnlyList<CalendarEvent>> ListConflictedAsync(
        Guid? accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CalendarEvent>>(
            [.. _events.Where(e => (accountId == null || e.AccountId == accountId)
                && e.SyncState == CalendarSyncState.Conflict)]);

    public Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(calendarEvent);
        return Task.CompletedTask;
    }

    public void Remove(CalendarEvent calendarEvent) => _events.Remove(calendarEvent);
}

/// <summary>
/// Cobre a grade da agenda: janelas de dia, semana e mês, e o que acontece ao arrastar um
/// compromisso — inclusive a recusa, que precisa chegar à tela com a explicação e a
/// alternativa.
/// </summary>
public class CalendarViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);

    private readonly FakeCalendarRepository _calendar = new();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly ICalendarSerializer _serializer = Substitute.For<ICalendarSerializer>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

    private readonly Folder _outboxFolder;

    private static EmailAddress Endereco(string value) => EmailAddress.Parse(value);

    public CalendarViewModelTests()
    {
        _outboxFolder = Folder.Create(
            _account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _folders.GetByTypeAsync(_account.Id, FolderType.Outbox, Arg.Any<CancellationToken>())
            .Returns(_outboxFolder);

        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
    }

    private CalendarViewModel CreateViewModel()
    {
        var enqueuer = new OutboxEnqueuer(_outbox, _clock);

        return new CalendarViewModel(
            new ManageEventsHandler(
                _calendar, _accounts, _serializer, _unitOfWork, enqueuer, _folders, _messages, _clock,
                NullLogger<ManageEventsHandler>.Instance),
            new MoveEventHandler(
                _calendar, _accounts, _folders, _messages, _serializer, _unitOfWork, enqueuer, _clock,
                NullLogger<MoveEventHandler>.Instance),
            new RespondToInvitationHandler(
                _calendar, _accounts, _folders, _messages, _serializer, _unitOfWork, enqueuer, _clock,
                NullLogger<RespondToInvitationHandler>.Instance),
            _clock);
    }

    private async Task<CalendarEvent> ArrangeEventAsync(
        string summary = "Reunião",
        DateTimeOffset? startsAt = null,
        EmailAddress? organizer = null,
        params EmailAddress[] attendees)
    {
        var evento = CalendarEvent.Create(
            _account.Id, $"uid-{Guid.CreateVersion7():N}", summary,
            startsAt ?? Inicio, (startsAt ?? Inicio).AddHours(1), Now);

        if (organizer is not null)
        {
            evento.SetOrganizer(organizer, "Organizador", Now);
        }

        foreach (var attendee in attendees)
        {
            evento.AddAttendee(attendee, Now);
        }

        await _calendar.AddAsync(evento);
        return evento;
    }

    [Fact]
    public async Task Inicializar_AbreNoDiaDeHojeECarregaAsMarcacoes()
    {
        await ArrangeEventAsync();

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences.Should().ContainSingle();
        viewModel.ViewMode.Should().Be(CalendarViewMode.Week);
    }

    [Fact]
    public async Task JanelaSemanal_ComecaNaSegundaFeira()
    {
        // Segunda fixa, não o primeiro dia da cultura: a cultura invariante começa no
        // domingo, e a agenda de trabalho brasileira começa na segunda.
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.RangeStart.DayOfWeek.Should().Be(DayOfWeek.Monday);
        (viewModel.RangeEnd - viewModel.RangeStart).Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task JanelaDiaria_CobreVinteEQuatroHoras()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.SetViewModeAsync(CalendarViewMode.Day);

        (viewModel.RangeEnd - viewModel.RangeStart).Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task JanelaMensal_ComecaNoPrimeiroDia()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.SetViewModeAsync(CalendarViewMode.Month);

        viewModel.RangeStart.Day.Should().Be(1);
        viewModel.RangeEnd.Should().Be(viewModel.RangeStart.AddMonths(1));
    }

    [Fact]
    public async Task Avancar_NaVisaoSemanal_PulaSeteDias()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        var inicial = viewModel.RangeStart;

        await viewModel.NextRangeAsync();

        viewModel.RangeStart.Should().Be(inicial.AddDays(7));
    }

    [Fact]
    public async Task Voltar_DepoisDeAvancar_RetornaAoMesmoLugar()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        var inicial = viewModel.RangeStart;

        await viewModel.NextRangeAsync();
        await viewModel.PreviousRangeAsync();

        viewModel.RangeStart.Should().Be(inicial);
    }

    [Fact]
    public async Task IrParaHoje_DepoisDeNavegar_VoltaParaASemanaAtual()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        var inicial = viewModel.RangeStart;

        await viewModel.NextRangeAsync();
        await viewModel.NextRangeAsync();
        await viewModel.GoToTodayAsync();

        viewModel.RangeStart.Should().Be(inicial);
    }

    [Fact]
    public async Task Marcacao_ComHorario_MostraAFaixa()
    {
        await ArrangeEventAsync(startsAt: Inicio);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences[0].TimeRange.Should().Contain("–");
    }

    [Fact]
    public async Task Marcacao_DeDiaInteiro_MostraORotuloEmVezDoHorario()
    {
        var evento = await ArrangeEventAsync();
        evento.SetAllDay(true, Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences[0].TimeRange.Should().Be("Dia inteiro");
    }

    [Fact]
    public async Task Marcacao_DeCompromissoCancelado_ContinuaNaGradeMarcada()
    {
        var evento = await ArrangeEventAsync();
        evento.Cancel(1, Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        var item = viewModel.Occurrences.Should().ContainSingle().Subject;
        item.IsCancelled.Should().BeTrue();
        item.NeedsResponse.Should().BeFalse("não se responde a uma reunião cancelada");
    }

    [Fact]
    public async Task Mover_ComoOrganizador_AvisaQueOConviteFoiParaAFila()
    {
        var evento = await ArrangeEventAsync(
            organizer: _account.EmailAddress, attendees: Endereco("ana@cliente.com.br"));

        _serializer.WriteRequest(Arg.Any<CalendarEventData>()).Returns("BEGIN:VCALENDAR");

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.MoveAsync(evento.Id, Inicio.AddDays(1));

        viewModel.StatusMessage.Should().Contain("fila de saída");
        viewModel.CanProposeNewTime.Should().BeFalse();
    }

    [Fact]
    public async Task Mover_ReuniaoDeOutro_TrazAExplicacaoEAAlternativa()
    {
        // A recusa não é erro a esconder: vem com o motivo e com o caminho que o usuário
        // queria de verdade.
        var evento = await ArrangeEventAsync(
            organizer: Endereco("ana@cliente.com.br"),
            attendees: [_account.EmailAddress, Endereco("bruno@cliente.com.br")]);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.MoveAsync(evento.Id, Inicio.AddDays(1));

        viewModel.StatusMessage.Should().Contain("Proponha");
        viewModel.CanProposeNewTime.Should().BeTrue();
        viewModel.RefusedStart.Should().Be(Inicio.AddDays(1));
        evento.StartsAt.Should().Be(Inicio, "o compromisso não se moveu");
    }

    [Fact]
    public async Task ProporHorarioRecusado_EnfileiraAProposta()
    {
        var evento = await ArrangeEventAsync(
            organizer: Endereco("ana@cliente.com.br"),
            attendees: [_account.EmailAddress, Endereco("bruno@cliente.com.br")]);

        _serializer.WriteCounter(
            Arg.Any<CalendarEventData>(), Arg.Any<EmailAddress>(), Arg.Any<DateTimeOffset>())
            .Returns("BEGIN:VCALENDAR");

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        await viewModel.MoveAsync(evento.Id, Inicio.AddDays(1));

        viewModel.SelectedOccurrence = viewModel.Occurrences.First(o => o.EventId == evento.Id);
        await viewModel.ProposeRefusedTimeAsync();

        viewModel.StatusMessage.Should().Contain("Proposta");
        viewModel.CanProposeNewTime.Should().BeFalse();

        await _outbox.Received().AddAsync(Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Responder_Aceitando_AvisaEAtualizaAMarcacao()
    {
        var evento = await ArrangeEventAsync(
            organizer: Endereco("ana@cliente.com.br"), attendees: _account.EmailAddress);

        _serializer.WriteReply(
            Arg.Any<CalendarEventData>(), Arg.Any<EmailAddress>(), Arg.Any<AttendeeResponse>())
            .Returns("BEGIN:VCALENDAR");

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.RespondAsync(evento.Id, AttendeeResponse.Accepted);

        viewModel.StatusMessage.Should().Contain("fila de saída");
        viewModel.Occurrences[0].ResponseLabel.Should().Be("Aceito");
        viewModel.Occurrences[0].NeedsResponse.Should().BeFalse();
    }

    [Fact]
    public async Task Remover_CompromissoSelecionado_SaiDaGrade()
    {
        var evento = await ArrangeEventAsync(organizer: _account.EmailAddress);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.SelectedOccurrence = viewModel.Occurrences[0];

        await viewModel.RemoveSelectedAsync();

        viewModel.Occurrences.Should().BeEmpty();
    }

    [Fact]
    public async Task Marcacao_ComLinkDeReuniao_ExpoeOBotaoDeEntrar()
    {
        var evento = await ArrangeEventAsync();
        evento.SetDetails("Reunião", null, null, "https://teams.microsoft.com/l/meetup-join/abc", Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences[0].HasMeetingUrl.Should().BeTrue();
    }

    [Fact]
    public async Task Marcacao_SemAssunto_MostraORotuloPadrao()
    {
        await ArrangeEventAsync(summary: "");

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences[0].Summary.Should().Be("(sem assunto)");
    }

    [Fact]
    public async Task Refresh_ForaDaJanela_NaoTrazAMarcacao()
    {
        await ArrangeEventAsync(startsAt: Inicio.AddDays(60));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences.Should().BeEmpty();
    }

    /// <summary>
    /// O conflito precisa aparecer com o compromisso, e não só numa tela separada: um
    /// conflito escondido é um conflito que ninguém resolve.
    /// </summary>
    [Fact]
    public async Task Refresh_CompromissoEmConflito_ApareceNaFaixaENaMarcacao()
    {
        var evento = await ArrangeEventAsync();
        evento.BindToRemoteCalendar(Guid.CreateVersion7(), Now);
        evento.MarkRemoteSynced("https://dav.exemplo.com/cal/1.ics", "\"1\"", null, Now);
        evento.MarkConflicted(Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.HasConflicts.Should().BeTrue();
        viewModel.ConflictSummary.Should().Contain("Escolha qual versão fica");
        viewModel.Conflicts.Should().ContainSingle().Which.EventId.Should().Be(evento.Id);
        viewModel.Occurrences[0].IsConflicted.Should().BeTrue();
    }

    /// <summary>
    /// Um conflito pode estar em qualquer data, inclusive fora do que a grade mostra agora.
    /// Filtrar pela janela esconderia justamente o que precisa de atenção.
    /// </summary>
    [Fact]
    public async Task Refresh_ConflitoForaDaJanela_ContinuaVisivel()
    {
        var evento = await ArrangeEventAsync(startsAt: Inicio.AddDays(60));
        evento.BindToRemoteCalendar(Guid.CreateVersion7(), Now);
        evento.MarkRemoteSynced("https://dav.exemplo.com/cal/1.ics", "\"1\"", null, Now);
        evento.MarkConflicted(Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Occurrences.Should().BeEmpty();
        viewModel.Conflicts.Should().ContainSingle();
    }

    [Fact]
    public async Task KeepLocalVersion_ConflitoResolvido_VoltaParaAFilaDeEnvio()
    {
        var evento = await ArrangeEventAsync();
        evento.BindToRemoteCalendar(Guid.CreateVersion7(), Now);
        evento.MarkRemoteSynced("https://dav.exemplo.com/cal/1.ics", "\"1\"", null, Now);
        evento.MarkConflicted(Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.KeepLocalVersionAsync(evento.Id);

        evento.SyncState.Should().Be(CalendarSyncState.PendingUpdate);
        viewModel.HasConflicts.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("sua versão");
    }

    /// <summary>
    /// Aceitar o servidor descarta o ETag conhecido: mantê-lo faria a passada seguinte
    /// concluir que os dois lados estão iguais e deixaria a versão local — a que o usuário
    /// acabou de descartar — como a final.
    /// </summary>
    [Fact]
    public async Task KeepServerVersion_ConflitoResolvido_DescartaOEtagConhecido()
    {
        var evento = await ArrangeEventAsync();
        evento.BindToRemoteCalendar(Guid.CreateVersion7(), Now);
        evento.MarkRemoteSynced("https://dav.exemplo.com/cal/1.ics", "\"1\"", null, Now);
        evento.MarkConflicted(Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.KeepServerVersionAsync(evento.Id);

        evento.SyncState.Should().Be(CalendarSyncState.Synced);
        evento.RemoteETag.Should().BeNull();
        viewModel.HasConflicts.Should().BeFalse();
    }
}
