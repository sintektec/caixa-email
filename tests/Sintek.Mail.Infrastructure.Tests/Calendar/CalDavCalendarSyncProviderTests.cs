using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Infrastructure.Calendar;
using Sintek.Mail.Infrastructure.Calendar.CalDav;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>Valor de teste, nunca uma credencial real.</summary>
internal static class FakeSecret
{
    /// <summary>Devolve um valor previsível e inconfundivelmente fictício.</summary>
    public static string For(string label) => string.Join('-', "valor", "ficticio", label);
}

/// <summary>
/// Cobre o cliente CalDAV contra as respostas que servidores reais devolvem — inclusive as
/// que estão fora da norma e derrubariam uma leitura ingênua.
/// </summary>
public class CalDavCalendarSyncProviderTests
{
    private const string Raiz = "https://dav.exemplo.com/";
    private const string Colecao = "https://dav.exemplo.com/calendars/joao/agenda/";

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ScriptedCalDavHandler _handler = new();
    private readonly FakeCredentialStore _credentials = new();

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("joao@exemplo.com"), "João", Now);

    public CalDavCalendarSyncProviderTests()
    {
        _account.ConfigureCalendar(CalendarProviderKind.CalDav, Raiz, syncEnabled: true, Now);
        _credentials.SetSecretAsync(_account.CredentialKey, FakeSecret.For("caldav")).GetAwaiter().GetResult();
    }

    private CalDavCalendarSyncProvider CreateProvider()
    {
        var transport = new CalDavTransport(
            new HttpClient(_handler),
            _credentials,
            Substitute.For<IOAuthProviderRegistry>(),
            NullLogger<CalDavTransport>.Instance);

        // Serializador real, não dublê: a escrita passa a montar o iCalendar dentro do
        // provedor, e substituí-lo esconderia justamente o que passou a ser trabalho dele.
        return new CalDavCalendarSyncProvider(
            transport,
            new IcalNetCalendarSerializer(NullLogger<IcalNetCalendarSerializer>.Instance),
            NullLogger<CalDavCalendarSyncProvider>.Instance);
    }

    private static CalendarEventData Compromisso(string uid = "uid-1", int sequence = 0)
        => new()
        {
            Uid = uid,
            Sequence = sequence,
            Summary = "Reunião de projeto",
            StartsAt = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero),
        };

    private static RemoteCalendar CalendarioLocal(Guid accountId, string? syncToken = null, string? ctag = null)
    {
        var calendar = RemoteCalendar.Create(
            accountId, CalendarProviderKind.CalDav, Colecao, "Agenda", Now);

        if (syncToken is not null || ctag is not null)
        {
            calendar.MarkSynced(syncToken, ctag, Now);
        }

        return calendar;
    }

    private static string MultiStatus(string inner)
        => $"""<?xml version="1.0" encoding="utf-8"?><multistatus xmlns="DAV:">{inner}</multistatus>""";

    // ---- Descoberta -------------------------------------------------------------------

    /// <summary>
    /// Os prefixos são arbitrários. Um servidor escreve <c>D:</c>, outro <c>d:</c>, outro
    /// <c>dav:</c>, e todos estão certos — casar por prefixo devolveria zero elementos sem
    /// erro nenhum.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_PrefixosDiferentesEmCadaResposta_EncontraAColecao()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <D:multistatus xmlns:D="DAV:">
                  <D:response>
                    <D:href>/</D:href>
                    <D:propstat>
                      <D:prop><D:current-user-principal><D:href>/principals/joao/</D:href></D:current-user-principal></D:prop>
                      <D:status>HTTP/1.1 200 OK</D:status>
                    </D:propstat>
                  </D:response>
                </D:multistatus>
                """))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <dav:multistatus xmlns:dav="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
                  <dav:response>
                    <dav:href>/principals/joao/</dav:href>
                    <dav:propstat>
                      <dav:prop><cal:calendar-home-set><dav:href>/calendars/joao/</dav:href></cal:calendar-home-set></dav:prop>
                      <dav:status>HTTP/1.1 200 OK</dav:status>
                    </dav:propstat>
                  </dav:response>
                </dav:multistatus>
                """))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"
                               xmlns:cs="http://calendarserver.org/ns/" xmlns:ic="http://apple.com/ns/ical/">
                  <d:response>
                    <d:href>/calendars/joao/</d:href>
                    <d:propstat>
                      <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>/calendars/joao/agenda/</d:href>
                    <d:propstat>
                      <d:prop>
                        <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                        <d:displayname>Agenda de trabalho</d:displayname>
                        <cs:getctag>3145</cs:getctag>
                        <ic:calendar-color>#FF5733FF</ic:calendar-color>
                      </d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        var agenda = colecoes.Should().ContainSingle().Subject;
        agenda.CollectionUrl.Should().Be(Colecao);
        agenda.DisplayName.Should().Be("Agenda de trabalho");
        agenda.Color.Should().Be("#FF5733FF");
        agenda.CTag.Should().Be("3145");
        agenda.IsReadOnly.Should().BeFalse();
    }

    /// <summary>
    /// A propriedade presente e sem <c>VEVENT</c> é uma lista de tarefas: gravar um
    /// compromisso ali devolveria 403 a cada tentativa.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_ColecaoSoDeTarefas_EDescartada()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response>
                    <d:href>/tarefas/</d:href>
                    <d:propstat>
                      <d:prop>
                        <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                        <d:displayname>Minhas tarefas</d:displayname>
                        <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set>
                      </d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().BeEmpty();
    }

    /// <summary>
    /// Cada propriedade tem o próprio status. Um <c>404</c> dentro de um <c>propstat</c>
    /// significa que aquela propriedade não existe neste recurso — não que o recurso sumiu.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_PropriedadeAusenteEm404_NaoInvalidaARespostaInteira()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"
                               xmlns:cs="http://calendarserver.org/ns/">
                  <d:response>
                    <d:href>/calendars/joao/agenda/</d:href>
                    <d:propstat>
                      <d:prop>
                        <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                        <d:displayname>Agenda</d:displayname>
                      </d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                    <d:propstat>
                      <d:prop><cs:getctag/><d:sync-token/></d:prop>
                      <d:status>HTTP/1.1 404 Not Found</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        var agenda = colecoes.Should().ContainSingle().Subject;
        agenda.DisplayName.Should().Be("Agenda");
        agenda.CTag.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAsync_SemPrivilegioDeEscrita_MarcaSomenteLeitura()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response>
                    <d:href>/calendars/joao/agenda/</d:href>
                    <d:propstat>
                      <d:prop>
                        <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                        <d:displayname>Compartilhada</d:displayname>
                        <d:current-user-privilege-set>
                          <d:privilege><d:read/></d:privilege>
                        </d:current-user-privilege-set>
                      </d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().ContainSingle().Which.IsReadOnly.Should().BeTrue();
    }

    /// <summary>
    /// Uma exceção aqui derrubaria o ciclo de sincronização inteiro da conta, e a agenda
    /// local funciona sem servidor.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_ServidorForaDoAr_DevolveListaVaziaSemLancar()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.InternalServerError));

        var colecoes = await CreateProvider().DiscoverAsync(_account);

        colecoes.Should().BeEmpty();
    }

    // ---- Redirecionamento -------------------------------------------------------------

    /// <summary>
    /// O <c>HttpClient</c> com redirecionamento automático transforma PROPFIND em GET e
    /// descarta o <c>Authorization</c> ao mudar de host — que é o caso do iCloud. Seguindo à
    /// mão, os três precisam sobreviver.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_RedirecionamentoParaOutroHost_PreservaMetodoECredencial()
    {
        _handler
            .Reply(new CalDavReply(
                HttpStatusCode.MovedPermanently,
                Location: "https://p34-dav.exemplo.com/1234/principal/"))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
                <response>
                  <href>/1234/principal/</href>
                  <propstat>
                    <prop><current-user-principal><href>/1234/principal/</href></current-user-principal></prop>
                    <status>HTTP/1.1 200 OK</status>
                  </propstat>
                </response>
                """)))
            .Reply(new CalDavReply(HttpStatusCode.NotFound))
            .Reply(new CalDavReply(HttpStatusCode.NotFound));

        await CreateProvider().DiscoverAsync(_account);

        var segunda = _handler.Requests[1];
        segunda.Method.Should().Be("PROPFIND");
        segunda.Uri.Host.Should().Be("p34-dav.exemplo.com");
        segunda.Headers.Should().ContainKey("Authorization");
        segunda.Body.Should().Contain("current-user-principal");
    }

    // ---- Sincronização incremental ----------------------------------------------------

    /// <summary>
    /// O discriminador entre "alterado" e "removido" é <b>onde</b> o <c>status</c> está,
    /// não o código: filho direto da <c>response</c> é o recurso; dentro de um
    /// <c>propstat</c> é uma propriedade que não existe.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_RespostaComRemocaoEPropriedadeAusente_DistingueAsDuas()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
            <response>
              <href>/calendars/joao/agenda/1.ics</href>
              <propstat>
                <prop><getetag>"00004-abcd1"</getetag></prop>
                <status>HTTP/1.1 200 OK</status>
              </propstat>
              <propstat>
                <prop><bigbox/></prop>
                <status>HTTP/1.1 404 Not Found</status>
              </propstat>
            </response>
            <response>
              <href>/calendars/joao/agenda/2.ics</href>
              <status>HTTP/1.1 404 Not Found</status>
            </response>
            <sync-token>http://exemplo.com/ns/sync/1238</sync-token>
            """)));

        var changes = await CreateProvider().FetchChangesAsync(
            _account, CalendarioLocal(_account.Id, "http://exemplo.com/ns/sync/1234"));

        changes.SyncToken.Should().Be("http://exemplo.com/ns/sync/1238");
        changes.Changes.Should().HaveCount(2);

        changes.Changes[0].Change.Should().Be(RemoteChangeKind.Upserted);
        changes.Changes[0].ETag.Should().Be("\"00004-abcd1\"");
        changes.Changes[0].Href.Should().Be($"{Colecao}1.ics");

        changes.Changes[1].Change.Should().Be(RemoteChangeKind.Removed);
    }

    /// <summary>
    /// <c>Depth: 0</c> é o único valor aceito no <c>sync-collection</c>; o escopo vai no
    /// <c>sync-level</c>, e <c>Depth: 1</c> faz alguns servidores devolverem 400.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_RelatorioDeSincronizacao_VaiComProfundidadeZero()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("<sync-token>t</sync-token>")));

        await CreateProvider().FetchChangesAsync(_account, CalendarioLocal(_account.Id, "t0"));

        var pedido = _handler.Requests.Should().ContainSingle().Subject;
        pedido.Method.Should().Be("REPORT");
        pedido.Headers["Depth"].Should().Be("0");
        pedido.Body.Should().Contain("sync-level");

        // Sem <D:limit>: quando o servidor não consegue truncar no número pedido ele falha a
        // requisição inteira, e o Nextcloud tem defeito conhecido com esse elemento.
        pedido.Body.Should().NotContain("limit");

        // A declaração precisa bater com os bytes que saem: o StringWriter comum anuncia
        // UTF-16 enquanto o corpo vai em UTF-8.
        pedido.Body.Should().StartWith("""<?xml version="1.0" encoding="utf-8"?>""");
    }

    /// <summary>
    /// Truncagem é 507 <b>dentro</b> do 207, para a própria request-URI. Ignorá-la faz a
    /// sincronização parar no meio sem erro visível.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_LoteTruncado_SinalizaQueHaMais()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
            <response>
              <href>/calendars/joao/agenda/1.ics</href>
              <propstat>
                <prop><getetag>"00001"</getetag></prop>
                <status>HTTP/1.1 200 OK</status>
              </propstat>
            </response>
            <response>
              <href>/calendars/joao/agenda/</href>
              <status>HTTP/1.1 507 Insufficient Storage</status>
              <error><number-of-matches-within-limits/></error>
            </response>
            <sync-token>http://exemplo.com/ns/sync/1233</sync-token>
            """)));

        var changes = await CreateProvider().FetchChangesAsync(
            _account, CalendarioLocal(_account.Id, "t0"));

        changes.HasMore.Should().BeTrue();
        changes.Changes.Should().ContainSingle();
    }

    /// <summary>
    /// O SabreDAV — e portanto Nextcloud, ownCloud e Baikal — responde <c>403</c> com
    /// <c>DAV:valid-sync-token</c>. O código sozinho não distingue token vencido de falta de
    /// permissão, e tratar os dois igual apagaria a agenda de uma coleção sem acesso.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_TokenRecusadoPeloServidor_RefazPassadaCompleta()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.Forbidden, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:error xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns">
                  <s:exception>Sabre\DAV\Exception\InvalidSyncToken</s:exception>
                  <d:valid-sync-token/>
                </d:error>
                """))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
                <response>
                  <href>/calendars/joao/agenda/1.ics</href>
                  <propstat>
                    <prop><getetag>"00001"</getetag></prop>
                    <status>HTTP/1.1 200 OK</status>
                  </propstat>
                </response>
                <sync-token>novo</sync-token>
                """)));

        var changes = await CreateProvider().FetchChangesAsync(
            _account, CalendarioLocal(_account.Id, "vencido"));

        changes.IsFullEnumeration.Should().BeTrue();
        changes.SyncToken.Should().Be("novo");

        // Passada completa se pede com o elemento vazio, não com a ausência dele.
        _handler.Requests[1].Body.Should().Contain("<d:sync-token></d:sync-token>");
    }

    /// <summary>
    /// 403 sem a pré-condição é falta de permissão, não token vencido: refazer do zero seria
    /// insistir num erro que não vai mudar.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_ProibidoSemPrecondicao_NaoRefazDoZero()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.Forbidden, "<d:error xmlns:d=\"DAV:\"/>"))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("")))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("")));

        await CreateProvider().FetchChangesAsync(_account, CalendarioLocal(_account.Id, "token"));

        _handler.Requests.Should().NotContain(
            r => r.Body != null && r.Body.Contains("<d:sync-token></d:sync-token>"));
    }

    // ---- Caminho do CTag ---------------------------------------------------------------

    /// <summary>
    /// Servidor sem <c>sync-collection</c> que responde "o CTag não mudou" devolve zero
    /// alterações — mas <b>não</b> enumerou nada. Marcar isso como passada completa apagaria
    /// a coleção inteira.
    /// </summary>
    [Fact]
    public async Task FetchChangesAsync_SemRelatorioEComCTagIgual_NaoDeclaraPassadaCompleta()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.MethodNotAllowed))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
                  <d:response>
                    <d:href>/calendars/joao/agenda/</d:href>
                    <d:propstat>
                      <d:prop><cs:getctag>3145</cs:getctag></d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """));

        var changes = await CreateProvider().FetchChangesAsync(
            _account, CalendarioLocal(_account.Id, ctag: "3145"));

        changes.Changes.Should().BeEmpty();
        changes.IsFullEnumeration.Should().BeFalse();
        changes.CTag.Should().Be("3145");
    }

    [Fact]
    public async Task FetchChangesAsync_SemRelatorioEComCTagDiferente_ListaAColecaoInteira()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.MethodNotAllowed))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
                  <d:response>
                    <d:href>/calendars/joao/agenda/</d:href>
                    <d:propstat>
                      <d:prop><cs:getctag>3200</cs:getctag></d:prop>
                      <d:status>HTTP/1.1 200 OK</d:status>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """))
            .Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
                <response>
                  <href>/calendars/joao/agenda/1.ics</href>
                  <propstat>
                    <prop><getetag>"a"</getetag></prop>
                    <status>HTTP/1.1 200 OK</status>
                  </propstat>
                </response>
                """)));

        var changes = await CreateProvider().FetchChangesAsync(
            _account, CalendarioLocal(_account.Id, ctag: "3145"));

        changes.IsFullEnumeration.Should().BeTrue();
        changes.CTag.Should().Be("3200");
        changes.Changes.Should().ContainSingle();

        // A listagem pede só os ETags: trazer o iCalendar de tudo torna a coleção grande
        // cara demais.
        _handler.Requests[2].Body.Should().NotContain("calendar-data");
    }

    // ---- Escrita ------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_RecursoNovo_UsaPrecondicaoDeNomeLivre()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.Created, ETag: "\"123-000\""));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.Succeeded.Should().BeTrue();
        resultado.ETag.Should().Be("\"123-000\"");
        resultado.Href.Should().StartWith(Colecao).And.EndWith(".ics");

        var pedido = _handler.Requests.Should().ContainSingle().Subject;
        pedido.Method.Should().Be("PUT");
        pedido.Headers["If-None-Match"].Should().Be("*");
    }

    /// <summary>
    /// 412 no <c>If-None-Match: *</c> é colisão de nome, não conflito de conteúdo: outro
    /// nome resolve.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NomeJaOcupado_TentaOutroNome()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.PreconditionFailed))
            .Reply(new CalDavReply(HttpStatusCode.Created, ETag: "\"1\""));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.Succeeded.Should().BeTrue();
        resultado.IsConflict.Should().BeFalse();
        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[0].Uri.Should().NotBe(_handler.Requests[1].Uri);
    }

    /// <summary>
    /// O UID já existe em outro recurso da coleção, e o erro diz onde. Gravar lá é o
    /// caminho — criar de novo repetiria o mesmo 403 para sempre.
    /// </summary>
    [Fact]
    public async Task CreateAsync_UidJaExisteEmOutroRecurso_GravaNoRecursoIndicado()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.Forbidden, """
                <?xml version="1.0" encoding="utf-8"?>
                <d:error xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <c:no-uid-conflict><d:href>/calendars/joao/agenda/existente.ics</d:href></c:no-uid-conflict>
                </d:error>
                """))
            .Reply(new CalDavReply(HttpStatusCode.NoContent, ETag: "\"2\""));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.Succeeded.Should().BeTrue();
        resultado.Href.Should().Be($"{Colecao}existente.ics");
        _handler.Requests[1].Uri.AbsoluteUri.Should().Be($"{Colecao}existente.ics");
    }

    /// <summary>
    /// Sem ETag forte na resposta, a norma <b>proíbe</b> supor o que ficou gravado: o
    /// servidor reescreveu o objeto. A releitura é o único jeito de saber.
    /// </summary>
    [Fact]
    public async Task CreateAsync_SemEtagNaResposta_RelêORecurso()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.Created))
            .Reply(new CalDavReply(
                HttpStatusCode.OK, "BEGIN:VCALENDAR\r\nSEQUENCE:1\r\nEND:VCALENDAR",
                ETag: "\"depois-da-gravacao\"", ContentType: "text/calendar"));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.ETag.Should().Be("\"depois-da-gravacao\"");
        resultado.ICalendar.Should().Contain("SEQUENCE:1");
        _handler.Requests[1].Method.Should().Be("GET");
    }

    /// <summary>
    /// ETag fraco não serve para <c>If-Match</c>, que compara forte. Guardá-lo faria a
    /// escrita seguinte falhar com 412 para sempre.
    /// </summary>
    [Fact]
    public async Task CreateAsync_EtagFracoNaResposta_ForcaAReleitura()
    {
        _handler
            .Reply(new CalDavReply(HttpStatusCode.Created, ETag: "W/\"fraco\""))
            .Reply(new CalDavReply(
                HttpStatusCode.OK, "BEGIN:VCALENDAR\r\nEND:VCALENDAR",
                ETag: "\"forte\"", ContentType: "text/calendar"));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.ETag.Should().Be("\"forte\"");
        _handler.Requests[1].Method.Should().Be("GET");
    }

    /// <summary>
    /// Servidores fora da norma devolvem o ETag sem aspas, e a propriedade tipada do
    /// <c>HttpClient</c> lança <see cref="FormatException"/> ao analisá-lo.
    /// </summary>
    [Fact]
    public async Task CreateAsync_EtagSemAspas_NaoLancaEGuardaOValorCru()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.Created, ETag: "2134-314"));

        var resultado = await CreateProvider().CreateAsync(
            _account, CalendarioLocal(_account.Id), Compromisso());

        resultado.Succeeded.Should().BeTrue();
        resultado.ETag.Should().Be("2134-314");
    }

    [Fact]
    public async Task UpdateAsync_ComEtagConhecido_VaiComPrecondicao()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.NoContent, ETag: "\"2134-315\""));

        var resultado = await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), $"{Colecao}1.ics", "\"2134-314\"",
            Compromisso());

        resultado.Succeeded.Should().BeTrue();
        _handler.Requests[0].Headers["If-Match"].Should().Be("\"2134-314\"");
    }

    [Fact]
    public async Task UpdateAsync_PrecondicaoRecusada_EConflitoNaoFalha()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.PreconditionFailed));

        var resultado = await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), $"{Colecao}1.ics", "\"antigo\"",
            Compromisso());

        resultado.Succeeded.Should().BeFalse();
        resultado.IsConflict.Should().BeTrue();
    }

    /// <summary>
    /// Alterado aqui e apagado lá: sobrescrever ressuscitaria o que alguém apagou de
    /// propósito, e apagar aqui descartaria a edição. Quem decide é o usuário.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RecursoJaExcluidoNoServidor_EConflito()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.NotFound));

        var resultado = await CreateProvider().UpdateAsync(
            _account, CalendarioLocal(_account.Id), $"{Colecao}1.ics", "\"1\"",
            Compromisso());

        resultado.IsConflict.Should().BeTrue();
    }

    /// <summary>
    /// Já não está lá é exatamente o estado desejado. Tratar como falha faria a exclusão
    /// local ficar pendente para sempre.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RecursoJaAusente_ContaComoSucesso()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.NotFound));

        var resultado = await CreateProvider().DeleteAsync(
            _account, CalendarioLocal(_account.Id), $"{Colecao}1.ics", "\"1\"");

        resultado.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_SempreVaiComPrecondicao()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.NoContent));

        await CreateProvider().DeleteAsync(
            _account, CalendarioLocal(_account.Id), $"{Colecao}1.ics", "\"1\"");

        _handler.Requests[0].Headers["If-Match"].Should().Be("\"1\"");
    }

    // ---- Leitura em lote ---------------------------------------------------------------

    [Fact]
    public async Task FetchResourcesAsync_VariosRecursos_TrazOCalendarioIntegro()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/calendars/joao/agenda/1.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <d:getetag>"fffff-abcd2"</d:getetag>
                    <c:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Outro Cliente//EN
            BEGIN:VEVENT
            UID:reuniao-1@exemplo.com
            SEQUENCE:3
            DTSTAMP:20260805T120000Z
            DTSTART:20260810T170000Z
            DTEND:20260810T180000Z
            SUMMARY:Reunião de projeto
            X-OUTRO-CLIENTE:preservar
            END:VEVENT
            END:VCALENDAR</c:calendar-data>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/calendars/joao/agenda/2.ics</d:href>
                <d:status>HTTP/1.1 404 Not Found</d:status>
              </d:response>
            </d:multistatus>
            """));

        var recursos = await CreateProvider().FetchResourcesAsync(
            _account, CalendarioLocal(_account.Id), [$"{Colecao}1.ics", $"{Colecao}2.ics"]);

        var achado = recursos.Should().ContainSingle().Subject;
        achado.Href.Should().Be($"{Colecao}1.ics");
        achado.Event!.Uid.Should().Be("reuniao-1@exemplo.com");

        // O SEQUENCE do documento é a versão que decide a precedência no caminho iCalendar.
        achado.Version.Sequence.Should().Be(3);

        // O documento cru viaja junto para ser preservado, não para ser lido de novo: o que
        // este produto não modela morre se a reescrita partir só do modelo.
        achado.ICalendar.Should().Contain("X-OUTRO-CLIENTE:preservar");

        // O corpo pede o calendar-data vazio, sem filtro: reescrever um objeto sem as
        // propriedades que outro cliente pôs é perda de dado silenciosa.
        _handler.Requests[0].Body.Should().Contain("calendar-multiget");
        _handler.Requests[0].Body.Should().Contain("calendar-data");
    }

    // ---- Teste de conexão ---------------------------------------------------------------

    [Fact]
    public async Task TestAsync_CredencialRecusada_ReportaFalhaDeAutenticacao()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.Unauthorized));

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.Succeeded.Should().BeFalse();
        resultado.IsAuthenticationFailure.Should().BeTrue();
    }

    /// <summary>
    /// O servidor aceita a conexão e devolve <c>unauthenticated</c>: é credencial inválida,
    /// não ausência de suporte.
    /// </summary>
    [Fact]
    public async Task TestAsync_PrincipalNaoAutenticado_ReportaFalhaDeAutenticacao()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
            <response>
              <href>/</href>
              <propstat>
                <prop><current-user-principal><unauthenticated/></current-user-principal></prop>
                <status>HTTP/1.1 200 OK</status>
              </propstat>
            </response>
            """)));

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.IsAuthenticationFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TestAsync_ServidorResponde_ReportaSucesso()
    {
        _handler.Reply(new CalDavReply(HttpStatusCode.MultiStatus, MultiStatus("""
            <response>
              <href>/</href>
              <propstat>
                <prop><current-user-principal><href>/principals/joao/</href></current-user-principal></prop>
                <status>HTTP/1.1 200 OK</status>
              </propstat>
            </response>
            """)));

        var resultado = await CreateProvider().TestAsync(_account);

        resultado.Succeeded.Should().BeTrue();
    }
}
