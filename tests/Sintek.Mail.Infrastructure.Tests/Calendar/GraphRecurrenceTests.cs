using System.Text.Json.Nodes;
using System.Text.Json;
using AwesomeAssertions;
using Sintek.Mail.Infrastructure.Calendar.Rest;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>
/// Cobre a tradução da recorrência do Graph para <c>RRULE</c>.
/// </summary>
/// <remarks>
/// O caso que mais importa aqui é o da <b>recusa</b>: um padrão que este código não sabe
/// traduzir devolve nada, e o compromisso aparece como encontro único — visivelmente errado
/// e corrigível. Uma regra parcialmente traduzida é pior: o usuário confia num padrão de
/// repetição que não corresponde ao servidor, e só descobre quando falta a uma reunião.
/// </remarks>
public class GraphRecurrenceTests
{
    private static string? Traduzir(string json)
        => GraphRecurrence.ToRRule(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ToRRule_Diaria_ComIntervalo()
        => Traduzir("""
            { "pattern": { "type": "daily", "interval": 3 }, "range": { "type": "noEnd" } }
            """)
            .Should().Be("FREQ=DAILY;INTERVAL=3");

    [Fact]
    public void ToRRule_Semanal_ComDiasDaSemana()
        => Traduzir("""
            {
              "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["tuesday", "thursday"] },
              "range": { "type": "noEnd" }
            }
            """)
            .Should().Be("FREQ=WEEKLY;BYDAY=TU,TH");

    [Fact]
    public void ToRRule_MensalPorDiaDoMes_UsaByMonthDay()
        => Traduzir("""
            {
              "pattern": { "type": "absoluteMonthly", "interval": 1, "dayOfMonth": 15 },
              "range": { "type": "noEnd" }
            }
            """)
            .Should().Be("FREQ=MONTHLY;BYMONTHDAY=15");

    [Fact]
    public void ToRRule_ComContagem_UsaCount()
        => Traduzir("""
            {
              "pattern": { "type": "daily", "interval": 1 },
              "range": { "type": "numbered", "numberOfOccurrences": 5 }
            }
            """)
            .Should().Be("FREQ=DAILY;COUNT=5");

    /// <summary>
    /// A norma trata o <c>UNTIL</c> como inclusivo, e um valor à meia-noite descartaria a
    /// última ocorrência.
    /// </summary>
    [Fact]
    public void ToRRule_ComDataFinal_UsaOFimDoDia()
        => Traduzir("""
            {
              "pattern": { "type": "daily", "interval": 1 },
              "range": { "type": "endDate", "endDate": "2026-12-31" }
            }
            """)
            .Should().Be("FREQ=DAILY;UNTIL=20261231T235959Z");

    /// <summary>
    /// "A segunda terça-feira do mês" exige <c>BYSETPOS</c> combinado com <c>BYDAY</c>, e o
    /// Graph descreve o índice por nome. A tradução existe, mas é onde o erro silencioso
    /// mora — fica de fora até ter teste próprio.
    /// </summary>
    [Fact]
    public void ToRRule_PadraoRelativo_NaoETraduzido()
        => Traduzir("""
            {
              "pattern": {
                "type": "relativeMonthly", "interval": 1,
                "index": "second", "daysOfWeek": ["tuesday"]
              },
              "range": { "type": "noEnd" }
            }
            """)
            .Should().BeNull();

    [Fact]
    public void ToRRule_SemPadrao_NaoETraduzido()
        => Traduzir("""{ "range": { "type": "noEnd" } }""").Should().BeNull();

    // ----------------------------------------------------------------------------------
    // Sentido inverso: RRULE -> objeto do Graph.
    // ----------------------------------------------------------------------------------

    /// <summary>Uma terça-feira, para que o dia derivado do início seja verificável.</summary>
    private static readonly DateTimeOffset Inicio = new(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);

    private static JsonNode? Escrever(string? rrule, DateTimeOffset? inicio = null)
        => GraphRecurrence.ToRecurrence(rrule, inicio ?? Inicio);

    private static string? Texto(JsonNode? no, string caminho)
    {
        var atual = no;

        foreach (var passo in caminho.Split('.'))
        {
            atual = atual?[passo];
        }

        return atual?.ToJsonString().Trim('"');
    }

    [Fact]
    public void ToRecurrence_Diaria_ComIntervalo()
    {
        var recorrencia = Escrever("FREQ=DAILY;INTERVAL=3");

        Texto(recorrencia, "pattern.type").Should().Be("daily");
        Texto(recorrencia, "pattern.interval").Should().Be("3");
        Texto(recorrencia, "range.type").Should().Be("noEnd");
        Texto(recorrencia, "range.startDate").Should().Be("2026-03-10");
    }

    [Fact]
    public void ToRecurrence_Semanal_LevaOsDiasDeclarados()
    {
        var recorrencia = Escrever("FREQ=WEEKLY;BYDAY=TU,TH");

        Texto(recorrencia, "pattern.type").Should().Be("weekly");
        DiasDaSemana(recorrencia).Should().Be("""["tuesday","thursday"]""");
    }

    /// <summary>
    /// A RFC 5545 §3.3.10 manda derivar do <c>DTSTART</c> a parte <c>BY*</c> ausente. O Graph
    /// exige o componente escrito, então derivá-lo é dizer a mesma coisa em outra sintaxe —
    /// não é escolher por conta própria.
    /// </summary>
    [Fact]
    public void ToRecurrence_SemanalSemByDay_DerivaODiaDoInicio()
        => DiasDaSemana(Escrever("FREQ=WEEKLY")).Should().Be("""["tuesday"]""");

    private static string DiasDaSemana(JsonNode? no)
        => no!["pattern"]!["daysOfWeek"]!.ToJsonString();

    [Fact]
    public void ToRecurrence_MensalSemByMonthDay_DerivaODiaDoInicio()
        => Texto(Escrever("FREQ=MONTHLY"), "pattern.dayOfMonth").Should().Be("10");

    [Fact]
    public void ToRecurrence_Anual_LevaMesEDiaDoInicio()
    {
        var recorrencia = Escrever("FREQ=YEARLY");

        Texto(recorrencia, "pattern.type").Should().Be("absoluteYearly");
        Texto(recorrencia, "pattern.month").Should().Be("3");
        Texto(recorrencia, "pattern.dayOfMonth").Should().Be("10");
    }

    [Fact]
    public void ToRecurrence_ComContagem_VaiComoNumbered()
    {
        var recorrencia = Escrever("FREQ=WEEKLY;BYDAY=MO;COUNT=10");

        Texto(recorrencia, "range.type").Should().Be("numbered");
        Texto(recorrencia, "range.numberOfOccurrences").Should().Be("10");
    }

    [Fact]
    public void ToRecurrence_ComLimiteDeData_VaiComoEndDate()
    {
        var recorrencia = Escrever("FREQ=DAILY;UNTIL=20261231T235959Z");

        Texto(recorrencia, "range.type").Should().Be("endDate");
        Texto(recorrencia, "range.endDate").Should().Be("2026-12-31");
    }

    /// <summary>
    /// O padrão da norma é segunda-feira e o do Graph é domingo. Com <c>INTERVAL</c> maior
    /// que 1 a diferença desloca quais semanas contam, então o valor vai sempre escrito.
    /// </summary>
    [Fact]
    public void ToRecurrence_Semanal_EscreveOPrimeiroDiaDaSemana()
        => Texto(Escrever("FREQ=WEEKLY;BYDAY=MO;INTERVAL=2"), "pattern.firstDayOfWeek")
            .Should().Be("monday");

    /// <summary>
    /// "A segunda terça-feira" pediria os padrões <c>relative*</c>, que a leitura não
    /// traduz. Escrever aqui criaria série que volta como encontro único na sincronização
    /// seguinte — divergência que aparece sozinha, sem ninguém ter mexido em nada.
    /// </summary>
    /// <remarks>
    /// Este teste pegou o defeito de verdade: o ramo mensal só olhava <c>BYMONTHDAY</c>, e o
    /// <c>BYDAY</c> era descartado em silêncio — a regra virava "dia 10 de todo mês", que não
    /// é uma tradução incompleta, é outra série.
    /// </remarks>
    [Fact]
    public void ToRecurrence_ByDayComOrdinal_NaoETraduzido()
        => Escrever("FREQ=MONTHLY;BYDAY=2TU").Should().BeNull();

    /// <summary>
    /// Parte que não vale para a frequência recusa a regra inteira, mesmo quando esta
    /// tradução saberia lê-la em outro contexto.
    /// </summary>
    [Theory]
    [InlineData("FREQ=MONTHLY;BYDAY=TU")]
    [InlineData("FREQ=DAILY;BYDAY=MO")]
    [InlineData("FREQ=DAILY;BYMONTHDAY=15")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO;BYMONTHDAY=15")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15;BYMONTH=3")]
    public void ToRecurrence_ParteForaDoContextoDaFrequencia_NaoETraduzida(string rrule)
        => Escrever(rrule).Should().BeNull();

