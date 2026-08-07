using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Infrastructure.Calendar;

/// <summary>
/// Lê e escreve iCalendar com o <c>Ical.Net</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A leitura nunca lança.</b> O documento vem de um anexo recebido pela rede, escolhido
/// por quem enviou a mensagem. Uma exceção aqui derrubaria a sincronização da conta inteira
/// por causa de uma mensagem malformada — e mensagem malformada é rotina, não exceção.
/// </para>
/// <para>
/// <b>Fuso: o instante é resolvido aqui, e nunca por nome do sistema operacional.</b> O
/// <c>Ical.Net</c> resolve o <c>TZID</c> pelo <c>VTIMEZONE</c> embutido no próprio
/// documento quando o nome não é IANA — que é o caso do Outlook, que emite nomes do Windows
/// como <c>E. South America Standard Time</c>. Quando é IANA, resolve pela base do NodaTime,
/// que vem dentro do pacote. Nenhum dos dois caminhos passa pela tabela do ICU que o
/// <c>InvariantGlobalization</c> remove — medido em 05/08/2026, os dois devolvem o instante
/// correto.
/// </para>
/// </remarks>
public sealed partial class IcalNetCalendarSerializer : ICalendarSerializer
{
    private readonly ILogger<IcalNetCalendarSerializer> _logger;

    public IcalNetCalendarSerializer(ILogger<IcalNetCalendarSerializer> logger) => _logger = logger;

    /// <summary>
    /// Propriedades em que Teams, Meet e Zoom colocam o endereço de entrada.
    /// </summary>
    /// <remarks>
    /// A norma não tem campo para isso, então cada produto inventou o seu. Ler as três é
    /// mais barato — e mais confiável — do que caçar a URL no corpo da descrição.
    /// </remarks>
    private static readonly string[] MeetingUrlProperties =
    [
        "X-MICROSOFT-SKYPETEAMSMEETINGURL",
        "X-GOOGLE-CONFERENCE",
        "X-ZOOM-MEETING-URL",
    ];

    /// <inheritdoc />
    public CalendarDocument? Read(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        Ical.Net.Calendar? calendar;

        try
        {
            calendar = Ical.Net.Calendar.Load(content);
        }
        catch (Exception ex)
        {
            // O conteúdo do convite não entra no log: é conteúdo de mensagem.
            _logger.LogWarning(ex, "Documento iCalendar recusado por não ser interpretável.");
            return null;
        }

        if (calendar is null)
        {
            return null;
        }

        var events = new List<CalendarEventData>();

        foreach (var source in calendar.Events)
        {
            if (ToData(source) is { } data)
            {
                events.Add(data);
            }
        }

        return new CalendarDocument(ParseMethod(calendar.Method), events);
    }

    /// <inheritdoc />
    public string WriteRequest(CalendarEventData calendarEvent)
        => Serialize("REQUEST", BuildEvent(calendarEvent, includeAttendees: true));

    /// <inheritdoc />
    public string WriteCancel(CalendarEventData calendarEvent)
    {
        var target = BuildEvent(calendarEvent, includeAttendees: true);
        target.Status = "CANCELLED";

        return Serialize("CANCEL", target);
    }

    /// <inheritdoc />
    public string WriteReply(
        CalendarEventData calendarEvent, EmailAddress respondent, AttendeeResponse response)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(respondent);

        // A resposta leva só quem respondeu. Reenviar a lista inteira faria o organizador
        // receber, de um participante, o estado que ele mesmo mantém.
        var target = BuildEvent(calendarEvent, includeAttendees: false);
        target.Attendees.Add(new Attendee($"mailto:{respondent.Value}")
        {
            CommonName = NameOf(calendarEvent, respondent),
            ParticipationStatus = ToPartStat(response),
        });

