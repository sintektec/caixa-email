using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Infrastructure.Calendar.Rest;

/// <summary>
/// Agenda do Google, pela Calendar API v3.
/// </summary>
/// <remarks>
/// <para>
/// A Google mantém um endpoint CalDAV, mas declara a compatibilidade como parcial e
/// recomenda esta API. Ver D-026.
/// </para>
/// <para>
/// <b>A sincronização incremental aqui é a mais limpa dos três.</b> O <c>syncToken</c> cobre
/// tudo o que se precisa: alterações e exclusões vêm na mesma listagem, a exclusão como
/// <c>status: "cancelled"</c>. O token vencido responde <b>410</b> com
/// <c>fullSyncRequired</c>, e a recuperação é descartá-lo e refazer do zero.
/// </para>
/// <para>
/// <b><c>singleEvents</c> fica em <c>false</c>, que é o padrão.</b> Com ele ligado a Google
/// expande a recorrência em ocorrências, e este produto guarda o mestre com a <c>RRULE</c> e
/// expande na hora de desenhar. É a mesma razão pela qual o <c>calendarView/delta</c> do
/// Graph foi recusado. Mudar esse parâmetro entre a passada inicial e a incremental também
/// invalida o token — a API exige que ele seja o mesmo.
/// </para>
/// </remarks>
public sealed class GoogleCalendarSyncProvider : ICalendarSyncProvider
{
    private const string ApiRoot = "https://www.googleapis.com/calendar/v3/";

    /// <summary>Escopo de leitura e escrita da agenda.</summary>
    private static readonly string[] CalendarScopes = ["https://www.googleapis.com/auth/calendar"];

    /// <summary>Quantos eventos por página são pedidos.</summary>
    private const int PageSize = 250;

    /// <summary>Quantas páginas são seguidas numa passada.</summary>
    private const int MaxPages = 50;

    private readonly CalendarRestClient _client;
    private readonly ILogger<GoogleCalendarSyncProvider> _logger;

    public GoogleCalendarSyncProvider(
        CalendarRestClient client, ILogger<GoogleCalendarSyncProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public CalendarProviderKind Provider => CalendarProviderKind.GoogleCalendar;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.CalendarProvider != CalendarProviderKind.GoogleCalendar)
        {
            return [];
        }

        try
        {
            var authentication = await AuthenticateAsync(account, cancellationToken).ConfigureAwait(false);

            if (authentication is null)
            {
                return [];
            }

            var response = await _client.SendAsync(
                HttpMethod.Get, new Uri($"{ApiRoot}users/me/calendarList"), authentication,
                json: null, ifMatch: null, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess || response.Json() is not { } payload)
            {
                _logger.LogWarning(
                    "A listagem de calendários da Google respondeu HTTP {Status}.",
                    (int)response.StatusCode);

                return [];
            }

            var found = new List<RemoteCalendarDescriptor>();

            foreach (var item in payload.Array("items"))
            {
                if (item.Text("id") is not { Length: > 0 } id)
                {
                    continue;
                }

                // accessRole reader e freeBusyReader não aceitam escrita; gravar ali
                // devolveria 403 a cada tentativa.
                var role = item.Text("accessRole");
                var readOnly = role is not ("owner" or "writer");

                found.Add(new RemoteCalendarDescriptor(
                    id,
                    item.Text("summaryOverride") ?? item.Text("summary") ?? "Agenda",
                    item.Text("backgroundColor"),
                    readOnly,
                    CTag: null,
                    SyncToken: null));
            }

            return found;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "A descoberta de calendários da Google da conta {AccountId} falhou.", account.Id);

