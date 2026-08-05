using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.Tests.UseCases;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.Sync;

/// <summary>
/// Um provedor de calendário roteirizado: os testes descrevem o que o servidor responde e
/// verificam o que o motor faz com isso.
/// </summary>
internal sealed class ScriptedCalendarProvider : ICalendarSyncProvider
{
    private readonly Queue<RemoteCalendarChanges> _changes = new();

    public CalendarProviderKind Provider { get; init; } = CalendarProviderKind.CalDav;

    public List<RemoteCalendarDescriptor> Collections { get; } = [];

    public Dictionary<string, CalendarEventData> Resources { get; } = new(StringComparer.Ordinal);

    public List<string> Created { get; } = [];

    public List<string> Updated { get; } = [];

    public List<string> Deleted { get; } = [];

    public bool NextWriteConflicts { get; set; }

    public int FetchResourceCalls { get; private set; }

    public void EnqueueChanges(RemoteCalendarChanges changes) => _changes.Enqueue(changes);

    public Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
        Account account, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteCalendarDescriptor>>(Collections);

    public Task<Abstractions.Mail.ConnectionTestResult> TestAsync(
        Account account, CancellationToken cancellationToken = default)
        => Task.FromResult(Abstractions.Mail.ConnectionTestResult.Success());

    public Task<RemoteCalendarChanges> FetchChangesAsync(
        Account account, RemoteCalendar calendar, CancellationToken cancellationToken = default)
        => Task.FromResult(_changes.Count > 0
            ? _changes.Dequeue()
            : new RemoteCalendarChanges([], calendar.SyncToken, calendar.CTag, false, false));

    public Task<IReadOnlyList<RemoteCalendarChange>> FetchResourcesAsync(
        Account account,
        RemoteCalendar calendar,
        IReadOnlyCollection<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        FetchResourceCalls++;

        return Task.FromResult<IReadOnlyList<RemoteCalendarChange>>(
            [.. hrefs
                .Where(Resources.ContainsKey)
                .Select(h => RemoteCalendarChange.Upserted(
                    h, "\"1\"", Resources[h],
                    RemoteVersion.FromSequence(Resources[h].Sequence)))]);
    }

    public Task<RemoteWriteResult> CreateAsync(
        Account account, RemoteCalendar calendar, CalendarEventData calendarEvent,
        CancellationToken cancellationToken = default)
    {
        if (TakeConflict() is { } conflict)
        {
            return Task.FromResult(conflict);
        }

        var href = $"https://dav.exemplo.com/cal/{Created.Count}.ics";
        Created.Add(href);
        Resources[href] = calendarEvent;

        return Task.FromResult(RemoteWriteResult.Success(href, "\"novo\""));
    }

    public Task<RemoteWriteResult> UpdateAsync(
        Account account, RemoteCalendar calendar, string href, string? knownETag,
        CalendarEventData calendarEvent, CancellationToken cancellationToken = default)
    {
        if (TakeConflict() is { } conflict)
        {
            return Task.FromResult(conflict);
        }

        Updated.Add(href);
        Resources[href] = calendarEvent;

        return Task.FromResult(RemoteWriteResult.Success(href, "\"atualizado\""));
    }

    public Task<RemoteWriteResult> DeleteAsync(
        Account account, RemoteCalendar calendar, string href, string? knownETag,
        CancellationToken cancellationToken = default)
    {
        if (TakeConflict() is { } conflict)
        {
            return Task.FromResult(conflict);
        }

        Deleted.Add(href);
        Resources.Remove(href);

        return Task.FromResult(RemoteWriteResult.Success(href, null));
    }

    private RemoteWriteResult? TakeConflict()
    {
        if (!NextWriteConflicts)
        {
            return null;
        }

        NextWriteConflicts = false;

        return RemoteWriteResult.Conflict("O servidor mudou antes.");
    }
}

/// <summary>
/// Cobre o motor de sincronização bidirecional de agenda: a ordem envio-antes-de-leitura, o
/// conflito que fica visível, e a remoção que só acontece em passada completa.
/// </summary>
public class CalendarSyncServiceTests
{
    private const string Colecao = "https://dav.exemplo.com/cal/";

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

    private readonly InMemoryCalendarRepository _events = new();
    private readonly InMemoryRemoteCalendarRepository _calendars = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ScriptedCalendarProvider _provider = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

    public CalendarSyncServiceTests()
    {
        _account.ConfigureCalendar(CalendarProviderKind.CalDav, Colecao, syncEnabled: true, Now);

        _provider.Collections.Add(new RemoteCalendarDescriptor(
            Colecao, "Agenda", "#FF5733FF", IsReadOnly: false, CTag: "1", SyncToken: null));

    }

