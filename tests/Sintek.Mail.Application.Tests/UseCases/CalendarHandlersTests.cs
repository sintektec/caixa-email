using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Calendar;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>Agenda em memória, com a mesma semântica do repositório real.</summary>
internal sealed class InMemoryCalendarRepository : ICalendarRepository
{
    private readonly List<CalendarEvent> _events = [];

    public IReadOnlyList<CalendarEvent> Events => _events;

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

    public Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(calendarEvent);
        return Task.CompletedTask;
    }

    public void Remove(CalendarEvent calendarEvent) => _events.Remove(calendarEvent);
}

/// <summary>
/// Cobre a importação de convites, a resposta pela fila de saída e a movimentação entre
/// datas — as três coisas que a agenda faz e que o usuário percebe.
/// </summary>
public class CalendarHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

    private readonly InMemoryCalendarRepository _calendar = new();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly ICalendarSerializer _serializer = Substitute.For<ICalendarSerializer>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

    private readonly Account _account;
    private readonly Folder _outboxFolder;

    private static EmailAddress Endereco(string value) => EmailAddress.Parse(value);

    public CalendarHandlersTests()
    {
        _account = Account.Create(
            _directory.Id, Endereco("contato@sintek.com.br"), "Contato", Now);

        _outboxFolder = Folder.Create(
            _account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
        _folders.GetByTypeAsync(_account.Id, FolderType.Outbox, Arg.Any<CancellationToken>())
            .Returns(_outboxFolder);

        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
    }

    private OutboxEnqueuer Enqueuer() => new(_outbox, _clock);

    private ImportInvitationHandler ImportHandler() => new(
        _calendar, _accounts, _directories, _serializer, _audit, _unitOfWork, _clock,
        NullLogger<ImportInvitationHandler>.Instance);

    private RespondToInvitationHandler RespondHandler() => new(
        _calendar, _accounts, _folders, _messages, _serializer, _unitOfWork, Enqueuer(), _clock,
        NullLogger<RespondToInvitationHandler>.Instance);

    private MoveEventHandler MoveHandler() => new(
        _calendar, _accounts, _folders, _messages, _serializer, _unitOfWork, Enqueuer(), _clock,
        NullLogger<MoveEventHandler>.Instance);

    private ManageEventsHandler EventsHandler() => new(
        _calendar, _accounts, _serializer, _unitOfWork, Enqueuer(), _folders, _messages, _clock,
        NullLogger<ManageEventsHandler>.Instance);

    /// <summary>Prepara o serializador para devolver o documento informado.</summary>
    private void ArrangeDocument(CalendarMethod method, params CalendarEventData[] events)
        => _serializer.Read(Arg.Any<string>())
            .Returns(new CalendarDocument(method, events));

    /// <summary>
    /// Monta um convite de teste.
    /// </summary>
    /// <remarks>
    /// A conta entra como participante por padrão porque é o caso normal — o convite chegou
    /// para ela. Sem isso, o Diretório de Domínio recusaria todos os convites, e é
    /// exatamente esse o comportamento que os testes de domínio verificam à parte.
    /// </remarks>
    private static CalendarEventData Convite(
        string uid = "uid-1",
        int sequence = 0,
        DateTimeOffset? startsAt = null,
        string organizer = "ana@cliente.com.br",
        bool includeAccount = true,
        params (string Address, AttendeeResponse Response)[] attendees)
    {
        var lista = attendees
            .Select(a => new CalendarAttendeeData(
                Endereco(a.Address), null, AttendeeRole.Required, a.Response))
            .ToList();

        if (includeAccount && !lista.Any(a => a.Address.Value == "contato@sintek.com.br"))
        {
            lista.Add(new CalendarAttendeeData(
                Endereco("contato@sintek.com.br"), "Contato", AttendeeRole.Required,
                AttendeeResponse.NeedsAction));
        }

        return new CalendarEventData
        {
            Uid = uid,
            Sequence = sequence,
            Summary = "Revisão do contrato",
            StartsAt = startsAt ?? Inicio,
            EndsAt = (startsAt ?? Inicio).AddHours(1),
            OrganizerAddress = Endereco(organizer),
            OrganizerDisplayName = "Ana Souza",
            Attendees = lista,
        };
    }

    [Fact]
    public async Task Import_ConviteNovo_CriaOCompromisso()
    {
        ArrangeDocument(
            CalendarMethod.Request,
            Convite(attendees: ("contato@sintek.com.br", AttendeeResponse.NeedsAction)));

        var resultado = await ImportHandler().ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.Created);
        _calendar.Events.Should().ContainSingle()
            .Which.StartsAt.Should().Be(Inicio);
    }

    [Fact]
    public async Task Import_MesmoUidComSequenciaMaior_Atualiza()
    {
        var handler = ImportHandler();

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 1));
        await handler.ImportAsync(_account.Id, "ics");

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 2, startsAt: Inicio.AddDays(1)));
        var resultado = await handler.ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.Updated);
        _calendar.Events.Should().ContainSingle()
            .Which.StartsAt.Should().Be(Inicio.AddDays(1));
    }

    [Fact]
    public async Task Import_ConviteAtrasado_NaoDesfazAAtualizacaoRecente()
    {
        // A regra que define a fase: o convite antigo mudaria a reunião de volta para o
        // horário errado.
        var handler = ImportHandler();

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 5, startsAt: Inicio.AddDays(1)));
        await handler.ImportAsync(_account.Id, "ics");

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 3, startsAt: Inicio));
        var resultado = await handler.ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.DiscardedAsStale);
        _calendar.Events[0].StartsAt.Should().Be(Inicio.AddDays(1));
    }

    [Fact]
    public async Task Import_ConviteAtrasado_RegistraEmAuditoria()
    {
        // Convite que some sem explicação parece defeito.
        var handler = ImportHandler();

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 5));
        await handler.ImportAsync(_account.Id, "ics");

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 3));
        await handler.ImportAsync(_account.Id, "ics");

        await _audit.Received().RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.InvitationOutOfOrderDiscarded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Import_Cancelamento_MarcaSemApagar()
    {
        var handler = ImportHandler();

        ArrangeDocument(CalendarMethod.Request, Convite(sequence: 1));
        await handler.ImportAsync(_account.Id, "ics");

        ArrangeDocument(CalendarMethod.Cancel, Convite(sequence: 2));
        var resultado = await handler.ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.Cancelled);
        _calendar.Events.Should().ContainSingle()
            .Which.Status.Should().Be(CalendarEventStatus.Cancelled);
    }

    [Fact]
    public async Task Import_CancelamentoDeCompromissoDesconhecido_EIgnorado()
    {
        ArrangeDocument(CalendarMethod.Cancel, Convite());

        var resultado = await ImportHandler().ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.Ignored);
        _calendar.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_Resposta_AtualizaSoOParticipante()
    {
        // Um REPLY não altera horário nem assunto; deixá-lo passar pelo caminho de
        // atualização faria a resposta reescrever o evento inteiro.
        var handler = ImportHandler();

        ArrangeDocument(
            CalendarMethod.Request,
            Convite(sequence: 1, attendees: ("bruno@cliente.com.br", AttendeeResponse.NeedsAction)));
        await handler.ImportAsync(_account.Id, "ics");

        ArrangeDocument(
            CalendarMethod.Reply,
            Convite(sequence: 1, startsAt: Inicio.AddDays(9),
                attendees: ("bruno@cliente.com.br", AttendeeResponse.Declined)));
        var resultado = await handler.ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.ResponseRecorded);
        _calendar.Events[0].StartsAt.Should().Be(Inicio, "a resposta não move a reunião");
        _calendar.Events[0].AttendeeFor(Endereco("bruno@cliente.com.br"))!
            .Response.Should().Be(AttendeeResponse.Declined);
    }

    [Fact]
    public async Task Import_DocumentoNaoInterpretavel_DevolveNotCalendar()
    {
        _serializer.Read(Arg.Any<string>()).Returns((CalendarDocument?)null);

        var resultado = await ImportHandler().ImportAsync(_account.Id, "lixo");

        resultado.Outcome.Should().Be(InvitationImportOutcome.NotCalendar);
    }

    [Fact]
    public async Task Import_MesmaMensagemDuasVezes_NaoDuplicaMesmoComUidNovo()
    {
        // A biblioteca inventa UID quando o documento não traz; sem a segunda via de
        // identidade, rebaixar o corpo criaria um compromisso a cada vez.
        var mensagem = Guid.CreateVersion7();
        var handler = ImportHandler();

        ArrangeDocument(CalendarMethod.Request, Convite(uid: "gerado-1"));
        await handler.ImportAsync(_account.Id, "ics", mensagem);

        ArrangeDocument(CalendarMethod.Request, Convite(uid: "gerado-2"));
        await handler.ImportAsync(_account.Id, "ics", mensagem);

        _calendar.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_ConviteForaDoDominioComBloqueio_Recusa()
    {
        // A mesma regra de Diretório de Domínio que vale para as mensagens.
        var directory = DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"), Now, invalidEmailAction: InvalidEmailAction.Block);

        _directories.GetByIdAsync(_account.DomainDirectoryId, Arg.Any<CancellationToken>())
            .Returns(directory);

        ArrangeDocument(
            CalendarMethod.Request,
            Convite(organizer: "ana@outraempresa.com", includeAccount: false,
                attendees: ("bruno@outraempresa.com", AttendeeResponse.NeedsAction)));

        var resultado = await ImportHandler().ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.BlockedByDomainRule);
        _calendar.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_ConviteForaDoDominioComAviso_EntraERegistra()
    {
        var directory = DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"), Now, invalidEmailAction: InvalidEmailAction.LogOnly);

        _directories.GetByIdAsync(_account.DomainDirectoryId, Arg.Any<CancellationToken>())
            .Returns(directory);

        ArrangeDocument(
            CalendarMethod.Request,
            Convite(organizer: "ana@outraempresa.com", includeAccount: false,
                attendees: ("bruno@outraempresa.com", AttendeeResponse.NeedsAction)));

        var resultado = await ImportHandler().ImportAsync(_account.Id, "ics");

        resultado.Outcome.Should().Be(InvitationImportOutcome.Created);
        resultado.Message.Should().NotBeEmpty();

        await _audit.Received().RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.InvitationBlockedByDomainRule),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Responder_Aceitando_EnfileiraOEnvioEMarcaLocalmente()
    {
        var handler = ImportHandler();
        ArrangeDocument(
            CalendarMethod.Request,
            Convite(attendees: ("contato@sintek.com.br", AttendeeResponse.NeedsAction)));
        await handler.ImportAsync(_account.Id, "ics");

        _serializer.WriteReply(
            Arg.Any<CalendarEventData>(), Arg.Any<EmailAddress>(), Arg.Any<AttendeeResponse>())
            .Returns("BEGIN:VCALENDAR\r\nMETHOD:REPLY\r\nEND:VCALENDAR");

        var resultado = await RespondHandler()
            .RespondAsync(_calendar.Events[0].Id, AttendeeResponse.Accepted);

        resultado.Succeeded.Should().BeTrue();

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());

        _calendar.Events[0].AttendeeFor(Endereco("contato@sintek.com.br"))!
            .Response.Should().Be(AttendeeResponse.Accepted);
    }

    [Fact]
    public async Task Responder_MensagemLevaAParteDeCalendario()
    {
        var handler = ImportHandler();
        ArrangeDocument(
            CalendarMethod.Request,
            Convite(attendees: ("contato@sintek.com.br", AttendeeResponse.NeedsAction)));
        await handler.ImportAsync(_account.Id, "ics");

        _serializer.WriteReply(
            Arg.Any<CalendarEventData>(), Arg.Any<EmailAddress>(), Arg.Any<AttendeeResponse>())
            .Returns("BEGIN:VCALENDAR\r\nMETHOD:REPLY\r\nEND:VCALENDAR");

        Message? gravada = null;
        await _messages.AddAsync(Arg.Do<Message>(m => gravada = m), Arg.Any<CancellationToken>());

        await RespondHandler().RespondAsync(_calendar.Events[0].Id, AttendeeResponse.Declined);

        gravada!.Body!.CalendarPayload.Should().Contain("METHOD:REPLY");
        gravada.Body.CalendarMethod.Should().Be("REPLY");
        gravada.Subject.Should().StartWith("Recusado:");
    }

    [Fact]
    public async Task Responder_CompromissoSemOrganizador_Recusa()
    {
        var proprio = CalendarEvent.Create(_account.Id, "proprio", "Foco", Inicio, Inicio, Now);
        await _calendar.AddAsync(proprio);

        var resultado = await RespondHandler().RespondAsync(proprio.Id, AttendeeResponse.Accepted);

        resultado.Succeeded.Should().BeFalse();
        resultado.ErrorMessage.Should().Contain("organizador");
    }

    [Fact]
    public async Task ProporNovoHorario_GeraCounterPelaFila()
    {
        var handler = ImportHandler();
        ArrangeDocument(
            CalendarMethod.Request,
            Convite(attendees: ("contato@sintek.com.br", AttendeeResponse.NeedsAction)));
        await handler.ImportAsync(_account.Id, "ics");

        _serializer.WriteCounter(
            Arg.Any<CalendarEventData>(), Arg.Any<EmailAddress>(), Arg.Any<DateTimeOffset>())
            .Returns("BEGIN:VCALENDAR\r\nMETHOD:COUNTER\r\nEND:VCALENDAR");

        Message? gravada = null;
        await _messages.AddAsync(Arg.Do<Message>(m => gravada = m), Arg.Any<CancellationToken>());

        var resultado = await RespondHandler()
            .ProposeNewTimeAsync(_calendar.Events[0].Id, Inicio.AddDays(2));

        resultado.Succeeded.Should().BeTrue();
        gravada!.Body!.CalendarMethod.Should().Be("COUNTER");
    }

    [Fact]
    public async Task Mover_ComoOrganizador_ReenviaOConviteAtualizado()
    {
        var evento = CalendarEvent.Create(_account.Id, "meu-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.SetOrganizer(_account.EmailAddress, "Contato", Now);
        evento.AddAttendee(Endereco("ana@cliente.com.br"), Now);
        await _calendar.AddAsync(evento);

        _serializer.WriteRequest(Arg.Any<CalendarEventData>())
            .Returns("BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nEND:VCALENDAR");

        var resultado = await MoveHandler().MoveAsync(evento.Id, Inicio.AddDays(1));

        resultado.Succeeded.Should().BeTrue();
        resultado.Outcome.Should().Be(EventMoveOutcome.MoveAndNotify);
        evento.StartsAt.Should().Be(Inicio.AddDays(1));
        evento.Sequence.Should().Be(1);

        await _outbox.Received(1).AddAsync(
            Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mover_CompromissoProprio_NaoEnfileiraNada()
    {
        var evento = CalendarEvent.Create(_account.Id, "meu-2", "Foco", Inicio, Inicio.AddHours(1), Now);
        await _calendar.AddAsync(evento);

        var resultado = await MoveHandler().MoveAsync(evento.Id, Inicio.AddDays(1));

        resultado.Succeeded.Should().BeTrue();
        resultado.Outcome.Should().Be(EventMoveOutcome.MoveLocally);
        evento.Sequence.Should().Be(0);

        await _outbox.DidNotReceive().AddAsync(
            Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mover_ReuniaoDeOutro_RecusaEPreservaOHorario()
    {
        var evento = CalendarEvent.Create(_account.Id, "dela-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.SetOrganizer(Endereco("ana@cliente.com.br"), "Ana", Now);
        evento.AddAttendee(_account.EmailAddress, Now);
        evento.AddAttendee(Endereco("bruno@cliente.com.br"), Now);
        await _calendar.AddAsync(evento);

        var resultado = await MoveHandler().MoveAsync(evento.Id, Inicio.AddDays(1));

        resultado.Succeeded.Should().BeFalse();
        resultado.CanProposeNewTime.Should().BeTrue();
        evento.StartsAt.Should().Be(Inicio);
    }

    [Fact]
    public async Task Salvar_CompromissoNovo_NasceOrganizadoPelaConta()
    {
        var resultado = await EventsHandler().SaveAsync(new CalendarEventCommand
        {
            AccountId = _account.Id,
            Summary = "Planejamento",
            StartsAt = Inicio,
            EndsAt = Inicio.AddHours(2),
        });

        resultado.Succeeded.Should().BeTrue();
        _calendar.Events.Should().ContainSingle()
            .Which.OrganizerAddress!.Value.Should().Be("contato@sintek.com.br");
    }

    [Fact]
    public async Task Salvar_FimAntesDoInicio_Recusa()
    {
        var resultado = await EventsHandler().SaveAsync(new CalendarEventCommand
        {
            AccountId = _account.Id,
            Summary = "Planejamento",
            StartsAt = Inicio,
            EndsAt = Inicio.AddHours(-1),
        });

        resultado.Succeeded.Should().BeFalse();
        resultado.ErrorMessage.Should().Contain("anterior");
    }

    [Fact]
    public async Task Salvar_SemAssunto_Recusa()
    {
        var resultado = await EventsHandler().SaveAsync(new CalendarEventCommand
        {
            AccountId = _account.Id,
            Summary = "   ",
            StartsAt = Inicio,
            EndsAt = Inicio.AddHours(1),
        });

        resultado.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Remover_ReuniaoComParticipantes_EnviaCancelamentoAntes()
    {
        var evento = CalendarEvent.Create(_account.Id, "meu-3", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.SetOrganizer(_account.EmailAddress, "Contato", Now);
        evento.AddAttendee(Endereco("ana@cliente.com.br"), Now);
        await _calendar.AddAsync(evento);

        _serializer.WriteCancel(Arg.Any<CalendarEventData>())
            .Returns("BEGIN:VCALENDAR\r\nMETHOD:CANCEL\r\nEND:VCALENDAR");

        await EventsHandler().RemoveAsync(evento.Id);

        _calendar.Events.Should().BeEmpty();
        await _outbox.Received(1).AddAsync(
            Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remover_CompromissoProprio_SomeSemAvisarNinguem()
    {
        var evento = CalendarEvent.Create(_account.Id, "meu-4", "Foco", Inicio, Inicio.AddHours(1), Now);
        evento.SetOrganizer(_account.EmailAddress, "Contato", Now);
        await _calendar.AddAsync(evento);

        await EventsHandler().RemoveAsync(evento.Id);

        _calendar.Events.Should().BeEmpty();
        await _outbox.DidNotReceive().AddAsync(
            Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListarOcorrencias_EventoSimples_DevolveUma()
    {
        var evento = CalendarEvent.Create(_account.Id, "s-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        await _calendar.AddAsync(evento);

        var ocorrencias = await EventsHandler()
            .ListOccurrencesAsync(_account.Id, Inicio.AddDays(-1), Inicio.AddDays(30));

        ocorrencias.Should().ContainSingle()
            .Which.IsRecurrence.Should().BeFalse();
    }

    [Fact]
    public async Task ListarOcorrencias_EventoSemanal_ExpandeNaJanela()
    {
        var evento = CalendarEvent.Create(_account.Id, "r-1", "Semanal", Inicio, Inicio.AddHours(1), Now);
        evento.SetRecurrence("FREQ=WEEKLY;COUNT=3", Now);
        await _calendar.AddAsync(evento);

        _serializer.ExpandOccurrences(
            "FREQ=WEEKLY;COUNT=3", Inicio, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns([Inicio, Inicio.AddDays(7), Inicio.AddDays(14)]);

        var ocorrencias = await EventsHandler()
            .ListOccurrencesAsync(_account.Id, Inicio.AddDays(-1), Inicio.AddDays(30));

        ocorrencias.Should().HaveCount(3);
        ocorrencias[0].IsRecurrence.Should().BeFalse("a primeira é o encontro original");
        ocorrencias[1].IsRecurrence.Should().BeTrue();
        (ocorrencias[1].EndsAt - ocorrencias[1].StartsAt).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ListarOcorrencias_EventoCancelado_ContinuaNaGrade()
    {
        // Sumir da grade é indistinguível de erro de sincronização; quem reservou o horário
        // precisa ver que a reunião caiu.
        var evento = CalendarEvent.Create(_account.Id, "c-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.Cancel(1, Now);
        await _calendar.AddAsync(evento);

        var ocorrencias = await EventsHandler()
            .ListOccurrencesAsync(_account.Id, Inicio.AddDays(-1), Inicio.AddDays(30));

        ocorrencias.Should().ContainSingle();
    }
}
