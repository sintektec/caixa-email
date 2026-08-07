using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Infrastructure.Calendar.CalDav;

/// <summary>
/// Cliente CalDAV (RFC 4791), com sincronização incremental da RFC 6578.
/// </summary>
/// <remarks>
/// <para>
/// Cobre Nextcloud, ownCloud, Baikal, Fastmail, iCloud, SOGo, Radicale, DAViCal e o
/// endpoint de compatibilidade da Google. <b>Não cobre Exchange Online</b>, que nunca
/// implementou CalDAV — ver D-026.
/// </para>
/// <para>
/// <b>Há dois caminhos de leitura, e o segundo não é opcional.</b> O preferido é o REPORT
/// <c>sync-collection</c>, que traz só o que mudou. Servidores que não o implementam ficam
/// com o <c>CTag</c>: uma marca da coleção inteira que muda a cada alteração. Quando ela
/// muda, lista-se a coleção pedindo <b>apenas os ETags</b> e comparam-se os conjuntos.
/// </para>
/// <para>
/// <b>O nome do recurso não tem relação com o <c>UID</c>.</b> Que muitos servidores usem
/// <c>{UID}.ics</c> é coincidência, não contrato: a Google usa identificadores internos e o
/// iCloud renomeia. Os dois identificadores são guardados separados — <c>href</c> é a
/// identidade de rede, <c>UID</c> é a identidade de calendário.
/// </para>
/// </remarks>
public sealed class CalDavCalendarSyncProvider : ICalendarSyncProvider
{
    /// <summary>
    /// Quantos <c>href</c> vão em cada <c>calendar-multiget</c>.
    /// </summary>
    /// <remarks>
    /// O equilíbrio prático: um lote por recurso multiplicaria as viagens, e um lote único
    /// com milhares de <c>href</c> derruba servidores que limitam o tamanho do corpo.
    /// </remarks>
    private const int MultigetBatchSize = 50;

    /// <summary>Quantas colisões de nome são toleradas ao criar um recurso.</summary>
    /// <remarks>
    /// A URL do recurso é escolhida pelo cliente, e um 412 no <c>If-None-Match: *</c>
    /// significa que já existe algo com aquele nome. Com <c>Guid.CreateVersion7()</c> a
    /// colisão é praticamente impossível; três tentativas cobrem o caso em que o servidor
    /// devolve 412 por outro motivo sem que valha insistir.
    /// </remarks>
    private const int MaxCreateAttempts = 3;

    private readonly CalDavTransport _transport;
    private readonly ICalendarSerializer _serializer;
    private readonly ILogger<CalDavCalendarSyncProvider> _logger;

    public CalDavCalendarSyncProvider(
        CalDavTransport transport,
        ICalendarSerializer serializer,
        ILogger<CalDavCalendarSyncProvider> logger)
    {
        _transport = transport;
        _serializer = serializer;
        _logger = logger;
    }

