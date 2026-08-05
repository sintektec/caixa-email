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
using Sintek.Mail.Infrastructure.Security;

namespace Sintek.Mail.Infrastructure.Calendar.Rest;

/// <summary>
/// Agenda do Microsoft 365 e do Outlook.com, pelo Microsoft Graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o único caminho suportado para Exchange Online.</b> Ele nunca implementou CalDAV, e o
/// EWS está sendo desligado — bloqueio global em 01/10/2026, remoção até 04/2027. Ver D-026.
/// </para>
/// <para>
/// <b>O <c>delta</c> do Graph não serve para este produto, e isso não é preferência.</b> O
/// único <c>delta</c> disponível em <c>v1.0</c> é <c>/calendarView/delta</c>, que exige janela
/// de datas e <b>expande a recorrência em ocorrências</b>: uma reunião semanal de um ano vira
/// 52 objetos sem <c>RRULE</c>. Este produto guarda o mestre com a regra e expande na hora de
/// desenhar a grade — é o que permite editar "a série" e é o que a agenda local já faz com o
/// convite que chega por e-mail. Usar <c>calendarView</c> destruiria isso, e ainda esconderia
/// tudo o que caísse fora da janela.
/// </para>
/// <para>
/// A saída é <c>GET /me/calendars/{id}/events</c> com <c>$filter</c> em
/// <c>lastModifiedDateTime</c>, que devolve mestres de série e eventos únicos. O preço é que
/// <b>essa consulta não reporta exclusão</b> — o recurso simplesmente some. Daí a passada
/// completa periódica, que é justamente o que <c>IsFullEnumeration</c> existe para autorizar
/// (D-028). Ver D-029.
/// </para>
/// </remarks>
public sealed class GraphCalendarSyncProvider : ICalendarSyncProvider
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0/";

    /// <summary>Quantos eventos por página são pedidos.</summary>
    private const int PageSize = 100;

    /// <summary>Quantas páginas são seguidas numa passada antes de devolver o controle.</summary>
    /// <remarks>
    /// O Graph pagina com <c>@odata.nextLink</c>. Um servidor com defeito pode devolver
    /// página para sempre; sem teto o laço nunca termina. Cinquenta páginas de cem eventos
    /// cobrem qualquer agenda real numa passada só.
    /// </remarks>
    private const int MaxPages = 50;

    /// <summary>
    /// De quanto em quanto tempo uma passada completa é obrigatória.
    /// </summary>
    /// <remarks>
    /// É o intervalo máximo em que uma exclusão feita no servidor pode passar despercebida.
    /// Vinte e quatro horas equilibra o custo — listar a agenda inteira — contra o incômodo
    /// de ver um compromisso cancelado que já não existe lá.
    /// </remarks>
    private static readonly TimeSpan FullPassInterval = TimeSpan.FromHours(24);

    private readonly CalendarRestClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GraphCalendarSyncProvider> _logger;

    public GraphCalendarSyncProvider(
        CalendarRestClient client,
        TimeProvider timeProvider,
        ILogger<GraphCalendarSyncProvider> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public CalendarProviderKind Provider => CalendarProviderKind.MicrosoftGraph;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.CalendarProvider != CalendarProviderKind.MicrosoftGraph)
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
                HttpMethod.Get, new Uri($"{GraphRoot}me/calendars"), authentication,
                json: null, ifMatch: null, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess || response.Json() is not { } payload)
            {
                _logger.LogWarning(
                    "A listagem de calendários do Graph respondeu HTTP {Status}.",
                    (int)response.StatusCode);

                return [];
            }

            var found = new List<RemoteCalendarDescriptor>();

            foreach (var item in payload.Array("value"))
            {
                if (item.Text("id") is not { Length: > 0 } id)
                {
                    continue;
                }

                found.Add(new RemoteCalendarDescriptor(
                    id,
                    item.Text("name") ?? "Agenda",
                    item.Text("hexColor") is { Length: > 0 } hex ? hex : null,
                    // canEdit ausente é tratado como gravável: presumir somente-leitura
                    // esconderia a agenda atrás de uma restrição que não existe.
                    !item.Bool("canEdit", fallback: true),
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
                ex, "A descoberta de calendários do Graph da conta {AccountId} falhou.", account.Id);

            return [];
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.AuthenticationType != AuthenticationType.OAuth2
            || account.OAuthProvider != OAuthProviderKind.Microsoft)
        {
            // Não é configuração incompleta: o Graph recusa qualquer coisa que não seja
            // OAuth 2.0, e dizer isso é mais útil do que deixar a autenticação falhar.
            return ConnectionTestResult.Failure(
                "A agenda do Microsoft 365 exige autenticação Microsoft (OAuth 2.0).");
        }

        try
        {
            var authentication = await AuthenticateAsync(account, cancellationToken).ConfigureAwait(false);

            if (authentication is null)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "O acesso à agenda ainda não foi autorizado nesta conta Microsoft. "
                    + "Refaça a autenticação para conceder a permissão de calendário.");
            }

            var response = await _client.SendAsync(
                HttpMethod.Get, new Uri($"{GraphRoot}me/calendars?$top=1"), authentication,
                json: null, ifMatch: null, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ConnectionTestResult.AuthenticationFailure(
                    "O Microsoft 365 recusou o acesso à agenda desta conta.");
            }

            return response.IsSuccess
                ? ConnectionTestResult.Success()
                : ConnectionTestResult.Failure(
                    $"O Microsoft Graph respondeu HTTP {(int)response.StatusCode}.");
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

        var now = _timeProvider.GetUtcNow();
        var watermark = GraphSyncToken.Parse(calendar.SyncToken);
        var isFull = watermark.NeedsFullPass(now, FullPassInterval);

        var url = BuildEventsUrl(calendar.CollectionUrl, isFull ? null : watermark.Since);
        var changes = new List<RemoteCalendarChange>();
        var highest = watermark.Since;

        for (var page = 0; page < MaxPages && url is not null; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _client.SendAsync(
                HttpMethod.Get, url, authentication, json: null, ifMatch: null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Json() is not { } payload)
            {
                throw new InvalidOperationException(
                    $"O Microsoft Graph respondeu HTTP {(int)response.StatusCode} à leitura da agenda.");
            }

            foreach (var item in payload.Array("value"))
            {
                if (ReadEvent(item) is not { } change)
                {
                    continue;
                }

                changes.Add(change);

                if (change.Version.LastModifiedAt is { } stamp
                    && (highest is null || stamp > highest))
                {
                    highest = stamp;
                }
            }

            // O nextLink já carrega o $filter e o $top do pedido original.
            url = payload.Text("@odata.nextLink") is { Length: > 0 } next
                ? new Uri(next, UriKind.Absolute)
                : null;
        }

        if (url is not null)
        {
            _logger.LogWarning(
                "A agenda {CalendarId} ainda tinha páginas após {Paginas}; o restante virá "
                + "no próximo ciclo.", calendar.Id, MaxPages);
        }

        var token = new GraphSyncToken(highest, isFull ? now : watermark.LastFullPassAt);

        return new RemoteCalendarChanges(
            changes, token.ToString(), CTag: null, HasMore: false, IsFullEnumeration: isFull);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nunca é chamado na prática: a listagem do Graph já traz o evento inteiro, então
    /// <c>HasContent</c> é sempre verdadeiro e o motor não pede um segundo pedido. Existe
    /// porque a porta o exige, e devolve vazio em vez de lançar.
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

        var target = new Uri(
            $"{GraphRoot}me/calendars/{Uri.EscapeDataString(calendar.CollectionUrl)}/events");

        var response = await _client.SendAsync(
            HttpMethod.Post, target, authentication, WriteEvent(calendarEvent),
            ifMatch: null, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return RemoteWriteResult.Failure(DescribeFailure(response));
        }

        return ReadWriteResult(response)
            ?? RemoteWriteResult.Failure("O Microsoft Graph aceitou a gravação sem devolver o evento.");
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
            HttpMethod.Patch, EventUri(href), authentication, WriteEvent(calendarEvent),
            knownETag, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return RemoteWriteResult.Conflict(
                "O compromisso mudou no servidor depois da última sincronização.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
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
            HttpMethod.Delete, EventUri(href), authentication, json: null, knownETag,
            cancellationToken).ConfigureAwait(false);

        // Já não está lá é o estado desejado. Tratar como falha faria a exclusão local ficar
        // pendente para sempre.
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

    private static Uri EventUri(string eventId)
        => new($"{GraphRoot}me/events/{Uri.EscapeDataString(eventId)}");

    private static Uri BuildEventsUrl(string calendarId, DateTimeOffset? since)
    {
        var url =
            $"{GraphRoot}me/calendars/{Uri.EscapeDataString(calendarId)}/events"
            + $"?$top={PageSize}&$orderby=lastModifiedDateTime";

        if (since is { } stamp)
        {
            // O Graph exige o instante em ISO 8601 UTC e sem aspas no $filter.
            var literal = stamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            url += $"&$filter=lastModifiedDateTime ge {literal}";
        }

        return new Uri(url, UriKind.Absolute);
    }

    private async Task<AuthenticationHeaderValue?> AuthenticateAsync(
        Account account, CancellationToken cancellationToken)
        => await _client
            .BuildAuthenticationAsync(account, MicrosoftOAuthProvider.CalendarScopes, cancellationToken)
            .ConfigureAwait(false);

    private async Task<AuthenticationHeaderValue> RequireAuthenticationAsync(
        Account account, CancellationToken cancellationToken)
        => await AuthenticateAsync(account, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "O acesso à agenda desta conta Microsoft ainda não foi autorizado.");

    /// <summary>Lê um evento do Graph.</summary>
    private static RemoteCalendarChange? ReadEvent(JsonElement item)
    {
        if (item.Text("id") is not { Length: > 0 } id)
        {
            return null;
        }

        // Delta e algumas respostas marcam a exclusão com @removed; a listagem por $filter
        // não a reporta, e é por isso que existe a passada completa.
        if (item.Object("@removed") is not null)
        {
            return RemoteCalendarChange.Removed(id);
        }

        if (ReadDateTime(item.Object("start")) is not { } startsAt)
        {
            return null;
        }

        var endsAt = ReadDateTime(item.Object("end")) ?? startsAt;
        var lastModified = item.Timestamp("lastModifiedDateTime");

        var data = new CalendarEventData
        {
            // O iCalUId é a identidade de calendário e atravessa sistemas; o id é a de rede.
            // Guardar o primeiro como UID é o que faz um convite recebido por e-mail e o
            // mesmo evento vindo do Graph se reconhecerem.
            Uid = item.Text("iCalUId") is { Length: > 0 } icalUid ? icalUid : id,
            Summary = item.Text("subject") ?? string.Empty,
            Description = item.Object("body")?.Text("content") ?? item.Text("bodyPreview"),
            Location = item.Object("location")?.Text("displayName"),
            MeetingUrl = item.Object("onlineMeeting")?.Text("joinUrl") ?? item.Text("onlineMeetingUrl"),
            StartsAt = startsAt,
            EndsAt = endsAt,
            IsAllDay = item.Bool("isAllDay"),
            TimeZoneId = item.Object("start")?.Text("timeZone"),
            Status = item.Bool("isCancelled")
                ? CalendarEventStatus.Cancelled
                : CalendarEventStatus.Confirmed,
            OrganizerAddress = ReadAddress(item.Object("organizer")),
            OrganizerDisplayName = item.Object("organizer")?.Object("emailAddress")?.Text("name"),
            RecurrenceRule = GraphRecurrence.ToRRule(item.Object("recurrence")),
            Attendees = [.. ReadAttendees(item)],
        };

        return RemoteCalendarChange.Upserted(
            id,
            item.Text("@odata.etag"),
            data,
            // O Graph não expõe SEQUENCE. A precedência é pelo instante de alteração — ver
            // D-029, que é onde a regra de D-024 deixa de valer.
            lastModified is { } stamp ? RemoteVersion.FromTimestamp(stamp) : RemoteVersion.Unknown);
    }

    private static IEnumerable<CalendarAttendeeData> ReadAttendees(JsonElement item)
    {
        foreach (var attendee in item.Array("attendees"))
        {
            if (ReadAddress(attendee) is not { } address)
            {
                continue;
            }

            var role = string.Equals(attendee.Text("type"), "optional", StringComparison.OrdinalIgnoreCase)
                ? AttendeeRole.Optional
                : AttendeeRole.Required;

            var response = attendee.Object("status")?.Text("response") switch
            {
                "accepted" or "organizer" => AttendeeResponse.Accepted,
                "declined" => AttendeeResponse.Declined,
                "tentativelyAccepted" => AttendeeResponse.Tentative,
                _ => AttendeeResponse.NeedsAction,
            };

            yield return new CalendarAttendeeData(
                address, attendee.Object("emailAddress")?.Text("name"), role, response);
        }
    }

    private static EmailAddress? ReadAddress(JsonElement? holder)
        => holder?.Object("emailAddress")?.Text("address") is { } raw
            && EmailAddress.TryParse(raw, out var address, out _)
                ? address
                : null;

    /// <summary>
    /// Lê um <c>dateTimeTimeZone</c> do Graph.
    /// </summary>
    /// <remarks>
    /// O campo <c>dateTime</c> vem <b>sem deslocamento</b>, e o fuso vai em <c>timeZone</c>.
    /// Interpretá-lo como hora local da máquina daria o instante errado para quem está em
    /// outro fuso; o Graph declara UTC por padrão, e é assim que é lido.
    /// </remarks>
    private static DateTimeOffset? ReadDateTime(JsonElement? holder)
    {
        if (holder?.Text("dateTime") is not { } raw)
        {
            return null;
        }

        if (!DateTime.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc));
    }

    private static RemoteWriteResult? ReadWriteResult(RestResponse response)
    {
        if (response.Json() is not { } payload || payload.Text("id") is not { Length: > 0 } id)
        {
            return null;
        }

        var etag = payload.Text("@odata.etag") ?? response.ETag;
        var stamp = payload.Timestamp("lastModifiedDateTime");

        return RemoteWriteResult.Success(
            id,
            etag,
            iCalendar: null,
            stamp is { } value ? RemoteVersion.FromTimestamp(value) : RemoteVersion.Unknown);
    }

    /// <summary>
    /// Descreve uma falha em texto exibível.
    /// </summary>
    /// <remarks>
    /// Só código e o <c>code</c> do erro. A mensagem do Graph pode citar o assunto do
    /// compromisso, e conteúdo não entra em texto de interface nem em log.
    /// </remarks>
    private static string DescribeFailure(RestResponse response)
    {
        var code = response.Json()?.Object("error")?.Text("code");

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "O Microsoft 365 recusou o acesso à agenda desta conta.",
            HttpStatusCode.Forbidden => "A conta não tem permissão de escrita nesta agenda.",
            HttpStatusCode.TooManyRequests
                => "O Microsoft Graph limitou a quantidade de pedidos; a próxima passada tenta de novo.",
            _ when code is { Length: > 0 }
                => $"O Microsoft Graph recusou a operação ({code}).",
            _ => $"O Microsoft Graph respondeu HTTP {(int)response.StatusCode}.",
        };
    }

    private static string WriteEvent(CalendarEventData data)
    {
        var payload = new JsonObject
        {
            ["subject"] = data.Summary,
            ["isAllDay"] = data.IsAllDay,
            ["start"] = new JsonObject
            {
                ["dateTime"] = Format(data.StartsAt),
                ["timeZone"] = "UTC",
            },
            ["end"] = new JsonObject
            {
                ["dateTime"] = Format(data.EndsAt ?? data.StartsAt),
                ["timeZone"] = "UTC",
            },
        };

        if (!string.IsNullOrWhiteSpace(data.Description))
        {
            payload["body"] = new JsonObject
            {
                ["contentType"] = "text",
                ["content"] = data.Description,
            };
        }

        if (!string.IsNullOrWhiteSpace(data.Location))
        {
            payload["location"] = new JsonObject { ["displayName"] = data.Location };
        }

        if (data.Attendees.Count > 0)
        {
            var attendees = new JsonArray();

            foreach (var attendee in data.Attendees)
            {
                attendees.Add(new JsonObject
                {
                    ["emailAddress"] = new JsonObject
                    {
                        ["address"] = attendee.Address.Value,
                        ["name"] = attendee.DisplayName ?? attendee.Address.Value,
                    },
                    ["type"] = attendee.Role == AttendeeRole.Optional ? "optional" : "required",
                });
            }

            payload["attendees"] = attendees;
        }

        // A recorrência não é enviada: traduzir RRULE para o objeto do Graph exige mapear
        // exceções, contagem e limite por data, e um mapeamento parcial gravaria uma série
        // diferente da que o usuário vê. Compromisso recorrente criado aqui sobe como
        // encontro único até haver a tradução completa — visível e corrigível, ao contrário
        // de uma série silenciosamente errada.
        return payload.ToJsonString();
    }

    private static string Format(DateTimeOffset? value)
        => (value ?? default).UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}
