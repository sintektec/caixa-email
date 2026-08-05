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
}