    /// <inheritdoc />
    public CalendarProviderKind Provider => CalendarProviderKind.CalDav;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!TryGetRoot(account, out var root))
        {
            return [];
        }

        try
        {
            var authentication = await _transport
                .BuildAuthenticationAsync(account, cancellationToken).ConfigureAwait(false);

            if (authentication is null)
            {
                _logger.LogWarning(
                    "A conta {AccountId} não tem credencial disponível para o servidor de agenda.",
                    account.Id);

                return [];
            }

            var principal = await ResolveSingleHrefAsync(
                root, authentication, CalDavRequests.CurrentUserPrincipal(),
                DavXml.Dav + "current-user-principal", cancellationToken).ConfigureAwait(false);

            // Servidor que não expõe o principal ainda pode ter as coleções logo abaixo da
            // raiz configurada — é o caso do endpoint da Google, cujo caminho já é o do
            // usuário. Cair para a raiz é melhor do que desistir.
            var home = await ResolveSingleHrefAsync(
                principal ?? root, authentication, CalDavRequests.CalendarHomeSet(),
                DavXml.CalDav + "calendar-home-set", cancellationToken).ConfigureAwait(false)
                ?? principal ?? root;

            return await ListCollectionsAsync(home, authentication, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A descoberta falhar não pode derrubar o ciclo: a agenda local continua
            // funcionando sem servidor, e a próxima passada tenta de novo.
            _logger.LogWarning(ex, "A descoberta de calendários da conta {AccountId} falhou.", account.Id);

            return [];
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!TryGetRoot(account, out var root))
        {
            return ConnectionTestResult.Failure(
                "Informe o endereço HTTPS do servidor de agenda.");
        }

        try
        {
            var authentication = await _transport
                .BuildAuthenticationAsync(account, cancellationToken).ConfigureAwait(false);

            if (authentication is null)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "A senha desta conta não foi encontrada no Gerenciador de Credenciais do Windows. " +
                    "Informe a senha novamente nas configurações da conta.");
            }

            var response = await _transport.SendAsync(
                CalDavTransport.Propfind, root, authentication,
                CalDavRequests.CurrentUserPrincipal(), "application/xml; charset=utf-8",
                depth: "0", ifMatch: null, ifNoneMatchAny: false, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "O servidor de agenda recusou as credenciais. Contas com verificação em duas "
                    + "etapas costumam exigir uma senha de aplicativo.");
            }

            if (!response.IsMultiStatus)
            {
                return ConnectionTestResult.Failure(
                    $"O endereço não respondeu como um servidor CalDAV (HTTP {(int)response.StatusCode}).");
            }

            var document = DavXml.Parse(response.Body);

            if (document?.Descendants(DavXml.Dav + "unauthenticated").Any() == true)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "O servidor de agenda aceitou a conexão mas não reconheceu o usuário.");
            }

            return ConnectionTestResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteCalendarChanges> FetchChangesAsync(
        Account account, RemoteCalendar calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var collection = CalDavHref.ToRequestUri(CalDavHref.AsCollection(calendar.CollectionUrl));

        var attempted = await SyncCollectionAsync(
            collection, authentication, calendar.SyncToken, cancellationToken).ConfigureAwait(false);

        if (attempted is { } changes)
        {
            return changes;
        }

        // O servidor não fala sync-collection. Resta o CTag.
        return await SyncByCTagAsync(collection, authentication, calendar, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendarChange>> FetchResourcesAsync(
        Account account,
        RemoteCalendar calendar,
        IReadOnlyCollection<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(hrefs);

        if (hrefs.Count == 0)
        {
            return [];
        }

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var collection = CalDavHref.ToRequestUri(CalDavHref.AsCollection(calendar.CollectionUrl));
        var found = new List<RemoteCalendarChange>(hrefs.Count);

        foreach (var batch in hrefs.Chunk(MultigetBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // O corpo leva o href absoluto, e não a chave decodificada: um '#' ou um '?' no
            // nome do recurso quebraria o endereço remontado.
            var addresses = batch
                .Select(h => CalDavHref.ToRequestUri(h).AbsoluteUri)
                .ToList();

            var response = await _transport.SendAsync(
                CalDavTransport.Report, collection, authentication,
                CalDavRequests.CalendarMultiget(addresses), "application/xml; charset=utf-8",
                depth: "1", ifMatch: null, ifNoneMatchAny: false, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsMultiStatus)
            {
                _logger.LogWarning(
                    "A leitura em lote do calendário {CalendarId} respondeu HTTP {Status}.",
                    calendar.Id, (int)response.StatusCode);

                continue;
            }

            var document = DavXml.Parse(response.Body);

            if (document?.Root is null)
            {
                continue;
            }

            foreach (var element in document.Root.Elements(DavXml.Dav + "response"))
            {
                if (ReadResourceChange(element, response.RequestUri) is { } change
                    && change.Change == RemoteChangeKind.Upserted)
                {
                    found.Add(change);
                }
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<RemoteWriteResult> CreateAsync(
        Account account,
        RemoteCalendar calendar,
        CalendarEventData calendarEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var iCalendar = _serializer.WriteRequest(calendarEvent);

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var collection = CalDavHref.ToRequestUri(CalDavHref.AsCollection(calendar.CollectionUrl));

        for (var attempt = 0; attempt < MaxCreateAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Nome escolhido pelo cliente, como a norma permite. Guid v7 é ordenado no tempo
            // e não tem caractere que precise de escape.
            var target = new Uri(collection, $"{Guid.CreateVersion7():N}.ics");

            var response = await _transport.SendAsync(
                HttpMethod.Put, target, authentication, iCalendar, "text/calendar; charset=utf-8",
                depth: null, ifMatch: null, ifNoneMatchAny: true, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Colisão de nome, não conflito de conteúdo: outro nome resolve.
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden
                && DavXml.Parse(response.Body) is { } error
                && error.Descendants(DavXml.CalDav + "no-uid-conflict")
                    .FirstOrDefault()?.Element(DavXml.Dav + "href")?.Value is { } existing
                && CalDavHref.Resolve(response.RequestUri, existing) is { } existingUri)
            {
                // O UID já existe em outro recurso da mesma coleção, e o erro diz onde.
                // Gravar lá é o caminho — criar de novo repetiria o mesmo 403 para sempre.
                return await UpdateAsync(
                    account, calendar, CalDavHref.Key(existingUri), null, calendarEvent,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccess)
            {
                return RemoteWriteResult.Failure(DescribeFailure(response));
            }

            return await ConfirmWriteAsync(
                target, authentication, response.ETag, cancellationToken).ConfigureAwait(false);
        }

        return RemoteWriteResult.Failure(
            "O servidor recusou os nomes propostos para o novo compromisso.");
    }

    /// <inheritdoc />
    public async Task<RemoteWriteResult> UpdateAsync(
        Account account,
        RemoteCalendar calendar,
        string href,
        string? knownETag,
        CalendarEventData calendarEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var iCalendar = _serializer.WriteRequest(calendarEvent);

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var target = CalDavHref.ToRequestUri(href);

        var response = await _transport.SendAsync(
            HttpMethod.Put, target, authentication, iCalendar, "text/calendar; charset=utf-8",
            depth: null, ifMatch: knownETag, ifNoneMatchAny: false, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return RemoteWriteResult.Conflict(
                "O compromisso mudou no servidor depois da última sincronização.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Alterado aqui e apagado lá. Sobrescrever ressuscitaria o que alguém apagou de
            // propósito; apagar aqui descartaria a edição. Quem decide é o usuário.
            return RemoteWriteResult.Conflict(
                "O compromisso foi excluído no servidor enquanto era alterado aqui.");
        }

        if (!response.IsSuccess)
        {
            return RemoteWriteResult.Failure(DescribeFailure(response));
        }

        return await ConfirmWriteAsync(
            target, authentication, response.ETag, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteWriteResult> DeleteAsync(
        Account account,
        RemoteCalendar calendar,
        string href,
        string? knownETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var target = CalDavHref.ToRequestUri(href);

        var response = await _transport.SendAsync(
            HttpMethod.Delete, target, authentication, body: null, contentType: null,
            depth: null, ifMatch: knownETag, ifNoneMatchAny: false, cancellationToken)
            .ConfigureAwait(false);

        // Já não está lá: é exatamente o estado que se queria. Tratar como falha faria a
        // exclusão local ficar pendente para sempre.
        if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccess)
        {
            return RemoteWriteResult.Success(href, null);
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return RemoteWriteResult.Conflict(
                "O compromisso mudou no servidor antes de ser excluído.");
        }

        return RemoteWriteResult.Failure(DescribeFailure(response));
    }

    private static bool TryGetRoot(Account account, out Uri root)
    {
        root = null!;

        if (account.CalendarProvider != CalendarProviderKind.CalDav
            || string.IsNullOrWhiteSpace(account.CalendarUrl))
        {
            return false;
        }

        return Uri.TryCreate(account.CalendarUrl, UriKind.Absolute, out root!)
            && root.Scheme == Uri.UriSchemeHttps;
    }

    private async Task<AuthenticationHeaderValue> RequireAuthenticationAsync(
        Account account, CancellationToken cancellationToken)
        => await _transport.BuildAuthenticationAsync(account, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "A credencial do servidor de agenda não está disponível no cofre do Windows.");

    /// <summary>Emite um PROPFIND de profundidade zero e devolve o href aninhado que ele traz.</summary>
    private async Task<Uri?> ResolveSingleHrefAsync(
        Uri target,
        AuthenticationHeaderValue authentication,
        string body,
        XName property,
        CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(
            CalDavTransport.Propfind, target, authentication, body,
            "application/xml; charset=utf-8", depth: "0", ifMatch: null, ifNoneMatchAny: false,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsMultiStatus || DavXml.Parse(response.Body)?.Root is not { } root)
        {
            return null;
        }

        foreach (var element in root.Elements(DavXml.Dav + "response"))
        {
            if (DavXml.NestedHref(element, property) is { } href
                && CalDavHref.Resolve(response.RequestUri, href) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<RemoteCalendarDescriptor>> ListCollectionsAsync(
        Uri home, AuthenticationHeaderValue authentication, CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(
            CalDavTransport.Propfind, home, authentication, CalDavRequests.CalendarCollections(),
            "application/xml; charset=utf-8", depth: "1", ifMatch: null, ifNoneMatchAny: false,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsMultiStatus || DavXml.Parse(response.Body)?.Root is not { } root)
        {
            return [];
        }

        var found = new List<RemoteCalendarDescriptor>();

        foreach (var element in root.Elements(DavXml.Dav + "response"))
        {
            var resourceType = DavXml.FindProperty(element, DavXml.Dav + "resourcetype");

            // Só coleção de calendário. A primeira resposta é o próprio home, que é apenas
            // uma collection; e a mesma listagem traz inbox, outbox e notificações.
            if (resourceType?.Element(DavXml.CalDav + "calendar") is null)
            {
                continue;
            }

            if (!AcceptsEvents(element))
            {
                continue;
            }

            var href = element.Element(DavXml.Dav + "href")?.Value;

            if (CalDavHref.Resolve(response.RequestUri, href) is not { } absolute)
            {
                continue;
            }

            var url = CalDavHref.AsCollection(CalDavHref.Key(absolute));

            found.Add(new RemoteCalendarDescriptor(
                url,
                DavXml.PropertyText(element, DavXml.Dav + "displayname") ?? LastSegment(absolute),
                DavXml.PropertyText(element, DavXml.AppleICal + "calendar-color"),
                IsReadOnly(element),
                DavXml.PropertyText(element, DavXml.CalendarServer + "getctag"),
                DavXml.PropertyText(element, DavXml.Dav + "sync-token")));
        }

        return found;
    }

    /// <summary>
    /// Se a coleção aceita <c>VEVENT</c>.
    /// </summary>
    /// <remarks>
    /// A propriedade ausente significa "aceita tudo", pela RFC 4791. Presente e sem
    /// <c>VEVENT</c> é uma lista de tarefas: gravar um compromisso ali devolveria 403 a cada
    /// tentativa.
    /// </remarks>
    private static bool AcceptsEvents(XElement response)
    {
        var set = DavXml.FindProperty(response, DavXml.CalDav + "supported-calendar-component-set");

        if (set is null)
        {
            return true;
        }

        var components = set.Elements(DavXml.CalDav + "comp").ToList();

        return components.Count == 0
            || components.Any(c => string.Equals(
                (string?)c.Attribute("name"), "VEVENT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Se o servidor só concede leitura sobre a coleção.
    /// </summary>
    /// <remarks>
    /// A propriedade ausente é tratada como gravável: nem todo servidor a expõe, e presumir
    /// somente-leitura esconderia a agenda do usuário atrás de uma restrição que não existe.
    /// </remarks>
    private static bool IsReadOnly(XElement response)
    {
        var privileges = DavXml.FindProperty(response, DavXml.Dav + "current-user-privilege-set");

        if (privileges is null)
        {
            return false;
        }

        return !privileges
            .Elements(DavXml.Dav + "privilege")
            .Any(p => p.Element(DavXml.Dav + "write") is not null
                || p.Element(DavXml.Dav + "write-content") is not null
                || p.Element(DavXml.Dav + "all") is not null);
    }

    private static string LastSegment(Uri uri)
    {
        var segments = uri.Segments;

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var candidate = Uri.UnescapeDataString(segments[i]).Trim('/');

            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return "Agenda";
    }

    /// <summary>
    /// Tenta o REPORT <c>sync-collection</c>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> quando o servidor não implementa o relatório — o chamador cai
    /// para o <c>CTag</c>.
    /// </returns>
    private async Task<RemoteCalendarChanges?> SyncCollectionAsync(
        Uri collection,
        AuthenticationHeaderValue authentication,
        string? syncToken,
        CancellationToken cancellationToken)
    {
        var requestedToken = syncToken;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Depth: 0 é o único valor aceito no sync-collection; o escopo vai no
            // <sync-level>. Mandar Depth: 1 faz alguns servidores devolverem 400.
            var response = await _transport.SendAsync(
                CalDavTransport.Report, collection, authentication,
                CalDavRequests.SyncCollection(requestedToken), "application/xml; charset=utf-8",
                depth: "0", ifMatch: null, ifNoneMatchAny: false, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsMultiStatus)
            {
                return ReadSyncCollection(response, isFullEnumeration: requestedToken is null);
            }

            var document = DavXml.Parse(response.Body);

            // 403 no SabreDAV, 409 em outros. O código sozinho não distingue "token vencido"
            // de "sem permissão" — o discriminador é a pré-condição declarada no corpo.
            if (requestedToken is not null
                && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict
                && DavXml.DeclaresInvalidSyncToken(document))
            {
                _logger.LogInformation(
                    "O servidor recusou o token de sincronização da coleção {Coleção}; "
                    + "a passada será completa.", collection);

                requestedToken = null;
                continue;
            }

            return null;
        }

        return null;
    }

    private RemoteCalendarChanges ReadSyncCollection(
        CalDavResponse response, bool isFullEnumeration)
    {
        var document = DavXml.Parse(response.Body);

        if (document?.Root is not { } root)
        {
            return new RemoteCalendarChanges([], null, null, false, isFullEnumeration);
        }

        var changes = new List<RemoteCalendarChange>();
        var hasMore = false;

        foreach (var element in root.Elements(DavXml.Dav + "response"))
        {
            // O status filho direto da <response> — e não o de dentro de um <propstat> — é
            // o que fala do recurso. Confundir os dois faz "esta propriedade não existe"
            // virar "este recurso foi apagado", que é o erro que mais quebra cliente aqui.
            var status = DavXml.StatusCode(element.Element(DavXml.Dav + "status"));

            if (status == (int)HttpStatusCode.InsufficientStorage)
            {
                // Truncagem: a resposta extra é para a própria request-URI. Aplicar o que
                // veio, guardar o token novo e repetir. Ignorá-la faz a sincronização parar
                // no meio sem erro visível.
                hasMore = true;
                continue;
            }

            if (ReadResourceChange(element, response.RequestUri) is { } change)
            {
                changes.Add(change);
            }
        }

        var token = root.Element(DavXml.Dav + "sync-token")?.Value;

        return new RemoteCalendarChanges(
            changes,
            string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            null,
            hasMore,
            isFullEnumeration);
    }

    /// <summary>Caminho dos servidores que não falam <c>sync-collection</c>.</summary>
    private async Task<RemoteCalendarChanges> SyncByCTagAsync(
        Uri collection,
        AuthenticationHeaderValue authentication,
        RemoteCalendar calendar,
        CancellationToken cancellationToken)
    {
        var markers = await _transport.SendAsync(
            CalDavTransport.Propfind, collection, authentication,
            CalDavRequests.CollectionMarkers(), "application/xml; charset=utf-8",
            depth: "0", ifMatch: null, ifNoneMatchAny: false, cancellationToken)
            .ConfigureAwait(false);

        string? ctag = null;

        if (markers.IsMultiStatus && DavXml.Parse(markers.Body)?.Root is { } markerRoot)
        {
            ctag = markerRoot
                .Elements(DavXml.Dav + "response")
                .Select(e => DavXml.PropertyText(e, DavXml.CalendarServer + "getctag"))
                .FirstOrDefault(v => v is not null);
        }

        // O CTag é opaco: uns devolvem um contador, outros um GUID datado. Comparação é de
        // texto, sempre.
        if (ctag is not null && string.Equals(ctag, calendar.CTag, StringComparison.Ordinal))
        {
            // Nada mudou. E, principalmente, nada foi enumerado — declarar passada completa
            // aqui apagaria a coleção inteira.
            return new RemoteCalendarChanges([], null, ctag, false, IsFullEnumeration: false);
        }

        var listing = await _transport.SendAsync(
            CalDavTransport.Report, collection, authentication,
            CalDavRequests.CalendarQueryETags(), "application/xml; charset=utf-8",
            depth: "1", ifMatch: null, ifNoneMatchAny: false, cancellationToken)
            .ConfigureAwait(false);

        if (!listing.IsMultiStatus || DavXml.Parse(listing.Body)?.Root is not { } root)
        {
            throw new InvalidOperationException(
                $"O servidor de agenda respondeu HTTP {(int)listing.StatusCode} à listagem da coleção.");
        }

        var changes = root
            .Elements(DavXml.Dav + "response")
            .Select(e => ReadResourceChange(e, listing.RequestUri))
            .OfType<RemoteCalendarChange>()
            .Where(c => c.Change == RemoteChangeKind.Upserted)
            .ToList();

        // O CTag só é gravado depois de a listagem ter vindo inteira: gravá-lo antes faria a
        // passada seguinte pular uma coleção que ficou pela metade.
        return new RemoteCalendarChanges(changes, null, ctag, false, IsFullEnumeration: true);
    }

    /// <summary>Lê uma <c>&lt;D:response&gt;</c> de recurso.</summary>
    private RemoteCalendarChange? ReadResourceChange(XElement element, Uri requestUri)
    {
        var key = CalDavHref.KeyOf(requestUri, element.Element(DavXml.Dav + "href")?.Value);

        if (key is null)
        {
            return null;
        }

        var status = DavXml.StatusCode(element.Element(DavXml.Dav + "status"));

        if (status == (int)HttpStatusCode.NotFound)
        {
            return RemoteCalendarChange.Removed(key);
        }

        if (status is >= 400)
        {
            // 403 de sub-coleção que não sabe sincronizar, 423 de recurso travado — nada a
            // aplicar, e nada que signifique remoção.
            return null;
        }

        var etag = DavXml.PropertyText(element, DavXml.Dav + "getetag");
        var document = DavXml.PropertyText(element, DavXml.CalDav + "calendar-data");

        if (etag is null && document is null)
        {
            return null;
        }

        if (document is null)
        {
            // A listagem trouxe só a identidade; o conteúdo vem no calendar-multiget.
            return RemoteCalendarChange.Listed(key, etag);
        }

        // A leitura nunca lança: o documento vem de uma coleção que outro cliente escreveu,
        // e um .ics malformado entre milhares é rotina.
        if (_serializer.Read(document) is not { Events.Count: > 0 } parsed)
        {
            _logger.LogWarning("Recurso {Recurso} descartado por não ser interpretável.", key);

            return null;
        }

        var data = parsed.Events[0];

        // O CalDAV carrega o iCalendar íntegro, então o SEQUENCE está lá — é ele que decide
        // a precedência (D-024), e não o instante de alteração.
        return RemoteCalendarChange.Upserted(
            key, etag, data, RemoteVersion.FromSequence(data.Sequence), document);
    }

    /// <summary>
    /// Fecha uma escrita, relendo o recurso quando o servidor não devolveu ETag forte.
    /// </summary>
    /// <remarks>
    /// A RFC 4791 §5.3.4 é taxativa: quando o servidor modifica o objeto ao gravar — e eles
    /// modificam, normalizando fuso, injetando <c>SEQUENCE</c>, reescrevendo
    /// <c>DTSTAMP</c> —, ele <b>não pode</b> devolver ETag forte. Guardar um ETag adivinhado
    /// faz o <c>If-Match</c> seguinte falhar com 412 para sempre, ou pior: sobrescrever em
    /// silêncio o que o servidor gravou.
    /// </remarks>
    private async Task<RemoteWriteResult> ConfirmWriteAsync(
        Uri target,
        AuthenticationHeaderValue authentication,
        string? etagFromWrite,
        CancellationToken cancellationToken)
    {
        var href = CalDavHref.Key(target);

        if (etagFromWrite is not null)
        {
            return RemoteWriteResult.Success(href, etagFromWrite);
        }

        var reread = await _transport.SendAsync(
            HttpMethod.Get, target, authentication, body: null, contentType: null,
            depth: null, ifMatch: null, ifNoneMatchAny: false, cancellationToken)
            .ConfigureAwait(false);

        if (!reread.IsSuccess)
        {
            // A gravação passou; só a releitura falhou. Registrar sem ETag deixa a próxima
            // escrita incondicional, o que é pior do que ter o ETag e melhor do que inventar
            // um.
            _logger.LogInformation(
                "A releitura de {Recurso} depois da gravação respondeu HTTP {Status}.",
                href, (int)reread.StatusCode);

            return RemoteWriteResult.Success(href, null);
        }

        return RemoteWriteResult.Success(href, reread.ETag, reread.Body);
    }

    /// <summary>
    /// Descreve uma falha em texto exibível.
    /// </summary>
    /// <remarks>
    /// Só código e pré-condição declarada. O corpo da resposta pode trazer o assunto do
    /// compromisso, e ele não entra em mensagem que vai para a interface ou para o log.
    /// </remarks>
    private static string DescribeFailure(CalDavResponse response)
    {
        var precondition = DavXml.Parse(response.Body)
            ?.Descendants()
            .FirstOrDefault(e => e.Name.Namespace == DavXml.CalDav || e.Name.Namespace == DavXml.Dav)
            ?.Name.LocalName;

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "O servidor de agenda recusou as credenciais.",
            HttpStatusCode.Forbidden when precondition is "supported-calendar-component"
                => "Esta coleção do servidor não aceita compromissos.",
            HttpStatusCode.Forbidden when precondition is "valid-calendar-data"
                => "O servidor recusou o compromisso por considerar o documento inválido.",
            HttpStatusCode.Forbidden when precondition is "max-resource-size"
                => "O compromisso ficou maior do que o limite do servidor.",
            HttpStatusCode.Forbidden => "O servidor de agenda não autorizou a gravação.",
            HttpStatusCode.Conflict => "A coleção de destino não existe mais no servidor.",
            HttpStatusCode.Locked => "O compromisso está travado por outro cliente.",
            HttpStatusCode.InsufficientStorage => "A cota do calendário no servidor está esgotada.",
            HttpStatusCode.UnsupportedMediaType => "O servidor recusou o formato enviado.",
            _ => $"O servidor de agenda respondeu HTTP {(int)response.StatusCode}.",
        };
    }
}