        return Serialize("REPLY", target);
    }

    /// <inheritdoc />
    public string WriteCounter(
        CalendarEventData calendarEvent, EmailAddress respondent, DateTimeOffset proposedStart)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(respondent);

        var target = BuildEvent(calendarEvent, includeAttendees: false);

        // A duração original é preservada: quem propõe outro horário está mudando quando,
        // não por quanto tempo.
        var duration = (calendarEvent.EndsAt ?? calendarEvent.StartsAt ?? proposedStart)
            - (calendarEvent.StartsAt ?? proposedStart);

        target.Start = ToCalDateTime(proposedStart);
        target.End = ToCalDateTime(proposedStart + duration);

        target.Attendees.Add(new Attendee($"mailto:{respondent.Value}")
        {
            CommonName = NameOf(calendarEvent, respondent),
            ParticipationStatus = ToPartStat(AttendeeResponse.Tentative),
        });

        return Serialize("COUNTER", target);
    }

    /// <inheritdoc />
    public IReadOnlyList<DateTimeOffset> ExpandOccurrences(
        string recurrenceRule, DateTimeOffset firstStart, DateTimeOffset from, DateTimeOffset until)
    {
        if (string.IsNullOrWhiteSpace(recurrenceRule) || until <= from)
        {
            return [];
        }

        try
        {
            var calendar = new Ical.Net.Calendar();
            var source = new Ical.Net.CalendarComponents.CalendarEvent
            {
                Uid = Guid.CreateVersion7().ToString("N"),
                Start = ToCalDateTime(firstStart),
                End = ToCalDateTime(firstStart.AddHours(1)),
                RecurrenceRule = new RecurrencePattern(recurrenceRule),
            };

            calendar.Events.Add(source);

            // GetOccurrences devolve uma sequência preguiçosa e potencialmente infinita —
            // uma RRULE sem COUNT nem UNTIL não termina. O TakeWhile é o que impede o
            // laço de rodar para sempre.
            return
            [
                .. calendar.GetOccurrences(ToCalDateTime(from))
                    .TakeWhile(o => o.Period.StartTime.AsUtc < until.UtcDateTime)
                    .Select(o => new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero))
            ];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Regra de recorrência recusada por não ser interpretável.");
            return [];
        }
    }

    private CalendarEventData? ToData(Ical.Net.CalendarComponents.CalendarEvent source)
    {
        if (string.IsNullOrWhiteSpace(source.Uid))
        {
            // Sem UID não há identidade nenhuma. Na prática o Ical.Net inventa um quando o
            // documento não traz — e por ser inventado a cada leitura, ele não serve para
            // casar com o que já está na agenda; quem cobre esse caso é a segunda via de
            // identidade do ImportInvitationHandler. Esta guarda existe para o dia em que a
            // biblioteca deixar de inventar.
            return null;
        }

        var attendees = new List<CalendarAttendeeData>();

        foreach (var attendee in source.Attendees)
        {
            if (ToAddress(attendee.Value?.ToString()) is { } address
                && !attendees.Any(a => a.Address == address))
            {
                attendees.Add(new CalendarAttendeeData(
                    address,
                    Blank(attendee.CommonName),
                    ToRole(attendee.Role),
                    ToResponse(attendee.ParticipationStatus)));
            }
        }

        return new CalendarEventData
        {
            Uid = source.Uid.Trim(),
            Sequence = source.Sequence,
            Summary = source.Summary?.Trim() ?? string.Empty,
            Description = Blank(source.Description),
            Location = Blank(source.Location),
            MeetingUrl = ExtractMeetingUrl(source),
            StartsAt = ToOffset(source.Start),
            EndsAt = ToOffset(source.End),
            IsAllDay = source.IsAllDay,
            TimeZoneId = Blank(source.Start?.TzId),
            Status = ToStatus(source.Status),
            OrganizerAddress = ToAddress(source.Organizer?.Value?.ToString()),
            OrganizerDisplayName = Blank(source.Organizer?.CommonName),
            RecurrenceRule = Blank(source.RecurrenceRule?.ToString()),
            Attendees = attendees,
        };
    }

    /// <summary>
    /// Acha o endereço de entrada da reunião on-line.
    /// </summary>
    /// <remarks>
    /// Primeiro nas propriedades próprias de cada produto; se nenhuma existir, na
    /// descrição, que é onde a maioria também repete o link. Sem isso o usuário teria de
    /// abrir o corpo da mensagem para entrar na reunião.
    /// </remarks>
    private static string? ExtractMeetingUrl(Ical.Net.CalendarComponents.CalendarEvent source)
    {
        foreach (var name in MeetingUrlProperties)
        {
            if (Blank(source.Properties[name]?.Value?.ToString()) is { } value)
            {
                return value;
            }
        }

        foreach (var text in new[] { source.Location, source.Description })
        {
            if (text is not null && MeetingUrlPattern().Match(text) is { Success: true } match)
            {
                return match.Value;
            }
        }

        return null;
    }

    /// <summary>Endereços de entrada dos serviços de reunião mais usados.</summary>
    [GeneratedRegex(
        @"https://(?:[\w.-]*\.)?(?:teams\.microsoft\.com|meet\.google\.com|zoom\.us|webex\.com)/[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeetingUrlPattern();

    private Ical.Net.CalendarComponents.CalendarEvent BuildEvent(
        CalendarEventData data, bool includeAttendees)
    {
        ArgumentNullException.ThrowIfNull(data);

        var target = new Ical.Net.CalendarComponents.CalendarEvent
        {
            Uid = data.Uid,
            Sequence = data.Sequence,
            Summary = data.Summary,
            Description = data.Description,
            Location = data.Location,
        };

        if (data.StartsAt is { } start)
        {
            target.Start = ToCalDateTime(start);
            target.End = ToCalDateTime(data.EndsAt ?? start);
        }

        if (data.OrganizerAddress is { } organizer)
        {
            target.Organizer = new Organizer($"mailto:{organizer.Value}")
            {
                CommonName = data.OrganizerDisplayName,
            };
        }

        if (!string.IsNullOrWhiteSpace(data.RecurrenceRule))
        {
            try
            {
                target.RecurrenceRule = new RecurrencePattern(data.RecurrenceRule);
            }
            catch (Exception ex)
            {
                // Melhor emitir o convite sem a recorrência do que não emitir convite.
                _logger.LogWarning(ex, "Recorrência descartada ao montar o convite.");
            }
        }

        if (includeAttendees)
        {
            foreach (var attendee in data.Attendees)
            {
                target.Attendees.Add(new Attendee($"mailto:{attendee.Address.Value}")
                {
                    CommonName = attendee.DisplayName,
                    Role = ToRoleText(attendee.Role),
                    ParticipationStatus = ToPartStat(attendee.Response),
                    Rsvp = attendee.Response == AttendeeResponse.NeedsAction,
                });
            }
        }

        target.Status = ToStatusText(data.Status);

        return target;
    }

    private static string Serialize(string method, Ical.Net.CalendarComponents.CalendarEvent target)
    {
        var calendar = new Ical.Net.Calendar { Method = method };
        calendar.Events.Add(target);

        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>
    /// Converte um instante em <c>CalDateTime</c> UTC.
    /// </summary>
    /// <remarks>
    /// Sempre em UTC ao escrever: é a única forma que não depende de o destinatário
    /// entender o nome de fuso que este computador usa. Ler continua aceitando qualquer
    /// fuso, porque quem envia decide o formato.
    /// </remarks>
    private static CalDateTime ToCalDateTime(DateTimeOffset value)
        => new(value.UtcDateTime, "UTC");

    private static DateTimeOffset? ToOffset(CalDateTime? value)
        => value is null ? null : new DateTimeOffset(value.AsUtc, TimeSpan.Zero);

    private static EmailAddress? ToAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }

        return EmailAddress.TryParse(text, out var address) ? address : null;
    }

    private static string? NameOf(CalendarEventData data, EmailAddress address)
        => data.Attendees.FirstOrDefault(a => a.Address == address).DisplayName;

    private static CalendarMethod ParseMethod(string? method) => method?.ToUpperInvariant() switch
    {
        null or "" or "PUBLISH" => CalendarMethod.Publish,
        "REQUEST" => CalendarMethod.Request,
        "REPLY" => CalendarMethod.Reply,
        "CANCEL" => CalendarMethod.Cancel,
        "COUNTER" => CalendarMethod.Counter,
        _ => CalendarMethod.Unsupported,
    };

    private static CalendarEventStatus ToStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "CANCELLED" => CalendarEventStatus.Cancelled,
        "TENTATIVE" => CalendarEventStatus.Tentative,
        _ => CalendarEventStatus.Confirmed,
    };

    private static string ToStatusText(CalendarEventStatus status) => status switch
    {
        CalendarEventStatus.Cancelled => "CANCELLED",
        CalendarEventStatus.Tentative => "TENTATIVE",
        _ => "CONFIRMED",
    };

    private static AttendeeRole ToRole(string? role) => role?.ToUpperInvariant() switch
    {
        "OPT-PARTICIPANT" => AttendeeRole.Optional,
        "NON-PARTICIPANT" => AttendeeRole.Informational,
        "CHAIR" => AttendeeRole.Chair,
        _ => AttendeeRole.Required,
    };

    private static string ToRoleText(AttendeeRole role) => role switch
    {
        AttendeeRole.Optional => "OPT-PARTICIPANT",
        AttendeeRole.Informational => "NON-PARTICIPANT",
        AttendeeRole.Chair => "CHAIR",
        _ => "REQ-PARTICIPANT",
    };

    private static AttendeeResponse ToResponse(string? partStat) => partStat?.ToUpperInvariant() switch
    {
        "ACCEPTED" => AttendeeResponse.Accepted,
        "DECLINED" => AttendeeResponse.Declined,
        "TENTATIVE" => AttendeeResponse.Tentative,
        "DELEGATED" => AttendeeResponse.Delegated,
        _ => AttendeeResponse.NeedsAction,
    };

    private static string ToPartStat(AttendeeResponse response) => response switch
    {
        AttendeeResponse.Accepted => "ACCEPTED",
        AttendeeResponse.Declined => "DECLINED",
        AttendeeResponse.Tentative => "TENTATIVE",
        AttendeeResponse.Delegated => "DELEGATED",
        _ => "NEEDS-ACTION",
    };

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