    /// <summary>
    /// "Último dia do mês" não tem representação em <c>dayOfMonth</c>, e escrever 1 no lugar
    /// inverteria a série — do fim do mês para o começo.
    /// </summary>
    [Fact]
    public void ToRecurrence_UltimoDiaDoMes_NaoETraduzido()
        => Escrever("FREQ=MONTHLY;BYMONTHDAY=-1").Should().BeNull();

    /// <summary>
    /// Parte desconhecida recusa a regra inteira. Descartá-la em silêncio não deixaria a
    /// série incompleta, e sim <i>diferente</i> — subindo ao servidor com aparência correta.
    /// </summary>
    [Theory]
    [InlineData("FREQ=MONTHLY;BYSETPOS=2;BYDAY=TU")]
    [InlineData("FREQ=YEARLY;BYWEEKNO=13")]
    [InlineData("FREQ=DAILY;BYHOUR=9")]
    [InlineData("FREQ=YEARLY;BYYEARDAY=100")]
    public void ToRecurrence_ParteNaoSuportada_NaoETraduzida(string rrule)
        => Escrever(rrule).Should().BeNull();

    [Theory]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=SECONDLY")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    [InlineData("FREQ=DAILY;UNTIL=amanha")]
    [InlineData("lixo")]
    public void ToRecurrence_RegraInvalidaOuFrequenciaSemEquivalente_NaoETraduzida(string rrule)
        => Escrever(rrule).Should().BeNull();

