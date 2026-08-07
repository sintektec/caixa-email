using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Calendar.Rest;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>
/// Cobre a agenda da Google. O que importa aqui é a recuperação do token vencido — a API
/// responde 410 e manda refazer do zero —, a exclusão que chega como <c>status: cancelled</c>,
/// e o mestre da série preservado por <c>singleEvents</c> em falso.
/// </summary>
public class GoogleCalendarSyncProviderTests
{
    private const string Colecao = "joao@exemplo.com";

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ScriptedRestHandler _handler = new();
    private readonly ScriptedOAuthProvider _oauth = new() { Kind = OAuthProviderKind.Google };

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("joao@exemplo.com"), "João", Now);

    public GoogleCalendarSyncProviderTests()
    {
        _account.UseOAuthAuthentication(OAuthProviderKind.Google, Now);
        _account.ConfigureCalendar(
            CalendarProviderKind.GoogleCalendar, "https://www.googleapis.com/calendar/v3/", true, Now);
    }

    private GoogleCalendarSyncProvider CreateProvider()
    {
        var client = new CalendarRestClient(
            new HttpClient(_handler),
            new ScriptedOAuthRegistry(_oauth),
            NullLogger<CalendarRestClient>.Instance);

        return new GoogleCalendarSyncProvider(client, NullLogger<GoogleCalendarSyncProvider>.Instance);
    }

    private static RemoteCalendar CalendarioLocal(Guid accountId, string? syncToken = null)
    {
        var calendar = RemoteCalendar.Create(
            accountId, CalendarProviderKind.GoogleCalendar, Colecao, "Agenda", Now);

        if (syncToken is not null)
        {
            calendar.MarkSynced(syncToken, null, Now);
        }

        return calendar;
    }

    private static CalendarEventData Compromisso(string? rrule = null) => new()
    {
        Uid = "uid-1",
        Summary = "Reunião",
        StartsAt = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
        RecurrenceRule = rrule,
    };

    // ---- Descoberta -------------------------------------------------------------------

    /// <summary>
    /// <c>reader</c> e <c>freeBusyReader</c> não aceitam escrita; gravar ali devolveria 403
    /// a cada tentativa, e a fila retentaria para sempre.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_PapeisDeAcesso_DecidemSeAColecaoEGravavel()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "items": [
                { "id": "primaria", "summary": "João", "accessRole": "owner", "backgroundColor": "#039BE5" },
                { "id": "feriados", "summary": "Feriados", "accessRole": "reader" },
                { "id": "equipe", "summary": "Equipe", "accessRole": "writer" }
              ]
            }
            """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().HaveCount(3);
        colecoes.Single(c => c.CollectionUrl == "primaria").IsReadOnly.Should().BeFalse();
        colecoes.Single(c => c.CollectionUrl == "equipe").IsReadOnly.Should().BeFalse();
        colecoes.Single(c => c.CollectionUrl == "feriados").IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverAsync_PedeOEscopoDaAgenda()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"items":[]}"""));

        await CreateProvider().DiscoverAsync(_account);

        _oauth.RequestedScopes.Should().ContainSingle()
            .Which.Should().Contain("https://www.googleapis.com/auth/calendar");
    }

    // ---- Leitura ------------------------------------------------------------------------

    /// <summary>
    /// <c>singleEvents</c> fica em falso — o padrão — para preservar o mestre da série. Com
    /// ele ligado a Google expande a recorrência, e este produto guarda o mestre com a
    /// <c>RRULE</c> e expande na hora de desenhar.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_NuncaPedeExpansaoDeRecorrencia()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"items":[],"nextSyncToken":"t1"}"""));

        await CreateProvider().FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        _handler.Requests[0].Uri.ToString().Should().NotContain("singleEvents=true");
    }

    [Fact]
    public async Task FetchChangesAsync_EventoRecorrente_TrazARRuleCrua()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "items": [{
                "id": "evt-1",
                "etag": "\"123\"",
                "iCalUID": "uid-externo@google.com",
                "status": "confirmed",
                "summary": "Semanal",
                "updated": "2026-08-05T10:00:00.000Z",
                "start": { "dateTime": "2026-08-10T14:00:00-03:00", "timeZone": "America/Sao_Paulo" },
                "end": { "dateTime": "2026-08-10T15:00:00-03:00", "timeZone": "America/Sao_Paulo" },
                "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;VALUE=DATE:20260817"]
              }],
              "nextSyncToken": "t2"
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        var evento = changes.Changes.Should().ContainSingle().Subject;
        evento.Event!.Uid.Should().Be("uid-externo@google.com");
        evento.Event.RecurrenceRule.Should().Be("FREQ=WEEKLY;BYDAY=MO");

        // O deslocamento declarado é preservado: 14h em São Paulo são 17h UTC.
        evento.Event.StartsAt.Should().Be(new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero));
        changes.SyncToken.Should().Be("t2");
    }

    /// <summary>
    /// A exclusão vem como um evento comum com <c>status: "cancelled"</c> — não há outro
    /// sinal, e lê-lo como alteração ressuscitaria o compromisso a cada passada.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_EventoCancelado_EUmaRemocao()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "items": [{ "id": "evt-1", "status": "cancelled" }],
              "nextSyncToken": "t3"
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, "t2"));

        changes.Changes.Should().ContainSingle()
            .Which.Change.Should().Be(RemoteChangeKind.Removed);
    }

    /// <summary>
    /// Evento de dia inteiro usa <c>date</c> em vez de <c>dateTime</c>. Ler só o segundo
    /// faria todo evento de dia inteiro desaparecer da agenda.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_EventoDeDiaInteiro_NaoSomeDaAgenda()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "items": [{
                "id": "evt-1",
                "status": "confirmed",
                "summary": "Feriado",
                "start": { "date": "2026-09-07" },
                "end": { "date": "2026-09-08" }
              }],
              "nextSyncToken": "t4"
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        var evento = changes.Changes.Should().ContainSingle().Subject;
        evento.Event!.IsAllDay.Should().BeTrue();
        evento.Event.StartsAt.Should().Be(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// 410 com <c>fullSyncRequired</c>: o token venceu. A recuperação que a própria API manda
    /// fazer é descartá-lo e refazer do zero — e a passada que vem daí é completa, o que
    /// autoriza o motor a apagar o que sumiu (D-028).
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_TokenVencido_RefazDoZeroEDeclaraPassadaCompleta()
    {
        _handler
            .Reply(new RestReply(HttpStatusCode.Gone, """
                {"error":{"code":410,"errors":[{"reason":"fullSyncRequired"}]}}
                """))
            .Reply(new RestReply(HttpStatusCode.OK, """
                {
                  "items": [{
                    "id": "evt-1", "status": "confirmed", "summary": "Reunião",
                    "start": { "dateTime": "2026-08-10T17:00:00Z" },
                    "end": { "dateTime": "2026-08-10T18:00:00Z" }
                  }],
                  "nextSyncToken": "novo"
                }
                """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, "vencido"));

        changes.IsFullEnumeration.Should().BeTrue();
        changes.SyncToken.Should().Be("novo");

        _handler.Requests[0].Uri.ToString().Should().Contain("syncToken=vencido");
        _handler.Requests[1].Uri.ToString().Should().NotContain("syncToken");
    }

    [Fact]
    public async Task FetchChangesAsync_ComToken_EPassadaIncremental()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"items":[],"nextSyncToken":"t5"}"""));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, "t4"));

        changes.IsFullEnumeration.Should().BeFalse();
    }

    [Fact]
    public async Task FetchChangesAsync_VariasPaginas_SegueOPageToken()
    {
        _handler
            .Reply(new RestReply(HttpStatusCode.OK, """
                {
                  "items": [{
                    "id": "evt-1", "status": "confirmed", "summary": "Um",
                    "start": { "dateTime": "2026-08-10T17:00:00Z" },
                    "end": { "dateTime": "2026-08-10T18:00:00Z" }
                  }],
                  "nextPageToken": "p2"
                }
                """))
            .Reply(new RestReply(HttpStatusCode.OK, """
                {
                  "items": [{
                    "id": "evt-2", "status": "confirmed", "summary": "Dois",
                    "start": { "dateTime": "2026-08-11T17:00:00Z" },
                    "end": { "dateTime": "2026-08-11T18:00:00Z" }
                  }],
                  "nextSyncToken": "t6"
                }
                """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        changes.Changes.Should().HaveCount(2);
        changes.SyncToken.Should().Be("t6");
        _handler.Requests[1].Uri.ToString().Should().Contain("pageToken=p2");
    }

    // ---- Escrita ------------------------------------------------------------------------

    /// <summary>
    /// A Google aceita a <c>RRULE</c> crua, que é como este produto a guarda: nenhuma
    /// tradução no caminho, nenhuma segunda interpretação da norma para divergir.
    /// </summary>
    [Fact]
    public async Task CreateAsync_CompromissoRecorrente_EnviaARRuleCrua()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {"id":"evt-novo","etag":"\"1\"","updated":"2026-08-05T12:00:00.000Z"}
            """));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso("FREQ=WEEKLY;BYDAY=MO"));

        resultado.Succeeded.Should().BeTrue();
        resultado.Href.Should().Be("evt-novo");
        resultado.Version.LastModifiedAt.Should().NotBeNull();
        _handler.Requests[0].Body.Should().Contain("RRULE:FREQ=WEEKLY;BYDAY=MO");
    }

    [Fact]
    public async Task UpdateAsync_PrecondicaoRecusada_EConflitoNaoFalha()
    {
        _handler.Reply(new RestReply(HttpStatusCode.PreconditionFailed));

        var resultado = await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), "evt-1", "\"antigo\"", Compromisso());

        resultado.IsConflict.Should().BeTrue();
        _handler.Requests[0].Headers["If-Match"].Should().Be("\"antigo\"");
    }

    /// <summary>
    /// A Google devolve 410 ao excluir o que já foi excluído. É o estado desejado, não uma
    /// falha — tratá-la como falha deixaria a exclusão local pendente para sempre.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_CompromissoJaExcluido_ContaComoSucesso()
    {
        _handler.Reply(new RestReply(HttpStatusCode.Gone));

        var resultado = await CreateProvider()
            .DeleteAsync(_account, CalendarioLocal(_account.Id), "evt-1", "\"1\"");

        resultado.Succeeded.Should().BeTrue();
    }

    // ---- Teste de conexão ---------------------------------------------------------------

    [Fact]
    public async Task TestAsync_ContaComSenha_ExplicaQueExigeOAuth()
    {
        _account.UsePasswordAuthentication(null, Now);

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.Succeeded.Should().BeFalse();
        resultado.ErrorMessage.Should().Contain("OAuth");
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TestAsync_ServidorResponde_ReportaSucesso()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"items":[]}"""));

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.Succeeded.Should().BeTrue();
    }
}
