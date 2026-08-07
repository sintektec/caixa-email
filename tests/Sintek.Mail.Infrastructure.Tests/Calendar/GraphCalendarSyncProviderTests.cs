using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Calendar.Rest;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>
/// Cobre a agenda do Microsoft 365. O que se verifica aqui não é só a leitura: é que a
/// consulta preserva o mestre da série, que a precedência usa o instante de alteração — o
/// Graph não expõe <c>SEQUENCE</c> — e que a exclusão feita no servidor chega pela passada
/// completa, que é o único caminho que a reporta.
/// </summary>
public class GraphCalendarSyncProviderTests
{
    private const string Colecao = "AAMkAGI2-calendario";

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ScriptedRestHandler _handler = new();
    private readonly ScriptedOAuthProvider _oauth = new() { Kind = OAuthProviderKind.Microsoft };
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("joao@contoso.com"), "João", Now);

    public GraphCalendarSyncProviderTests()
    {
        _account.UseOAuthAuthentication(OAuthProviderKind.Microsoft, Now);
        _account.ConfigureCalendar(
            CalendarProviderKind.MicrosoftGraph, "https://graph.microsoft.com/v1.0/", true, Now);
    }

    private GraphCalendarSyncProvider CreateProvider()
    {
        var client = new CalendarRestClient(
            new HttpClient(_handler),
            new ScriptedOAuthRegistry(_oauth),
            NullLogger<CalendarRestClient>.Instance);

        return new GraphCalendarSyncProvider(
            client, _clock, NullLogger<GraphCalendarSyncProvider>.Instance);
    }

    private static RemoteCalendar CalendarioLocal(Guid accountId, string? syncToken = null)
    {
        var calendar = RemoteCalendar.Create(
            accountId, CalendarProviderKind.MicrosoftGraph, Colecao, "Agenda", Now);

        if (syncToken is not null)
        {
            calendar.MarkSynced(syncToken, null, Now);
        }

        return calendar;
    }

    // ---- Descoberta -------------------------------------------------------------------

    [Fact]
    public async Task DiscoverAsync_CalendariosDaConta_SaoEspelhados()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "value": [
                { "id": "cal-1", "name": "Calendário", "hexColor": "#0078D4", "canEdit": true },
                { "id": "cal-2", "name": "Equipe", "canEdit": false }
              ]
            }
            """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().HaveCount(2);
        colecoes[0].CollectionUrl.Should().Be("cal-1");
        colecoes[0].Color.Should().Be("#0078D4");
        colecoes[0].IsReadOnly.Should().BeFalse();
        colecoes[1].IsReadOnly.Should().BeTrue();
    }

    /// <summary>
    /// O token do Entra é emitido por recurso: o de IMAP não abre o Graph. A agenda tem de
    /// pedir o escopo dela, e é isso que se verifica.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_PedeOEscopoDoGraph_NaoODeImap()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        await CreateProvider().DiscoverAsync(_account);

        _oauth.RequestedScopes.Should().ContainSingle()
            .Which.Should().Contain("https://graph.microsoft.com/Calendars.ReadWrite");
    }

    /// <summary>
    /// Sem consentimento de agenda a conta de e-mail continua funcionando; a agenda espera.
    /// Uma exceção aqui derrubaria o ciclo inteiro.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_SemConsentimentoDeAgenda_DevolveVazioSemLancar()
    {
        _oauth.HasConsent = false;

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().BeEmpty();
        _handler.Requests.Should().BeEmpty();
    }

    // ---- Leitura ------------------------------------------------------------------------

    /// <summary>
    /// A consulta é <c>events</c> com <c>$filter</c>, e não <c>calendarView/delta</c>: o
    /// delta expande a recorrência em ocorrências e exige janela de datas, o que destruiria
    /// o mestre com <c>RRULE</c> que este produto guarda.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_PrimeiraPassada_ListaTudoSemJanelaDeDatas()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        changes.IsFullEnumeration.Should().BeTrue();

        var url = _handler.Requests.Should().ContainSingle().Subject.Uri.ToString();
        url.Should().NotContain("calendarView");
        url.Should().NotContain("startDateTime");
        url.Should().NotContain("$filter");
    }

    [Fact]
    public async Task FetchChangesAsync_EventoRecorrente_PreservaARegraDeRepeticao()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "value": [{
                "id": "evt-1",
                "@odata.etag": "W/\"abc\"",
                "iCalUId": "uid-externo@contoso.com",
                "subject": "Semanal",
                "isAllDay": false,
                "isCancelled": false,
                "lastModifiedDateTime": "2026-08-05T10:00:00Z",
                "start": { "dateTime": "2026-08-10T17:00:00.0000000", "timeZone": "UTC" },
                "end": { "dateTime": "2026-08-10T18:00:00.0000000", "timeZone": "UTC" },
                "recurrence": {
                  "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["monday", "wednesday"] },
                  "range": { "type": "numbered", "numberOfOccurrences": 10 }
                }
              }]
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        var evento = changes.Changes.Should().ContainSingle().Subject;
        evento.Href.Should().Be("evt-1");

        // O UID é a identidade de calendário e atravessa sistemas; o id é a de rede. É o que
        // faz o convite recebido por e-mail e o mesmo evento vindo do Graph se reconhecerem.
        evento.Event!.Uid.Should().Be("uid-externo@contoso.com");
        evento.Event.RecurrenceRule.Should().Be("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10");
    }

    /// <summary>
    /// O Graph não expõe <c>SEQUENCE</c> — a precedência é pelo instante de alteração
    /// (D-029), e é ele que precisa chegar ao motor.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_Evento_DeclaraAVersaoPeloInstanteDeAlteracao()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "value": [{
                "id": "evt-1",
                "subject": "Reunião",
                "lastModifiedDateTime": "2026-08-05T10:30:00Z",
                "start": { "dateTime": "2026-08-10T17:00:00.0000000", "timeZone": "UTC" },
                "end": { "dateTime": "2026-08-10T18:00:00.0000000", "timeZone": "UTC" }
              }]
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        var versao = changes.Changes[0].Version;
        versao.Sequence.Should().BeNull();
        versao.LastModifiedAt.Should().Be(new DateTimeOffset(2026, 8, 5, 10, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// O <c>dateTime</c> do Graph vem sem deslocamento, e o fuso vai em campo separado.
    /// Interpretá-lo como hora local da máquina daria o instante errado.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_HorarioSemDeslocamento_ELidoComoUtc()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """
            {
              "value": [{
                "id": "evt-1",
                "subject": "Reunião",
                "start": { "dateTime": "2026-08-10T17:00:00.0000000", "timeZone": "UTC" },
                "end": { "dateTime": "2026-08-10T18:00:00.0000000", "timeZone": "UTC" }
              }]
            }
            """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        changes.Changes[0].Event!.StartsAt
            .Should().Be(new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Depois da passada completa, a seguinte filtra pela marca-d'água — é o que torna a
    /// sincronização incremental sem perder o mestre da série.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_ComMarcaDaguaRecente_FiltraPorAlteracao()
    {
        var token = new GraphSyncToken(
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            Now.AddHours(-1)).ToString();

        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, token));

        changes.IsFullEnumeration.Should().BeFalse();
        _handler.Requests[0].Uri.ToString()
            .Should().Contain("lastModifiedDateTime ge 2026-08-05T10:00:00Z");
    }

    /// <summary>
    /// A consulta por <c>$filter</c> não reporta exclusão — o recurso simplesmente some.
    /// Sem a passada completa periódica, o compromisso apagado no servidor ficaria aqui para
    /// sempre. É o que <c>IsFullEnumeration</c> autoriza (D-028).
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_MarcaDaguaAntiga_ForcaPassadaCompleta()
    {
        var token = new GraphSyncToken(
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
            Now.AddDays(-2)).ToString();

        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, token));

        changes.IsFullEnumeration.Should().BeTrue();
        _handler.Requests[0].Uri.ToString().Should().NotContain("$filter");
    }

    /// <summary>
    /// Token ilegível força passada completa. É o lado certo do erro: uma passada a mais
    /// custa tráfego; uma incremental sobre marca inventada perderia alterações em silêncio.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_TokenCorrompido_ForcaPassadaCompleta()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id, "isto-não-é-json"));

        changes.IsFullEnumeration.Should().BeTrue();
    }

    [Fact]
    public async Task FetchChangesAsync_VariasPaginas_SegueONextLink()
    {
        _handler
            .Reply(new RestReply(HttpStatusCode.OK, """
                {
                  "value": [{
                    "id": "evt-1", "subject": "Um",
                    "start": { "dateTime": "2026-08-10T17:00:00.0000000", "timeZone": "UTC" },
                    "end": { "dateTime": "2026-08-10T18:00:00.0000000", "timeZone": "UTC" }
                  }],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/calendars/x/events?$skip=1"
                }
                """))
            .Reply(new RestReply(HttpStatusCode.OK, """
                {
                  "value": [{
                    "id": "evt-2", "subject": "Dois",
                    "start": { "dateTime": "2026-08-11T17:00:00.0000000", "timeZone": "UTC" },
                    "end": { "dateTime": "2026-08-11T18:00:00.0000000", "timeZone": "UTC" }
                  }]
                }
                """));

        var changes = await CreateProvider()
            .FetchChangesAsync(_account, CalendarioLocal(_account.Id));

        changes.Changes.Should().HaveCount(2);
        _handler.Requests.Should().HaveCount(2);
    }

    // ---- Escrita ------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_CompromissoNovo_GravaEDevolveOIdentificador()
    {
        _handler.Reply(new RestReply(HttpStatusCode.Created, """
            {
              "id": "evt-novo",
              "@odata.etag": "W/\"1\"",
              "lastModifiedDateTime": "2026-08-05T12:00:00Z"
            }
            """));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), new Application.Abstractions.Calendar.CalendarEventData
            {
                Uid = "uid-1",
                Summary = "Reunião",
                StartsAt = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
            });

        resultado.Succeeded.Should().BeTrue();
        resultado.Href.Should().Be("evt-novo");
        resultado.Version.LastModifiedAt.Should().NotBeNull();

        var pedido = _handler.Requests[0];
        pedido.Method.Should().Be("POST");

        // O JsonNode escapa não-ASCII por padrão: "Reunião" sai como "Reunião". É JSON
        // válido e o Graph aceita — o que não pode acontecer é o acento se perder.
        pedido.Body.Should().Contain("\"subject\":\"Reuni\\u00E3o\"");
    }

    /// <summary>
    /// As três situações da recorrência no corpo enviado, e a diferença entre a segunda e a
    /// terceira é o que impede de apagar no servidor uma série que ninguém pediu para apagar.
    /// </summary>
    /// <remarks>
    /// Num <c>PATCH</c>, campo ausente significa "não mexa". Por isso a remoção precisa do
    /// nulo explícito — sem ele, o usuário apaga a repetição, salva, e ela volta na
    /// sincronização seguinte. E por isso mesmo a regra <i>intraduzível</i> exige o oposto:
    /// mandar nulo ali apagaria do servidor a série que não soubemos ler.
    /// </remarks>
    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", true, false)]
    [InlineData(null, false, true)]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", false, false)]
    public async Task UpdateAsync_Recorrencia_EnviaSerieNuloOuNada(
        string? rrule, bool esperaSerie, bool esperaNulo)
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{ "id": "evt-1" }"""));

        await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), "evt-1", null,
            new Application.Abstractions.Calendar.CalendarEventData
            {
                Uid = "uid-1",
                Summary = "Reunião",
                RecurrenceRule = rrule,
                StartsAt = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
            });

        var corpo = _handler.Requests[0].Body!;

        corpo.Contains("\"pattern\"", StringComparison.Ordinal).Should().Be(esperaSerie);
        corpo.Contains("\"recurrence\":null", StringComparison.Ordinal).Should().Be(esperaNulo);

        if (!esperaSerie && !esperaNulo)
        {
            corpo.Should().NotContain("\"recurrence\"",
                "regra sem tradução fiel deixa o campo de fora — mandar nulo apagaria a série do servidor");
        }
    }

    [Fact]
    public async Task UpdateAsync_PrecondicaoRecusada_EConflitoNaoFalha()
    {
        _handler.Reply(new RestReply(HttpStatusCode.PreconditionFailed));

        var resultado = await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), "evt-1", "W/\"antigo\"",
            new Application.Abstractions.Calendar.CalendarEventData
            {
                Uid = "uid-1",
                Summary = "Reunião",
                StartsAt = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero),
                EndsAt = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
            });

        resultado.IsConflict.Should().BeTrue();
        _handler.Requests[0].Method.Should().Be("PATCH");
        _handler.Requests[0].Headers["If-Match"].Should().Be("W/\"antigo\"");
    }

    /// <summary>
    /// Já não está lá é o estado desejado. Tratar como falha faria a exclusão local ficar
    /// pendente para sempre.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_CompromissoJaAusente_ContaComoSucesso()
    {
        _handler.Reply(new RestReply(HttpStatusCode.NotFound));

        var resultado = await CreateProvider()
            .DeleteAsync(_account, CalendarioLocal(_account.Id), "evt-1", "W/\"1\"");

        resultado.Succeeded.Should().BeTrue();
    }

    // ---- Teste de conexão ---------------------------------------------------------------

    /// <summary>
    /// O Graph recusa qualquer coisa que não seja OAuth 2.0. Dizer isso é mais útil do que
    /// deixar a autenticação falhar depois.
    /// </summary>
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
    public async Task TestAsync_SemConsentimentoDeAgenda_ReportaFalhaDeAutenticacao()
    {
        _oauth.HasConsent = false;

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.IsAuthenticationFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TestAsync_ServidorResponde_ReportaSucesso()
    {
        _handler.Reply(new RestReply(HttpStatusCode.OK, """{"value":[]}"""));

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.Succeeded.Should().BeTrue();
    }
}
