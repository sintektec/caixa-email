using AwesomeAssertions;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Entities;

/// <summary>
/// Cobre o compromisso da agenda, com atenção à regra que define a fase: sequência menor
/// nunca sobrescreve maior.
/// </summary>
public class CalendarEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid Conta = Guid.CreateVersion7();

    private static EmailAddress Endereco(string value) => EmailAddress.Parse(value);

    private static CalendarEvent Evento(int sequence = 0)
    {
        var evento = CalendarEvent.Create(
            Conta, "uid-1", "Revisão do contrato", Inicio, Inicio.AddHours(1), Now);

        if (sequence > 0)
        {
            Atualizar(evento, sequence, Inicio);
        }

        return evento;
    }

    private static bool Atualizar(CalendarEvent evento, int sequence, DateTimeOffset inicio)
        => evento.ApplyUpdate(
            sequence, "Revisão do contrato", null, null, null, inicio, inicio.AddHours(1),
            false, null, CalendarEventStatus.Confirmed, null, Now);

    [Fact]
    public void Create_FimAntesDoInicio_Recusa()
    {
        var criar = () => CalendarEvent.Create(
            Conta, "uid-1", "Reunião", Inicio, Inicio.AddHours(-1), Now);

        criar.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_SemUid_Recusa()
    {
        var criar = () => CalendarEvent.Create(Conta, "  ", "Reunião", Inicio, Inicio, Now);

        criar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplyUpdate_SequenciaMaior_Aplica()
    {
        var evento = Evento(sequence: 2);
        var novoInicio = Inicio.AddDays(1);

        var aplicou = Atualizar(evento, 3, novoInicio);

        aplicou.Should().BeTrue();
        evento.StartsAt.Should().Be(novoInicio);
        evento.Sequence.Should().Be(3);
    }

    [Fact]
    public void ApplyUpdate_MesmaSequencia_Aplica()
    {
        // Mesma versão reenviada é o caso do convite que chega duas vezes; aplicar de novo
        // é inofensivo e mantém o conteúdo mais completo dos dois.
        var evento = Evento(sequence: 2);

        Atualizar(evento, 2, Inicio.AddHours(3)).Should().BeTrue();
        evento.StartsAt.Should().Be(Inicio.AddHours(3));
    }

    [Fact]
    public void ApplyUpdate_SequenciaMenor_RecusaEPreservaOHorario()
    {
        // O convite atrasado mudaria a reunião de volta para o horário errado.
        var evento = Evento(sequence: 5);
        var horarioAtual = evento.StartsAt;

        var aplicou = Atualizar(evento, 4, Inicio.AddDays(-2));

        aplicou.Should().BeFalse();
        evento.StartsAt.Should().Be(horarioAtual);
        evento.Sequence.Should().Be(5);
    }

    [Fact]
    public void Cancel_SequenciaMenor_Recusa()
    {
        var evento = Evento(sequence: 5);

        evento.Cancel(3, Now).Should().BeFalse();
        evento.Status.Should().Be(CalendarEventStatus.Confirmed);
    }

    [Fact]
    public void Cancel_SequenciaMaior_MarcaSemApagar()
    {
        var evento = Evento(sequence: 2);

        evento.Cancel(3, Now).Should().BeTrue();
        evento.Status.Should().Be(CalendarEventStatus.Cancelled);
        evento.Summary.Should().NotBeEmpty("o compromisso é preservado, não apagado");
    }

    [Fact]
    public void MoveTo_PreservaADuracao()
    {
        var evento = CalendarEvent.Create(
            Conta, "uid-1", "Reunião", Inicio, Inicio.AddMinutes(90), Now);

        evento.MoveTo(Inicio.AddDays(2), Now, incrementSequence: false);

        (evento.EndsAt - evento.StartsAt).Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void MoveTo_ComoOrganizador_IncrementaASequencia()
    {
        var evento = Evento(sequence: 2);

        evento.MoveTo(Inicio.AddDays(1), Now, incrementSequence: true);

        evento.Sequence.Should().Be(3);
    }

    [Fact]
    public void MoveTo_CompromissoProprio_NaoMexeNaSequencia()
    {
        var evento = Evento(sequence: 2);

        evento.MoveTo(Inicio.AddDays(1), Now, incrementSequence: false);

        evento.Sequence.Should().Be(2, "mover a própria cópia não é nova versão de convite nenhum");
    }

    [Fact]
    public void SyncAttendees_ConviteReenviado_PreservaARespostaJaDada()
    {
        // O organizador reenvia com NEEDS-ACTION para todos a cada alteração; aceitar isso
        // cegamente apagaria o "aceito" que o usuário acabou de dar.
        var evento = Evento();
        evento.AddAttendee(Endereco("contato@sintek.com.br"), Now, response: AttendeeResponse.Accepted);

        evento.SyncAttendees(
            [new AttendeeSnapshot(
                Endereco("contato@sintek.com.br"), "Contato", AttendeeRole.Required,
                AttendeeResponse.NeedsAction)],
            Now);

        evento.AttendeeFor(Endereco("contato@sintek.com.br"))!
            .Response.Should().Be(AttendeeResponse.Accepted);
    }

    [Fact]
    public void SyncAttendees_RespostaExplicitaNoConvite_Prevalece()
    {
        var evento = Evento();
        evento.AddAttendee(Endereco("bruno@cliente.com.br"), Now, response: AttendeeResponse.NeedsAction);

        evento.SyncAttendees(
            [new AttendeeSnapshot(
                Endereco("bruno@cliente.com.br"), "Bruno", AttendeeRole.Required,
                AttendeeResponse.Declined)],
            Now);

        evento.AttendeeFor(Endereco("bruno@cliente.com.br"))!
            .Response.Should().Be(AttendeeResponse.Declined);
    }

    [Fact]
    public void SyncAttendees_ParticipanteRetirado_SaiDaLista()
    {
        var evento = Evento();
        evento.AddAttendee(Endereco("saiu@cliente.com.br"), Now);
        evento.AddAttendee(Endereco("ficou@cliente.com.br"), Now);

        evento.SyncAttendees(
            [new AttendeeSnapshot(
                Endereco("ficou@cliente.com.br"), null, AttendeeRole.Required,
                AttendeeResponse.NeedsAction)],
            Now);

        evento.Attendees.Should().ContainSingle()
            .Which.Address.Value.Should().Be("ficou@cliente.com.br");
    }

    [Fact]
    public void SyncAttendees_ListaVazia_NaoApagaOsParticipantes()
    {
        // Convite sem ATTENDEE é publicação, não uma reunião esvaziada.
        var evento = Evento();
        evento.AddAttendee(Endereco("contato@sintek.com.br"), Now);

        evento.SyncAttendees([], Now);

        evento.Attendees.Should().ContainSingle();
    }

    [Fact]
    public void SetAttendeeResponse_EnderecoDesconhecido_DevolveFalso()
    {
        var evento = Evento();

        evento.SetAttendeeResponse(Endereco("ninguem@cliente.com.br"), AttendeeResponse.Accepted, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void OtherAttendeeCount_SoOUsuario_DevolveZero()
    {
        var evento = Evento();
        evento.AddAttendee(Endereco("contato@sintek.com.br"), Now);

        evento.OtherAttendeeCount(Endereco("contato@sintek.com.br")).Should().Be(0);
    }

    [Fact]
    public void IsRecurring_ComRegra_DevolveVerdadeiro()
    {
        var evento = Evento();

        evento.SetRecurrence("FREQ=WEEKLY;BYDAY=MO", Now);

        evento.IsRecurring.Should().BeTrue();
    }
}
