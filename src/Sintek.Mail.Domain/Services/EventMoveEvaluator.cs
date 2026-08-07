using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>O que acontece ao arrastar um compromisso para outra data.</summary>
public enum EventMoveOutcome
{
    /// <summary>Move e avisa todo mundo: o usuário organiza o evento.</summary>
    MoveAndNotify = 0,

    /// <summary>Move só localmente: o compromisso não tem outros participantes.</summary>
    MoveLocally = 1,

    /// <summary>
    /// Recusado: mover a própria cópia de uma reunião de outra pessoa dessincronizaria o
    /// usuário em silêncio. A alternativa é propor novo horário.
    /// </summary>
    ProposeNewTimeInstead = 2,

    /// <summary>Recusado: o evento está cancelado.</summary>
    RefusedCancelled = 3,
}

/// <summary>Veredito da avaliação, com o motivo exibível.</summary>
/// <param name="Outcome">O que fazer.</param>
/// <param name="Reason">Texto para o usuário. Vazio quando o movimento é permitido.</param>
public readonly record struct EventMoveDecision(EventMoveOutcome Outcome, string Reason)
{
    /// <summary>Se o compromisso pode ser movido.</summary>
    public bool IsAllowed
        => Outcome is EventMoveOutcome.MoveAndNotify or EventMoveOutcome.MoveLocally;

    /// <summary>Se a movimentação precisa reenviar o convite aos participantes.</summary>
    public bool RequiresNotification => Outcome == EventMoveOutcome.MoveAndNotify;
}

/// <summary>
/// Decide o que fazer quando o usuário arrasta um compromisso para outra data.
/// </summary>
/// <remarks>
/// <para>
/// Puro e sem dependência: recebe o evento e o endereço de quem está mexendo, e devolve a
/// decisão. É a única regra desta fase que o usuário percebe como comportamento, e por isso
/// precisa ser verificável sem banco nem interface.
/// </para>
/// <para>
/// <b>Onde este produto diverge do Outlook, de propósito:</b> o Outlook deixa o participante
/// arrastar a própria cópia de uma reunião alheia. O resultado é que a pessoa passa a
/// aparecer livre no horário em que todos combinaram, sem que ninguém saiba — inclusive ela,
/// que vê a reunião no horário novo e confia nele. Aqui a operação é recusada com
/// explicação, e a alternativa oferecida é propor novo horário ao organizador, que é o que
/// resolve o problema de verdade.
/// </para>
/// </remarks>
public static class EventMoveEvaluator
{
    /// <summary>Avalia a movimentação.</summary>
    /// <param name="calendarEvent">Evento arrastado.</param>
    /// <param name="movedBy">Endereço da conta que está movendo.</param>
    public static EventMoveDecision Evaluate(CalendarEvent calendarEvent, EmailAddress movedBy)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(movedBy);

        if (calendarEvent.Status == CalendarEventStatus.Cancelled)
        {
            return new EventMoveDecision(
                EventMoveOutcome.RefusedCancelled,
                "Este compromisso foi cancelado e não pode ser remarcado.");
        }

        // Sem organizador declarado o compromisso é próprio: foi criado aqui, não veio de
        // convite de ninguém.
        var isOrganizer = calendarEvent.OrganizerAddress is null
            || calendarEvent.IsOrganizedBy(movedBy);

        if (isOrganizer)
        {
            return calendarEvent.OtherAttendeeCount(movedBy) > 0
                ? new EventMoveDecision(EventMoveOutcome.MoveAndNotify, string.Empty)
                : new EventMoveDecision(EventMoveOutcome.MoveLocally, string.Empty);
        }

        if (calendarEvent.OtherAttendeeCount(movedBy) > 0)
        {
            return new EventMoveDecision(
                EventMoveOutcome.ProposeNewTimeInstead,
                "Esta reunião é de outra pessoa. Mover apenas a sua cópia deixaria você "
                + "fora do horário combinado sem que ninguém soubesse. Proponha um novo "
                + "horário ao organizador.");
        }

        // Convite só para o usuário: mover a própria cópia não desencontra ninguém, mas o
        // organizador continua com o horário antigo — e é ele quem manda.
        return new EventMoveDecision(
            EventMoveOutcome.ProposeNewTimeInstead,
            "Este compromisso foi marcado por outra pessoa. Proponha um novo horário ao "
            + "organizador para que os dois fiquem com a mesma data.");
    }
}
