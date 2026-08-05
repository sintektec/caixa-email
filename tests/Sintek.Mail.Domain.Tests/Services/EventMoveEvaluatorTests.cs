using AwesomeAssertions;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre a decisão de arrastar um compromisso na grade — a única regra desta fase que o
/// usuário percebe como comportamento, e onde o produto diverge do Outlook de propósito.
/// </summary>
public class EventMoveEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid Conta = Guid.CreateVersion7();

    private static readonly EmailAddress Eu = EmailAddress.Parse("contato@sintek.com.br");
    private static readonly EmailAddress Outro = EmailAddress.Parse("ana@cliente.com.br");

    private static CalendarEvent Evento(
        EmailAddress? organizador, params EmailAddress[] participantes)
    {
        var evento = CalendarEvent.Create(
            Conta, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);

        if (organizador is not null)
        {
            evento.SetOrganizer(organizador, null, Now);
        }

        foreach (var participante in participantes)
        {
            evento.AddAttendee(participante, Now);
        }

        return evento;
    }

    [Fact]
    public void Evaluate_CompromissoProprioSemParticipantes_MoveLocalmente()
    {
        var decisao = EventMoveEvaluator.Evaluate(Evento(organizador: null), Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.MoveLocally);
        decisao.IsAllowed.Should().BeTrue();
        decisao.RequiresNotification.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OrganizadorComParticipantes_MoveEAvisa()
    {
        var decisao = EventMoveEvaluator.Evaluate(Evento(Eu, Outro), Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.MoveAndNotify);
        decisao.RequiresNotification.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_OrganizadorSemOutrosParticipantes_MoveSemAvisar()
    {
        var decisao = EventMoveEvaluator.Evaluate(Evento(Eu, Eu), Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.MoveLocally);
        decisao.RequiresNotification.Should().BeFalse("não há a quem avisar");
    }

    [Fact]
    public void Evaluate_ParticipanteEmReuniaoDeOutro_RecusaEOfereceProporHorario()
    {
        // Onde o produto diverge do Outlook: mover só a própria cópia deixaria o usuário
        // fora do horário combinado sem que ninguém soubesse.
        var decisao = EventMoveEvaluator.Evaluate(Evento(Outro, Eu, Outro), Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.ProposeNewTimeInstead);
        decisao.IsAllowed.Should().BeFalse();
        decisao.Reason.Should().Contain("Proponha");
    }

    [Fact]
    public void Evaluate_ConviteSoParaOUsuario_TambemPedeParaProporHorario()
    {
        // Ninguém mais se desencontra, mas o organizador continua com o horário antigo — e
        // é ele quem manda.
        var decisao = EventMoveEvaluator.Evaluate(Evento(Outro, Eu), Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.ProposeNewTimeInstead);
        decisao.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_EventoCancelado_Recusa()
    {
        var evento = Evento(Eu, Outro);
        evento.Cancel(1, Now);

        var decisao = EventMoveEvaluator.Evaluate(evento, Eu);

        decisao.Outcome.Should().Be(EventMoveOutcome.RefusedCancelled);
        decisao.IsAllowed.Should().BeFalse();
        decisao.Reason.Should().Contain("cancelado");
    }

    [Fact]
    public void Evaluate_CanceladoEOrganizado_ContinuaRecusando()
    {
        // O cancelamento vence o papel: remarcar uma reunião cancelada é criar outra.
        var evento = Evento(Eu);
        evento.Cancel(1, Now);

        EventMoveEvaluator.Evaluate(evento, Eu)
            .Outcome.Should().Be(EventMoveOutcome.RefusedCancelled);
    }

    [Fact]
    public void Evaluate_DecisaoPermitida_NaoTrazMotivo()
    {
        EventMoveEvaluator.Evaluate(Evento(Eu, Outro), Eu)
            .Reason.Should().BeEmpty("motivo só existe quando há o que explicar");
    }
}