    private CalendarSyncService CreateService()
        => new(
            _calendars, _events, [_provider], _unitOfWork, _clock,
            NullLogger<CalendarSyncService>.Instance);

    private void GivenDocument(string href, string uid, DateTimeOffset startsAt, int sequence = 0)
        => _provider.Resources[href] = new CalendarEventData
        {
            Uid = uid,
            Sequence = sequence,
            Summary = "Reunião de projeto",
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(1),
        };

    private RemoteCalendar GivenLocalCalendar(string? syncToken = null)
    {
        var calendar = RemoteCalendar.Create(
            _account.Id, CalendarProviderKind.CalDav, Colecao, "Agenda", Now);

        calendar.SetSyncEnabled(true, Now);

        if (syncToken is not null)
        {
            calendar.MarkSynced(syncToken, "1", Now);
        }

        _calendars.AddAsync(calendar).GetAwaiter().GetResult();

        return calendar;
    }

    [Fact]
    public async Task SyncAsync_ColecaoNovaNoServidor_EEspelhada()
    {
        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.CalendarsMirrored.Should().Be(1);
        _calendars.Calendars.Should().ContainSingle()
            .Which.CollectionUrl.Should().Be(Colecao);
    }

    /// <summary>
    /// Mesma regra do <c>FolderMirrorService</c>: uma listagem incompleta é indistinguível
    /// de uma exclusão real, e o custo dos dois erros não é simétrico.
    /// </summary>
    [Fact]
    public async Task SyncAsync_ColecaoQueSomeDaListagem_DesligaSemApagar()
    {
        var calendar = GivenLocalCalendar();
        _provider.Collections.Clear();
        _provider.Collections.Add(new RemoteCalendarDescriptor(
            "https://dav.exemplo.com/outra/", "Outra", null, false, null, null));

        await CreateService().SyncAsync(_account, _provider);

        _calendars.Calendars.Should().Contain(c => c.Id == calendar.Id);
        calendar.SyncEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SyncAsync_RecursoNovoNoServidor_ViraCompromissoLocal()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";
        GivenDocument(href, "uid-1", Inicio);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Listed(href, "\"1\"")],
            "token-1", "2", HasMore: false, IsFullEnumeration: true));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.Added.Should().Be(1);

        var criado = _events.Events.Should().ContainSingle().Subject;
        criado.Uid.Should().Be("uid-1");
        criado.RemoteHref.Should().Be(href);
        criado.RemoteETag.Should().Be("\"1\"");
        criado.RemoteCalendarId.Should().Be(calendar.Id);
        criado.SyncState.Should().Be(CalendarSyncState.Synced);
        calendar.SyncToken.Should().Be("token-1");
    }

    /// <summary>
    /// O documento só é buscado para o que a decisão vai usar. O caminho do <c>CTag</c>
    /// lista a coleção inteira a cada alteração, e baixar tudo o que apareceu significaria
    /// milhares de recursos para aplicar dois.
    /// </summary>
    [Fact]
    public async Task SyncAsync_EtagInalterado_NaoBuscaODocumento()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var existente = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        existente.BindToRemoteCalendar(calendar.Id, Now);
        existente.MarkRemoteSynced(href, "\"1\"", null, Now);
        await _events.AddAsync(existente);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Listed(href, "\"1\"")],
            "token-2", "2", HasMore: false, IsFullEnumeration: true));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.Added.Should().Be(0);
        resultado.Updated.Should().Be(0);
        _provider.FetchResourceCalls.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_AlteracaoLocalPendente_SobeAntesDaLeitura()
    {
        var calendar = GivenLocalCalendar();

        var local = CalendarEvent.Create(_account.Id, "uid-local", "Novo", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        await _events.AddAsync(local);

        await CreateService().SyncAsync(_account, _provider);

        _provider.Created.Should().ContainSingle();
        local.SyncState.Should().Be(CalendarSyncState.Synced);
        local.RemoteHref.Should().Be(_provider.Created[0]);
    }

    [Fact]
    public async Task SyncAsync_ExclusaoLocalPendente_ApagaNoServidorEAqui()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        local.MarkPendingDelete(Now);
        await _events.AddAsync(local);

        await CreateService().SyncAsync(_account, _provider);

        _provider.Deleted.Should().Contain(href);
        _events.Events.Should().BeEmpty();
    }

    /// <summary>
    /// Conflito não é resolvido em silêncio: qualquer escolha automática descarta o trabalho
    /// de alguém, e a pessoa só descobre quando procura o que escreveu e não acha.
    /// </summary>
    [Fact]
    public async Task SyncAsync_ServidorRecusaPorPrecondicao_MarcaConflito()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        local.MoveTo(Inicio.AddHours(2), Now, incrementSequence: false);
        await _events.AddAsync(local);

        _provider.NextWriteConflicts = true;

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.Pushed.Should().Be(0);
        local.SyncState.Should().Be(CalendarSyncState.Conflict);
    }

    [Fact]
    public async Task SyncAsync_RecursoRemovidoNoServidor_ApagaACopiaLocal()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        await _events.AddAsync(local);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Removed(href)],
            "token-3", "3", HasMore: false, IsFullEnumeration: false));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.RemovedLocally.Should().Be(1);
        _events.Events.Should().BeEmpty();
    }

    /// <summary>
    /// Numa passada incremental o servidor manda só o que mudou. Apagar o que não veio
    /// esvaziaria a agenda a cada sincronização em que nada aconteceu.
    /// </summary>
    [Fact]
    public async Task SyncAsync_PassadaIncrementalSemNovidade_NaoApagaNada()
    {
        var calendar = GivenLocalCalendar("token-antigo");
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        await _events.AddAsync(local);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [], "token-novo", "3", HasMore: false, IsFullEnumeration: false));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.RemovedLocally.Should().Be(0);
        _events.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task SyncAsync_PassadaCompletaSemORecurso_ApagaACopiaLocal()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/sumiu.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        await _events.AddAsync(local);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [], "token-4", "4", HasMore: false, IsFullEnumeration: true));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.RemovedLocally.Should().Be(1);
        _events.Events.Should().BeEmpty();
    }

    /// <summary>
    /// O compromisso criado offline ainda não subiu; o servidor não teria como listá-lo, e a
    /// ausência dele numa passada completa não significa exclusão.
    /// </summary>
    [Fact]
    public async Task SyncAsync_PassadaCompletaComPendenteLocal_PreservaOPendente()
    {
        var calendar = GivenLocalCalendar();
        _provider.Collections[0] = _provider.Collections[0] with { IsReadOnly = true };

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced("https://dav.exemplo.com/cal/abcd.ics", "\"1\"", null, Now);
        local.MoveTo(Inicio.AddHours(3), Now, incrementSequence: false);
        await _events.AddAsync(local);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [], "token-5", "5", HasMore: false, IsFullEnumeration: true));

        await CreateService().SyncAsync(_account, _provider);

        _events.Events.Should().ContainSingle();
        local.SyncState.Should().Be(CalendarSyncState.PendingUpdate);
    }

    /// <summary>
    /// Calendário compartilhado só para leitura devolveria 403 a cada tentativa, e a fila
    /// retentaria para sempre uma operação que nunca vai passar.
    /// </summary>
    [Fact]
    public async Task SyncAsync_ColecaoSomenteLeitura_NaoTentaEscrever()
    {
        var calendar = GivenLocalCalendar();
        _provider.Collections[0] = _provider.Collections[0] with { IsReadOnly = true };

        var local = CalendarEvent.Create(_account.Id, "uid-local", "Novo", Inicio, Inicio.AddHours(1), Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        await _events.AddAsync(local);

        await CreateService().SyncAsync(_account, _provider);

        _provider.Created.Should().BeEmpty();
        local.SyncState.Should().Be(CalendarSyncState.PendingCreate);
    }

    [Fact]
    public async Task SyncAsync_ServidorPaginaOLote_RepeteAteOFim()
    {
        _ = GivenLocalCalendar();
        const string primeiro = "https://dav.exemplo.com/cal/1.ics";
        const string segundo = "https://dav.exemplo.com/cal/2.ics";

        GivenDocument(primeiro, "uid-1", Inicio);
        GivenDocument(segundo, "uid-2", Inicio.AddDays(1));

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Listed(primeiro, "\"1\"")],
            "token-a", "1", HasMore: true, IsFullEnumeration: true));

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Listed(segundo, "\"1\"")],
            "token-b", "2", HasMore: false, IsFullEnumeration: false));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.Added.Should().Be(2);
        _events.Events.Should().HaveCount(2);
    }

    /// <summary>
    /// A regra do <c>SEQUENCE</c> (D-024) vale também para o que vem do CalDAV: o documento
    /// carrega a versão, e a menor nunca sobrescreve a maior.
    /// </summary>
    [Fact]
    public async Task SyncAsync_DocumentoComSequenceMenor_NaoSobrescreve()
    {
        var calendar = GivenLocalCalendar();
        const string href = "https://dav.exemplo.com/cal/abcd.ics";

        var local = CalendarEvent.Create(_account.Id, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        local.ApplyUpdate(
            5, "Reunião", null, null, null, Inicio, Inicio.AddHours(1), false, null,
            CalendarEventStatus.Confirmed, null, Now);
        local.BindToRemoteCalendar(calendar.Id, Now);
        local.MarkRemoteSynced(href, "\"1\"", null, Now);
        await _events.AddAsync(local);

        GivenDocument(href, "uid-1", Inicio.AddHours(4), sequence: 3);

        _provider.EnqueueChanges(new RemoteCalendarChanges(
            [RemoteCalendarChange.Listed(href, "\"2\"")],
            "token-6", "6", HasMore: false, IsFullEnumeration: false));

        var resultado = await CreateService().SyncAsync(_account, _provider);

        resultado.Updated.Should().Be(0);
        local.StartsAt.Should().Be(Inicio);
        local.Sequence.Should().Be(5);
    }

    /// <summary>
    /// Uma coleção quebrada não pode esconder a atualização de todas as outras.
    /// </summary>
    [Fact]
    public async Task SyncAsync_FalhaEmUmaColecao_NaoDerrubaAsOutras()
    {
        _provider.Collections.Clear();
        _provider.Collections.Add(new RemoteCalendarDescriptor(
            Colecao, "Agenda", null, false, null, null));

        var quebrado = new FalhaNaPrimeiraColecaoProvider(_provider);

        var resultado = await CreateService().SyncAsync(_account, quebrado);

        resultado.CalendarsMirrored.Should().Be(1);

        _calendars.Calendars.Should().ContainSingle()
            .Which.LastSyncError.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncAsync_ContaSemServidorDeAgenda_NaoFazNada()
    {
        _account.ConfigureCalendar(CalendarProviderKind.None, null, syncEnabled: false, Now);

        var resultado = await CreateService().SyncAsync(_account);

        resultado.CalendarsMirrored.Should().Be(0);
        _calendars.Calendars.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_ProtocoloSemImplementacaoRegistrada_NaoFazNada()
    {
        _account.ConfigureCalendar(
            CalendarProviderKind.MicrosoftGraph, "https://graph.microsoft.com/v1.0/me",
            syncEnabled: true, Now);

        var resultado = await CreateService().SyncAsync(_account);

        resultado.CalendarsMirrored.Should().Be(0);
        _calendars.Calendars.Should().BeEmpty();
    }

    /// <summary>Provedor que descobre normalmente e falha ao ler as alterações.</summary>
    private sealed class FalhaNaPrimeiraColecaoProvider : ICalendarSyncProvider
    {
        private readonly ScriptedCalendarProvider _inner;

        public FalhaNaPrimeiraColecaoProvider(ScriptedCalendarProvider inner) => _inner = inner;

        public CalendarProviderKind Provider => _inner.Provider;

        public Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
            Account account, CancellationToken cancellationToken = default)
            => _inner.DiscoverAsync(account, cancellationToken);

        public Task<Abstractions.Mail.ConnectionTestResult> TestAsync(
            Account account, CancellationToken cancellationToken = default)
            => _inner.TestAsync(account, cancellationToken);

        public Task<RemoteCalendarChanges> FetchChangesAsync(
            Account account, RemoteCalendar calendar, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("A coleção respondeu 500.");

        public Task<IReadOnlyList<RemoteCalendarChange>> FetchResourcesAsync(
            Account account, RemoteCalendar calendar, IReadOnlyCollection<string> hrefs,
            CancellationToken cancellationToken = default)
            => _inner.FetchResourcesAsync(account, calendar, hrefs, cancellationToken);

        public Task<RemoteWriteResult> CreateAsync(
            Account account, RemoteCalendar calendar, CalendarEventData calendarEvent,
            CancellationToken cancellationToken = default)
            => _inner.CreateAsync(account, calendar, calendarEvent, cancellationToken);

        public Task<RemoteWriteResult> UpdateAsync(
            Account account, RemoteCalendar calendar, string href, string? knownETag,
            CalendarEventData calendarEvent, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(account, calendar, href, knownETag, calendarEvent, cancellationToken);

        public Task<RemoteWriteResult> DeleteAsync(
            Account account, RemoteCalendar calendar, string href, string? knownETag,
            CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(account, calendar, href, knownETag, cancellationToken);
    }
}
