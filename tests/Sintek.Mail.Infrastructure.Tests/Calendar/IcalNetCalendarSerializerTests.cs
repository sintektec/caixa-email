using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Calendar;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>
/// Cobre a leitura e a escrita de iCalendar contra os formatos que Teams, Outlook e Meet
/// realmente emitem — incluindo os dois jeitos de declarar fuso, que é onde a fase temia
/// esbarrar no <c>InvariantGlobalization</c>.
/// </summary>
public class IcalNetCalendarSerializerTests
{
    private static IcalNetCalendarSerializer Serializer()
        => new(NullLogger<IcalNetCalendarSerializer>.Instance);

    private const string ConviteTeams = """
        BEGIN:VCALENDAR
        PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN
        VERSION:2.0
        METHOD:REQUEST
        BEGIN:VTIMEZONE
        TZID:America/Sao_Paulo
        BEGIN:STANDARD
        DTSTART:16010101T000000
        TZOFFSETFROM:-0300
        TZOFFSETTO:-0300
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:040000008200E00074C5B7101A82E008
        SEQUENCE:3
        SUMMARY:Revisão do contrato
        DESCRIPTION:Pauta: escopo e prazo.
        LOCATION:Microsoft Teams Meeting
        X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/abc
        DTSTART;TZID=America/Sao_Paulo:20260810T140000
        DTEND;TZID=America/Sao_Paulo:20260810T150000
        DTSTAMP:20260805T120000Z
        ORGANIZER;CN=Ana Souza:mailto:ana@cliente.com.br
        ATTENDEE;CN=Contato;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:contato@sintek.com.br
        ATTENDEE;CN=Bruno;ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:bruno@cliente.com.br
        RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=4
        STATUS:CONFIRMED
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Read_ConviteDoTeams_ExtraiOsCamposEOsParticipantes()
    {
        var documento = Serializer().Read(ConviteTeams);

        documento.Should().NotBeNull();
        documento!.Value.Method.Should().Be(CalendarMethod.Request);

        var evento = documento.Value.Events.Should().ContainSingle().Subject;
        evento.Uid.Should().Be("040000008200E00074C5B7101A82E008");
        evento.Sequence.Should().Be(3);
        evento.Summary.Should().Be("Revisão do contrato");
        evento.Location.Should().Be("Microsoft Teams Meeting");
        evento.OrganizerAddress!.Value.Should().Be("ana@cliente.com.br");
        evento.OrganizerDisplayName.Should().Be("Ana Souza");
        evento.Attendees.Should().HaveCount(2);
        evento.RecurrenceRule.Should().Contain("WEEKLY");
    }

    [Fact]
    public void Read_FusoIana_ResolveOInstanteMesmoComGlobalizacaoInvariante()
    {
        // 14h em America/Sao_Paulo é 17h UTC. Se a base de fusos não estivesse disponível,
        // este é o teste que quebraria.
        var evento = Serializer().Read(ConviteTeams)!.Value.Events[0];

        evento.StartsAt.Should().Be(new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Read_FusoNoFormatoWindows_ResolvePeloVTimezoneEmbutido()
    {
        // O Outlook emite nomes do Windows, que não existem na base IANA. A norma manda o
        // convite embutir as regras de offset justamente para isso.
        var documento = Serializer().Read("""
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REQUEST
            BEGIN:VTIMEZONE
            TZID:E. South America Standard Time
            BEGIN:STANDARD
            DTSTART:16010101T000000
            TZOFFSETFROM:-0300
            TZOFFSETTO:-0300
            END:STANDARD
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:win-1
            SUMMARY:Reunião
            DTSTART;TZID=E. South America Standard Time:20260810T140000
            DTEND;TZID=E. South America Standard Time:20260810T150000
            DTSTAMP:20260805T120000Z
            END:VEVENT
            END:VCALENDAR
            """);

        documento!.Value.Events[0].StartsAt
            .Should().Be(new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Read_DiaInteiro_MarcaComoTal()
    {
        var documento = Serializer().Read("""
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:PUBLISH
            BEGIN:VEVENT
            UID:allday-1
            SUMMARY:Feriado
            DTSTART;VALUE=DATE:20260907
            DTEND;VALUE=DATE:20260908
            DTSTAMP:20260805T120000Z
            END:VEVENT
            END:VCALENDAR
            """);

        documento!.Value.Events[0].IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Read_Cancelamento_ReconheceOMetodoEOStatus()
    {
        var documento = Serializer().Read("""
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:CANCEL
            BEGIN:VEVENT
            UID:utc-1
            SEQUENCE:2
            STATUS:CANCELLED
            DTSTART:20260810T170000Z
            DTSTAMP:20260805T130000Z
            END:VEVENT
            END:VCALENDAR
            """);

        documento!.Value.Method.Should().Be(CalendarMethod.Cancel);
        documento.Value.Events[0].Status.Should().Be(CalendarEventStatus.Cancelled);
        documento.Value.Events[0].Sequence.Should().Be(2);
    }

    [Fact]
    public void Read_Resposta_TrazSoOParticipanteQueRespondeu()
    {
        var documento = Serializer().Read("""
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REPLY
            BEGIN:VEVENT
            UID:utc-1
            DTSTAMP:20260805T140000Z
            ORGANIZER:mailto:contato@sintek.com.br
            ATTENDEE;PARTSTAT=DECLINED:mailto:bruno@cliente.com.br
            END:VEVENT
            END:VCALENDAR
            """);

        documento!.Value.Method.Should().Be(CalendarMethod.Reply);

        var evento = documento.Value.Events[0];
        evento.StartsAt.Should().BeNull("uma resposta não carrega horário");
        evento.Attendees.Should().ContainSingle()
            .Which.Response.Should().Be(AttendeeResponse.Declined);
    }

    [Fact]
    public void Read_ConteudoMalformado_DevolveNuloSemLancar()
    {
        // O documento vem de um anexo escolhido por quem enviou a mensagem; uma exceção
        // aqui derrubaria a sincronização da conta inteira.
        var ler = () => Serializer().Read("isto nao e um ics");

        ler.Should().NotThrow();
        Serializer().Read("isto nao e um ics").Should().BeNull();
    }

    [Fact]
    public void Read_ConteudoVazio_DevolveNulo()
        => Serializer().Read(string.Empty).Should().BeNull();

    [Fact]
    public void Read_EventoSemUid_RecebeUmGeradoPelaBiblioteca()
    {
        // Comportamento medido, e não desejado: a biblioteca inventa um UID quando o
        // documento não traz um. Como o UID inventado muda a cada leitura, ele não serve de
        // identidade — é por isso que a importação tem uma segunda via, pela mensagem em
        // que o convite chegou. Sem ela, rebaixar o corpo criaria um compromisso novo.
        const string SemUid = """
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REQUEST
            BEGIN:VEVENT
            SUMMARY:Sem identidade
            DTSTART:20260810T170000Z
            DTSTAMP:20260805T120000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var primeira = Serializer().Read(SemUid)!.Value.Events[0];
        var segunda = Serializer().Read(SemUid)!.Value.Events[0];

