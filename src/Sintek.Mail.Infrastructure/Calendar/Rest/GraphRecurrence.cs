using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sintek.Mail.Infrastructure.Calendar.Rest;

/// <summary>
/// Traduz o objeto de recorrência do Graph para <c>RRULE</c>.
/// </summary>
/// <remarks>
/// <para>
/// A tradução é <b>só de leitura</b>, e só das formas que o Graph e a RFC 5545 descrevem da
/// mesma maneira. O que não couber devolve <see langword="null"/>: um compromisso sem regra
/// de repetição aparece como encontro único, o que é visivelmente errado e corrigível. Uma
/// regra <i>parcialmente</i> traduzida é pior — o usuário confia num padrão de repetição que
/// não corresponde ao que está no servidor, e só descobre quando falta a uma reunião.
/// </para>
/// <para>
/// A tradução no sentido inverso — <c>RRULE</c> para o objeto do Graph — não existe de
/// propósito. Ela exigiria mapear exceções, contagem e limite por data, e um mapeamento
/// parcial gravaria no servidor uma série diferente da que o usuário vê.
/// </para>
/// </remarks>
internal static class GraphRecurrence
{
    /// <summary>Converte, ou devolve <see langword="null"/> quando não há tradução fiel.</summary>
    internal static string? ToRRule(JsonElement? recurrence)
    {
        if (recurrence?.Object("pattern") is not { } pattern)
        {
            return null;
        }

        var interval = pattern.TryGetProperty("interval", out var rawInterval)
            && rawInterval.ValueKind == JsonValueKind.Number
                ? rawInterval.GetInt32()
                : 1;

        var builder = new StringBuilder();

        switch (pattern.Text("type"))
        {
            case "daily":
                builder.Append("FREQ=DAILY");
                break;

            case "weekly":
                builder.Append("FREQ=WEEKLY");
                AppendDays(builder, pattern);
                break;

            case "absoluteMonthly":
                builder.Append("FREQ=MONTHLY");

                if (pattern.TryGetProperty("dayOfMonth", out var dayOfMonth)
                    && dayOfMonth.ValueKind == JsonValueKind.Number)
                {
                    builder.Append(CultureInfo.InvariantCulture, $";BYMONTHDAY={dayOfMonth.GetInt32()}");
                }

                break;

            case "absoluteYearly":
                builder.Append("FREQ=YEARLY");
                break;

            // relativeMonthly e relativeYearly ("a segunda terça-feira") exigem BYSETPOS
            // combinado com BYDAY, e o Graph descreve o índice por nome. A tradução existe,
            // mas é onde o erro silencioso mora — fica de fora até ter teste próprio.
            default:
                return null;
        }

        if (interval > 1)
        {
            builder.Append(CultureInfo.InvariantCulture, $";INTERVAL={interval}");
        }

        AppendRange(builder, recurrence.Value.Object("range"));

        return builder.ToString();
    }

    private static void AppendDays(StringBuilder builder, JsonElement pattern)
    {
        var days = pattern
            .Array("daysOfWeek")
            .Select(d => d.GetString())
            .Select(ToIcalDay)
            .Where(d => d is not null)
            .ToList();

        if (days.Count > 0)
        {
            builder.Append(";BYDAY=").AppendJoin(',', days);
        }
    }

    private static void AppendRange(StringBuilder builder, JsonElement? range)
    {
        if (range is not { } value)
        {
            return;
        }

        switch (value.Text("type"))
        {
            case "endDate" when value.Text("endDate") is { } endDate
                && DateOnly.TryParse(endDate, CultureInfo.InvariantCulture, out var parsed):
                // UNTIL em UTC, com o fim do dia: a norma trata o valor como inclusivo, e um
                // UNTIL à meia-noite descartaria a última ocorrência.
                builder.Append(CultureInfo.InvariantCulture, $";UNTIL={parsed:yyyyMMdd}T235959Z");
                break;

            case "numbered" when value.TryGetProperty("numberOfOccurrences", out var count)
                && count.ValueKind == JsonValueKind.Number && count.GetInt32() > 0:
                builder.Append(CultureInfo.InvariantCulture, $";COUNT={count.GetInt32()}");
                break;

            default:
                // "noEnd" é uma RRULE sem COUNT nem UNTIL — infinita, e é assim mesmo.
                break;
        }
    }

    private static string? ToIcalDay(string? graphDay) => graphDay switch
    {
        "sunday" => "SU",
        "monday" => "MO",
        "tuesday" => "TU",
        "wednesday" => "WE",
        "thursday" => "TH",
        "friday" => "FR",
        "saturday" => "SA",
        _ => null,
    };
}