    /// <summary>
    /// A norma proíbe <c>COUNT</c> e <c>UNTIL</c> juntos, e o Graph não teria como expressar
    /// a combinação: o <c>range</c> é de um tipo só.
    /// </summary>
    [Fact]
    public void ToRecurrence_ContagemELimiteJuntos_NaoETraduzida()
        => Escrever("FREQ=DAILY;COUNT=5;UNTIL=20261231").Should().BeNull();

    /// <summary>
    /// Sem início não há <c>range.startDate</c>, que o Graph exige — e é dele que saem os
    /// componentes que a regra omite.
    /// </summary>
    [Fact]
    public void ToRecurrence_SemInicio_NaoETraduzida()
        => GraphRecurrence.ToRecurrence("FREQ=DAILY", null).Should().BeNull();

    [Fact]
    public void ToRecurrence_SemRegra_NaoETraduzida()
        => Escrever(null).Should().BeNull();

    /// <summary>
    /// O contrato que mantém as duas direções coerentes: o que a escrita produz, a leitura
    /// entende, e o resultado é a mesma regra. Sem isto, um compromisso subiria como série e
    /// voltaria diferente na sincronização seguinte.
    /// </summary>
    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=DAILY;INTERVAL=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU,TH")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO;INTERVAL=2;COUNT=10")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("FREQ=DAILY;UNTIL=20261231T235959Z")]
    public void ToRecurrence_SeguidoDeToRRule_DevolveARegraOriginal(string rrule)
    {
        var recorrencia = Escrever(rrule);

        recorrencia.Should().NotBeNull(
            "toda regra desta lista precisa ser traduzível — é o conjunto que as duas direções compartilham");

        GraphRecurrence.ToRRule(JsonDocument.Parse(recorrencia!.ToJsonString()).RootElement)
            .Should().Be(rrule);
    }
}