        primeira.Uid.Should().NotBeEmpty();
        segunda.Uid.Should().NotBe(primeira.Uid);
    }

    [Fact]
    public void Read_LinkDoTeamsNaPropriedadePropria_EExtraido()
        => Serializer().Read(ConviteTeams)!.Value.Events[0]
            .MeetingUrl.Should().Be("https://teams.microsoft.com/l/meetup-join/abc");

    [Fact]
    public void Read_LinkDoMeetNoLocal_EExtraidoDoTexto()
    {
        var documento = Serializer().Read("""
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REQUEST
            BEGIN:VEVENT
            UID:meet-1
            SUMMARY:Alinhamento
            LOCATION:https://meet.google.com/abc-defg-hij
            DTSTART:20260810T170000Z
            DTSTAMP:20260805T120000Z
            END:VEVENT
            END:VCALENDAR
            """);

        documento!.Value.Events[0].MeetingUrl
            .Should().Be("https://meet.google.com/abc-defg-hij");
    }

    [Fact]
    public void WriteReply_LevaSoQuemRespondeu()
    {
        var evento = Serializer().Read(ConviteTeams)!.Value.Events[0];

        var texto = Serializer().WriteReply(
            evento, EmailAddress.Parse("contato@sintek.com.br"), AttendeeResponse.Accepted);

        texto.Should().Contain("METHOD:REPLY");
        texto.Should().Contain("PARTSTAT=ACCEPTED");
        texto.Should().Contain("contato@sintek.com.br");
        texto.Should().NotContain(
            "bruno@cliente.com.br",
            "quem mantém o estado dos outros participantes é o organizador");
    }

    [Fact]
    public void WriteReply_PreservaOUidEASequencia()
    {
        var evento = Serializer().Read(ConviteTeams)!.Value.Events[0];

        var relido = Serializer().Read(Serializer().WriteReply(
            evento, EmailAddress.Parse("contato@sintek.com.br"), AttendeeResponse.Declined))!.Value;

        relido.Events[0].Uid.Should().Be(evento.Uid);
        relido.Events[0].Sequence.Should().Be(evento.Sequence);
    }

    [Fact]
    public void WriteCancel_MarcaOStatusEOMetodo()
    {
        var evento = Serializer().Read(ConviteTeams)!.Value.Events[0];

        var texto = Serializer().WriteCancel(evento);

        texto.Should().Contain("METHOD:CANCEL");
        texto.Should().Contain("STATUS:CANCELLED");
    }

    [Fact]
    public void WriteCounter_PropoeOutroHorarioPreservandoADuracao()
    {
        var evento = Serializer().Read(ConviteTeams)!.Value.Events[0];
        var proposto = new DateTimeOffset(2026, 8, 12, 19, 0, 0, TimeSpan.Zero);

        var relido = Serializer().Read(Serializer().WriteCounter(
            evento, EmailAddress.Parse("contato@sintek.com.br"), proposto))!.Value;

        relido.Method.Should().Be(CalendarMethod.Counter);
        relido.Events[0].StartsAt.Should().Be(proposto);
        (relido.Events[0].EndsAt - relido.Events[0].StartsAt).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void WriteRequest_DepoisDeLer_PreservaOsCamposEssenciais()
    {
        var original = Serializer().Read(ConviteTeams)!.Value.Events[0];

        var relido = Serializer().Read(Serializer().WriteRequest(original))!.Value.Events[0];

        relido.Uid.Should().Be(original.Uid);
        relido.Summary.Should().Be(original.Summary);
        relido.StartsAt.Should().Be(original.StartsAt);
        relido.EndsAt.Should().Be(original.EndsAt);
        relido.Attendees.Should().HaveCount(original.Attendees.Count);
    }

    [Fact]
    public void ExpandOccurrences_RegraSemanalComContagem_DevolveAsQuatro()
    {
        var inicio = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

        var ocorrencias = Serializer().ExpandOccurrences(
            "FREQ=WEEKLY;BYDAY=MO;COUNT=4", inicio, inicio.AddDays(-1), inicio.AddMonths(3));

        ocorrencias.Should().HaveCount(4);
        ocorrencias[1].Should().Be(inicio.AddDays(7));
    }

    [Fact]
    public void ExpandOccurrences_RegraSemFim_ParaNoLimiteDaJanela()
    {
        // Uma RRULE sem COUNT nem UNTIL não termina; sem o corte a expansão rodaria para
        // sempre.
        var inicio = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

        var ocorrencias = Serializer().ExpandOccurrences(
            "FREQ=DAILY", inicio, inicio, inicio.AddDays(5));

        ocorrencias.Should().HaveCount(5);
    }

    [Fact]
    public void ExpandOccurrences_RegraInvalida_DevolveVazioSemLancar()
    {
        var inicio = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

        Serializer().ExpandOccurrences("isto nao e uma rrule", inicio, inicio, inicio.AddDays(5))
            .Should().BeEmpty();
    }

    [Fact]
    public void ExpandOccurrences_JanelaInvertida_DevolveVazio()
    {
        var inicio = new DateTimeOffset(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

        Serializer().ExpandOccurrences("FREQ=DAILY", inicio, inicio.AddDays(5), inicio)
            .Should().BeEmpty();
    }
}