            return [];
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.AuthenticationType != AuthenticationType.OAuth2
            || account.OAuthProvider != OAuthProviderKind.Google)
        {
            return ConnectionTestResult.Failure(
                "A agenda da Google exige autenticação Google (OAuth 2.0).");
        }

        try
        {
            var authentication = await AuthenticateAsync(account, cancellationToken).ConfigureAwait(false);

            if (authentication is null)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "O acesso à agenda ainda não foi autorizado nesta conta Google. "
                    + "Refaça a autenticação para conceder a permissão de calendário.");
            }

            var response = await _client.SendAsync(
                HttpMethod.Get, new Uri($"{ApiRoot}users/me/calendarList?maxResults=1"),
                authentication, json: null, ifMatch: null, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "A Google recusou o acesso à agenda desta conta.");
            }

            return response.IsSuccess
                ? ConnectionTestResult.Success()
                : ConnectionTestResult.Failure(
                    $"A Calendar API respondeu HTTP {(int)response.StatusCode}.");
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

        var syncToken = calendar.SyncToken;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var isFull = syncToken is null;
            var changes = new List<RemoteCalendarChange>();
            string? pageToken = null;
            string? nextSyncToken = null;
            var rejected = false;

            for (var page = 0; page < MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await _client.SendAsync(
                    HttpMethod.Get, BuildEventsUrl(calendar.CollectionUrl, syncToken, pageToken),
                    authentication, json: null, ifMatch: null, cancellationToken)
                    .ConfigureAwait(false);

                // 410 com fullSyncRequired: o token venceu. Descartar e refazer do zero é a
                // recuperação que a própria API manda fazer — e não apagar o cache local, que
                // a passada completa vai reconciliar pelo ETag.
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    _logger.LogInformation(
                        "A Google recusou o token da agenda {CalendarId}; a passada será completa.",
                        calendar.Id);

                    rejected = true;
                    break;
                }

                if (!response.IsSuccess || response.Json() is not { } payload)
                {
                    throw new InvalidOperationException(
                        $"A Calendar API respondeu HTTP {(int)response.StatusCode} à leitura da agenda.");
                }

                foreach (var item in payload.Array("items"))
                {
                    if (ReadEvent(item) is { } change)
                    {
                        changes.Add(change);
                    }
                }

                pageToken = payload.Text("nextPageToken");
                nextSyncToken = payload.Text("nextSyncToken") ?? nextSyncToken;

                if (pageToken is null)
                {
                    break;
                }
            }

            if (rejected)
            {
                syncToken = null;
                continue;
            }

            if (pageToken is not null)
            {
                _logger.LogWarning(
                    "A agenda {CalendarId} ainda tinha páginas após {Paginas}; o restante virá "
                    + "no próximo ciclo.", calendar.Id, MaxPages);
            }

            return new RemoteCalendarChanges(
                changes,
                // Sem nextSyncToken a passada ficou incompleta: preservar o anterior faria a
                // próxima pular o que faltou, e guardar nulo forçaria outra passada completa
                // — que é o lado certo do erro.
                nextSyncToken,
                CTag: null,
                HasMore: false,
                IsFullEnumeration: isFull);
        }

        throw new InvalidOperationException(
            "A Calendar API recusou o token de sincronização duas vezes seguidas.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nunca é chamado na prática: a listagem da Google já traz o evento inteiro. Existe
    /// porque a porta o exige.
    /// </remarks>
    public Task<IReadOnlyList<RemoteCalendarChange>> FetchResourcesAsync(
        Account account,
        RemoteCalendar calendar,
        IReadOnlyCollection<string> hrefs,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteCalendarChange>>([]);

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

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var response = await _client.SendAsync(
            HttpMethod.Post, EventsUri(calendar.CollectionUrl), authentication,
            WriteEvent(calendarEvent), ifMatch: null, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return RemoteWriteResult.Failure(DescribeFailure(response));
        }

        return ReadWriteResult(response)
            ?? RemoteWriteResult.Failure("A Calendar API aceitou a gravação sem devolver o evento.");
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

        var authentication = await RequireAuthenticationAsync(account, cancellationToken)
            .ConfigureAwait(false);

        var response = await _client.SendAsync(
            HttpMethod.Patch, EventUri(calendar.CollectionUrl, href), authentication,
            WriteEvent(calendarEvent), knownETag, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return RemoteWriteResult.Conflict(
                "O compromisso mudou no servidor depois da última sincronização.");
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return RemoteWriteResult.Conflict(
                "O compromisso foi excluído no servidor enquanto era alterado aqui.");
        }

        if (!response.IsSuccess)
        {
            return RemoteWriteResult.Failure(DescribeFailure(response));
        }

        return ReadWriteResult(response) ?? RemoteWriteResult.Success(href, null);
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

        var response = await _client.SendAsync(
            HttpMethod.Delete, EventUri(calendar.CollectionUrl, href), authentication,
            json: null, knownETag, cancellationToken).ConfigureAwait(false);

        // A Google devolve 410 ao excluir o que já foi excluído — é o estado desejado, não
        // uma falha.
        if (response.IsSuccess
            || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
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

    private static Uri EventsUri(string calendarId)
        => new($"{ApiRoot}calendars/{Uri.EscapeDataString(calendarId)}/events");

    private static Uri EventUri(string calendarId, string eventId)
        => new($"{ApiRoot}calendars/{Uri.EscapeDataString(calendarId)}/events/"
            + Uri.EscapeDataString(eventId));

    private static Uri BuildEventsUrl(string calendarId, string? syncToken, string? pageToken)
    {
        var url = $"{EventsUri(calendarId)}?maxResults={PageSize}&showDeleted=true";

        // singleEvents fica em false (o padrão) para preservar o mestre da série. Ele também
        // não pode variar entre a passada inicial e a incremental — a API invalida o token.
        if (syncToken is { Length: > 0 })
        {
            url += $"&syncToken={Uri.EscapeDataString(syncToken)}";
        }

        if (pageToken is { Length: > 0 })
        {
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        }

        return new Uri(url, UriKind.Absolute);
    }

    private async Task<AuthenticationHeaderValue?> AuthenticateAsync(
        Account account, CancellationToken cancellationToken)
        => await _client.BuildAuthenticationAsync(account, CalendarScopes, cancellationToken)
            .ConfigureAwait(false);

    private async Task<AuthenticationHeaderValue> RequireAuthenticationAsync(
        Account account, CancellationToken cancellationToken)
        => await AuthenticateAsync(account, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "O acesso à agenda desta conta Google ainda não foi autorizado.");

    /// <summary>Lê um evento da Calendar API.</summary>
    private static RemoteCalendarChange? ReadEvent(JsonElement item)
    {
        if (item.Text("id") is not { Length: > 0 } id)
        {
            return null;
        }

        // A exclusão vem como um evento comum com status "cancelled" — é assim que a
        // listagem incremental a reporta, e não há outro sinal.
        if (string.Equals(item.Text("status"), "cancelled", StringComparison.Ordinal))
        {
            return RemoteCalendarChange.Removed(id);
        }

        if (ReadDateTime(item.Object("start")) is not { } startsAt)
        {
            return null;
        }

        var isAllDay = item.Object("start")?.Text("date") is not null;
        var updated = item.Timestamp("updated");

        var data = new CalendarEventData
        {
            // O iCalUID atravessa sistemas; o id é a identidade de rede desta API.
            Uid = item.Text("iCalUID") is { Length: > 0 } icalUid ? icalUid : id,
            Summary = item.Text("summary") ?? string.Empty,
            Description = item.Text("description"),
            Location = item.Text("location"),
            MeetingUrl = item.Text("hangoutLink"),
            StartsAt = startsAt,
            EndsAt = ReadDateTime(item.Object("end")) ?? startsAt,
            IsAllDay = isAllDay,
            TimeZoneId = item.Object("start")?.Text("timeZone"),
            Status = CalendarEventStatus.Confirmed,
            OrganizerAddress = ReadAddress(item.Object("organizer")),
            OrganizerDisplayName = item.Object("organizer")?.Text("displayName"),
            // A RRULE vem crua, exatamente como este produto a guarda.
            RecurrenceRule = ReadRecurrence(item),
            Attendees = [.. ReadAttendees(item)],
        };

        return RemoteCalendarChange.Upserted(
            id,
            item.Text("etag"),
            data,
            // A Google também não expõe SEQUENCE na API. A precedência é pelo "updated" —
            // ver D-029.
            updated is { } stamp ? RemoteVersion.FromTimestamp(stamp) : RemoteVersion.Unknown);
    }

    /// <summary>
    /// Lê a regra de repetição.
    /// </summary>
    /// <remarks>
    /// O vetor <c>recurrence</c> traz linhas inteiras da norma — <c>RRULE:</c>, <c>EXDATE:</c>,
    /// <c>RDATE:</c>. Só a <c>RRULE</c> é aproveitada, porque é o que este produto modela; as
    /// demais ficariam guardadas sem serem aplicadas, o que é pior do que não guardá-las.
    /// </remarks>
    private static string? ReadRecurrence(JsonElement item)
    {
        foreach (var line in item.Array("recurrence"))
        {
            if (line.GetString() is { } text
                && text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            {
                return text["RRULE:".Length..];
            }
        }

        return null;
    }

    private static IEnumerable<CalendarAttendeeData> ReadAttendees(JsonElement item)
    {
        foreach (var attendee in item.Array("attendees"))
        {
            if (attendee.Text("email") is not { } raw
                || !EmailAddress.TryParse(raw, out var address, out _))
            {
                continue;
            }

            var response = attendee.Text("responseStatus") switch
            {
                "accepted" => AttendeeResponse.Accepted,
                "declined" => AttendeeResponse.Declined,
                "tentative" => AttendeeResponse.Tentative,
                _ => AttendeeResponse.NeedsAction,
            };

            yield return new CalendarAttendeeData(
                address,
                attendee.Text("displayName"),
                attendee.Bool("optional") ? AttendeeRole.Optional : AttendeeRole.Required,
                response);
        }
    }

    private static EmailAddress? ReadAddress(JsonElement? holder)
        => holder?.Text("email") is { } raw && EmailAddress.TryParse(raw, out var address, out _)
            ? address
            : null;

    /// <summary>
    /// Lê o início ou o fim de um evento.
    /// </summary>
    /// <remarks>
    /// A Google usa <c>dateTime</c> com deslocamento para evento com hora e <c>date</c> para
    /// o de dia inteiro. Ler só o primeiro faria todo evento de dia inteiro desaparecer da
    /// agenda.
    /// </remarks>
    private static DateTimeOffset? ReadDateTime(JsonElement? holder)
    {
        if (holder is not { } value)
        {
            return null;
        }

        if (value.Text("dateTime") is { } instant
            && DateTimeOffset.TryParse(
                instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        if (value.Text("date") is { } day
            && DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var date))
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        return null;
    }

    private static RemoteWriteResult? ReadWriteResult(RestResponse response)
    {
        if (response.Json() is not { } payload || payload.Text("id") is not { Length: > 0 } id)
        {
            return null;
        }

        var updated = payload.Timestamp("updated");

        return RemoteWriteResult.Success(
            id,
            payload.Text("etag") ?? response.ETag,
            iCalendar: null,
            updated is { } stamp ? RemoteVersion.FromTimestamp(stamp) : RemoteVersion.Unknown);
    }

    /// <summary>
    /// Descreve uma falha em texto exibível.
    /// </summary>
    /// <remarks>
    /// Só código e motivo declarado. A mensagem da API pode citar o assunto do compromisso,
    /// e conteúdo não entra em texto de interface nem em log.
    /// </remarks>
    private static string DescribeFailure(RestResponse response)
    {
        var reason = response.Json()
            ?.Object("error")
            ?.Array("errors")
            .Select(e => e.Text("reason"))
            .FirstOrDefault(r => r is not null);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "A Google recusou o acesso à agenda desta conta.",
            HttpStatusCode.Forbidden when reason is "rateLimitExceeded" or "userRateLimitExceeded"
                => "A Google limitou a quantidade de pedidos; a próxima passada tenta de novo.",
            HttpStatusCode.Forbidden => "A conta não tem permissão de escrita nesta agenda.",
            HttpStatusCode.TooManyRequests
                => "A Google limitou a quantidade de pedidos; a próxima passada tenta de novo.",
            _ when reason is { Length: > 0 } => $"A Calendar API recusou a operação ({reason}).",
            _ => $"A Calendar API respondeu HTTP {(int)response.StatusCode}.",
        };
    }

    private static string WriteEvent(CalendarEventData data)
    {
        var payload = new JsonObject
        {
            ["summary"] = data.Summary,
            ["start"] = WriteInstant(data.StartsAt, data.IsAllDay),
            ["end"] = WriteInstant(data.EndsAt ?? data.StartsAt, data.IsAllDay),
        };

        if (!string.IsNullOrWhiteSpace(data.Description))
        {
            payload["description"] = data.Description;
        }

        if (!string.IsNullOrWhiteSpace(data.Location))
        {
            payload["location"] = data.Location;
        }

        if (!string.IsNullOrWhiteSpace(data.RecurrenceRule))
        {
            // A Google aceita a RRULE crua, que é como este produto a guarda — nenhuma
            // tradução no caminho, nenhuma segunda interpretação da norma para divergir.
            payload["recurrence"] = new JsonArray($"RRULE:{data.RecurrenceRule}");
        }

        if (data.Attendees.Count > 0)
        {
            var attendees = new JsonArray();

            foreach (var attendee in data.Attendees)
            {
                var entry = new JsonObject { ["email"] = attendee.Address.Value };

                if (attendee.DisplayName is { Length: > 0 } name)
                {
                    entry["displayName"] = name;
                }

                if (attendee.Role == AttendeeRole.Optional)
                {
                    entry["optional"] = true;
                }

                attendees.Add(entry);
            }

            payload["attendees"] = attendees;
        }

        return payload.ToJsonString();
    }

    private static JsonObject WriteInstant(DateTimeOffset? value, bool isAllDay)
    {
        var instant = value ?? default;

        return isAllDay
            ? new JsonObject
            {
                ["date"] = instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }
            : new JsonObject
            {
                ["dateTime"] = instant.UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["timeZone"] = "UTC",
            };
    }
}
