using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Abstractions.Calendar;

/// <summary>Um participante lido de um convite ou a escrever em um.</summary>
/// <param name="Address">Endereço.</param>
/// <param name="DisplayName">Nome exibido, quando declarado.</param>
/// <param name="Role">Papel na reunião.</param>
/// <param name="Response">Resposta declarada.</param>
public readonly record struct CalendarAttendeeData(
    EmailAddress Address,
    string? DisplayName,
    AttendeeRole Role,
    AttendeeResponse Response);

/// <summary>Um evento lido de um documento iCalendar.</summary>
public sealed record CalendarEventData
{
    /// <summary>O <c>UID</c> da norma.</summary>
    public required string Uid { get; init; }

    /// <summary>O <c>SEQUENCE</c>.</summary>
    public int Sequence { get; init; }

    /// <summary>Assunto.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Descrição.</summary>
    public string? Description { get; init; }

    /// <summary>Local.</summary>
    public string? Location { get; init; }

    /// <summary>Endereço de entrada da reunião on-line.</summary>
    public string? MeetingUrl { get; init; }

    /// <summary>
    /// Início como instante absoluto. Nulo em documentos que não trazem horário — o caso
    /// de um <c>REPLY</c>, que só carrega a resposta.
    /// </summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>Fim como instante absoluto.</summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>Se ocupa o dia inteiro.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>Fuso declarado, como veio no documento.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Situação declarada.</summary>
    public CalendarEventStatus Status { get; init; } = CalendarEventStatus.Confirmed;

    /// <summary>Endereço do organizador.</summary>
    public EmailAddress? OrganizerAddress { get; init; }

    /// <summary>Nome exibido do organizador.</summary>
    public string? OrganizerDisplayName { get; init; }

    /// <summary>Regra de recorrência, como texto.</summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>Participantes.</summary>
    public IReadOnlyList<CalendarAttendeeData> Attendees { get; init; } = [];
}

/// <summary>Um documento iCalendar entendido.</summary>
/// <param name="Method">Intenção declarada no <c>METHOD</c>.</param>
/// <param name="Events">Eventos que o documento traz.</param>
public readonly record struct CalendarDocument(
    CalendarMethod Method, IReadOnlyList<CalendarEventData> Events);

/// <summary>
/// Lê e escreve documentos iCalendar (RFC 5545).
/// </summary>
/// <remarks>
/// <para>
/// Porta, e não implementação, pelo motivo de sempre: a Aplicação não referencia
/// biblioteca de formato. Também é o que permite verificar os casos de uso sem depender do
/// comportamento de uma dependência externa.
/// </para>
/// <para>
/// <b>Teams, Google Meet e Outlook não precisam de três implementações.</b> Os três enviam
/// o mesmo <c>text/calendar; METHOD=REQUEST</c> da norma. Uma implementação correta cobre
/// os três, e cobre também Zoom, Webex e qualquer outro que respeite a RFC. Um conector por
/// produto seria triplicar o trabalho para obter menos.
/// </para>
/// </remarks>
public interface ICalendarSerializer
{
    /// <summary>
    /// Lê um documento iCalendar.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> quando o conteúdo não é um documento válido. A leitura nunca
    /// lança: o anexo vem da rede, e uma exceção aqui derrubaria a sincronização da conta
    /// inteira por causa de uma mensagem malformada.
    /// </returns>
    CalendarDocument? Read(string content);

    /// <summary>Escreve um convite (<c>METHOD=REQUEST</c>).</summary>
    string WriteRequest(CalendarEventData calendarEvent);

    /// <summary>Escreve um cancelamento (<c>METHOD=CANCEL</c>).</summary>
    string WriteCancel(CalendarEventData calendarEvent);

    /// <summary>
    /// Escreve a resposta de um participante (<c>METHOD=REPLY</c>).
    /// </summary>
    /// <param name="calendarEvent">Evento respondido.</param>
    /// <param name="respondent">Quem responde.</param>
    /// <param name="response">Resposta.</param>
    string WriteReply(
        CalendarEventData calendarEvent, EmailAddress respondent, AttendeeResponse response);

    /// <summary>
    /// Escreve uma proposta de novo horário (<c>METHOD=COUNTER</c>).
    /// </summary>
    /// <remarks>
    /// É a alternativa oferecida ao participante que tenta arrastar a reunião de outra
    /// pessoa — a operação que o <c>EventMoveEvaluator</c> recusa.
    /// </remarks>
    string WriteCounter(
        CalendarEventData calendarEvent, EmailAddress respondent, DateTimeOffset proposedStart);

    /// <summary>
    /// Expande as ocorrências de um evento recorrente dentro de uma janela.
    /// </summary>
    /// <remarks>
    /// Expandir <c>RRULE</c> à mão é a armadilha clássica deste recurso: a norma tem
    /// exceções, contagem, limite por data e ajuste de fuso a cada ocorrência. Fica com
    /// quem já implementa a norma.
    /// </remarks>
    IReadOnlyList<DateTimeOffset> ExpandOccurrences(
        string recurrenceRule, DateTimeOffset firstStart, DateTimeOffset from, DateTimeOffset until);
}
