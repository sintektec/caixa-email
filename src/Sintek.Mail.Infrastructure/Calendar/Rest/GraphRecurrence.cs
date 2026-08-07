using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
/// A tradução no sentido inverso — <see cref="ToRecurrence"/> — obedece à mesma regra, com
/// um critério a mais: <b>só escreve o que ela própria consegue reler</b>. O conjunto que
/// <see cref="ToRecurrence"/> aceita é exatamente o que <see cref="ToRRule"/> produz, e é
/// isso que mantém a ida e a volta estáveis. Escrever um padrão que a leitura não entende
/// faria o compromisso subir como série e voltar como encontro único na sincronização
/// seguinte — a divergência apareceria sozinha, sem ninguém ter mexido em nada.
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

    /// <summary>
    /// Converte uma <c>RRULE</c> no objeto de recorrência do Graph, ou devolve
    /// <see langword="null"/> quando não há tradução fiel.
    /// </summary>
    /// <param name="rrule">A regra, sem o prefixo <c>RRULE:</c>.</param>
    /// <param name="startsAt">
    /// Início da primeira ocorrência. É obrigatório: o Graph exige <c>range.startDate</c>, e
    /// é dele que saem os componentes que a regra omite.
    /// </param>
    /// <remarks>
    /// <para>
    /// Completar o que a regra não diz <b>não é adivinhação</b>. A RFC 5545 §3.3.10 manda
    /// derivar as partes <c>BY*</c> ausentes do <c>DTSTART</c>: uma <c>FREQ=MONTHLY</c> sem
    /// <c>BYMONTHDAY</c> repete no dia do mês em que a série começou. O Graph, ao contrário,
    /// exige o componente escrito. Derivá-lo é dizer em outra sintaxe o que a norma já diz.
    /// </para>
    /// <para>
    /// Tudo que não estiver na tabela de conversão faz a função devolver <see langword="null"/>,
    /// inclusive parte <c>BY*</c> desconhecida — <c>BYSETPOS</c>, <c>BYWEEKNO</c>,
    /// <c>BYYEARDAY</c> — e <c>BYDAY</c> com ordinal (<c>2TU</c>, "a segunda terça"), que
    /// pediria os padrões <c>relative*</c> do Graph. Recusar é a decisão barata: o
    /// compromisso sobe como encontro único, que é visivelmente errado. O caro é o contrário —
    /// uma série que repete no dia errado parece certa até alguém faltar à reunião.
    /// </para>
    /// </remarks>
    internal static JsonObject? ToRecurrence(string? rrule, DateTimeOffset? startsAt)
    {
        if (string.IsNullOrWhiteSpace(rrule) || startsAt is not { } start)
        {
            return null;
        }

        if (Parse(rrule) is not { } parts)
        {
            return null;
        }

        var interval = 1;

        if (parts.TryGetValue("INTERVAL", out var rawInterval))
        {
            if (!int.TryParse(rawInterval, CultureInfo.InvariantCulture, out interval) || interval < 1)
            {
                return null;
            }
        }

        var pattern = new JsonObject { ["interval"] = interval };
        var startDate = DateOnly.FromDateTime(start.UtcDateTime);
        var frequency = parts.GetValueOrDefault("FREQ");

        // Cada frequência entende um conjunto próprio de partes `BY*`, e o que sobra recusa a
        // regra inteira. Sem esta verificação, `FREQ=MONTHLY;BYDAY=2TU` — "a segunda terça do
        // mês" — cairia no ramo mensal, que só olha `BYMONTHDAY`: o `BYDAY` seria descartado
        // em silêncio e a série subiria repetindo no dia do mês em que começou. Não é uma
        // tradução incompleta, é outra série, com aparência de correta.
        if (!Applicable.TryGetValue(frequency ?? string.Empty, out var applicable)
            || parts.Keys.Any(key => !Universal.Contains(key) && !applicable.Contains(key)))
        {
            return null;
        }

        switch (frequency)
        {
            case "DAILY":
                pattern["type"] = "daily";
                break;

            case "WEEKLY":
                pattern["type"] = "weekly";

                if (WeekDays(parts, startDate) is not { } weekDays)
                {
                    return null;
                }

                pattern["daysOfWeek"] = weekDays;

                // O padrão da norma é segunda-feira (WKST=MO); o do Graph é domingo. A
                // diferença não é cosmética: com INTERVAL maior que 1 ela desloca quais
                // semanas contam, então o valor vai sempre escrito.
                pattern["firstDayOfWeek"] = parts.GetValueOrDefault("WKST") switch
                {
                    null or "MO" => "monday",
                    "SU" => "sunday",
                    "TU" => "tuesday",
                    "WE" => "wednesday",
                    "TH" => "thursday",
                    "FR" => "friday",
                    "SA" => "saturday",
                    _ => null,
                };

                if (pattern["firstDayOfWeek"] is null)
                {
                    return null;
                }

                break;

            case "MONTHLY":
                pattern["type"] = "absoluteMonthly";

                if (MonthDay(parts, startDate) is not { } monthDay)
                {
                    return null;
                }

                pattern["dayOfMonth"] = monthDay;
                break;

            case "YEARLY":
                pattern["type"] = "absoluteYearly";

                if (MonthDay(parts, startDate) is not { } yearlyDay
                    || Month(parts, startDate) is not { } month)
                {
                    return null;
                }

                pattern["dayOfMonth"] = yearlyDay;
                pattern["month"] = month;
                break;

            default:
                return null;
        }

        if (Range(parts, startDate) is not { } range)
        {
            return null;
        }

        return new JsonObject { ["pattern"] = pattern, ["range"] = range };
    }

    /// <summary>
    /// Quebra a regra em partes, ou devolve <see langword="null"/> se encontrar alguma que
    /// esta tradução não sabe honrar.
    /// </summary>
    /// <remarks>
    /// Recusar o desconhecido em vez de ignorá-lo é o ponto: uma <c>BYSETPOS</c> descartada
    /// em silêncio não deixa a série incompleta, deixa-a <i>diferente</i> — e ela sobe ao
    /// servidor parecendo correta.
    /// </remarks>
    private static Dictionary<string, string>? Parse(string rrule)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var piece in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            var separator = piece.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                return null;
            }

            var key = piece[..separator].ToUpperInvariant();

            if (!Understood.Contains(key) || !parts.TryAdd(key, piece[(separator + 1)..].ToUpperInvariant()))
            {
                return null;
            }
        }

        return parts;
    }

    private static readonly HashSet<string> Understood = new(StringComparer.Ordinal)
    {
        "FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY", "BYMONTHDAY", "BYMONTH", "WKST",
    };

    /// <summary>Partes que valem para qualquer frequência.</summary>
    /// <remarks>
    /// <c>WKST</c> entra aqui porque, no conjunto que esta tradução aceita, ele só muda a
    /// expansão de <c>FREQ=WEEKLY</c> — e de <c>BYWEEKNO</c>, que é recusado de qualquer
    /// forma. Numa regra diária ou mensal é literalmente sem efeito, e recusá-la por isso
    /// rejeitaria regra legítima de outros clientes sem ganho nenhum.
    /// </remarks>
    private static readonly HashSet<string> Universal = new(StringComparer.Ordinal)
    {
        "FREQ", "INTERVAL", "COUNT", "UNTIL", "WKST",
    };

    /// <summary>Partes <c>BY*</c> que cada frequência entende.</summary>
    private static readonly Dictionary<string, HashSet<string>> Applicable = new(StringComparer.Ordinal)
    {
        ["DAILY"] = [],
        ["WEEKLY"] = ["BYDAY"],
        ["MONTHLY"] = ["BYMONTHDAY"],
        ["YEARLY"] = ["BYMONTH", "BYMONTHDAY"],
    };

    private static JsonArray? WeekDays(Dictionary<string, string> parts, DateOnly start)
    {
        if (!parts.TryGetValue("BYDAY", out var raw))
        {
            // Sem BYDAY, a norma repete no dia da semana do DTSTART.
            return new JsonArray(GraphDay(start.DayOfWeek));
        }

        var days = new JsonArray();

        foreach (var day in raw.Split(',', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            // Ordinal ("2TU", "-1FR") pede relativeMonthly/relativeYearly, que a leitura não
            // traduz. Escrever aqui criaria série que volta como encontro único.
            if (day.Length != 2 || ToGraphDay(day) is not { } converted)
            {
                return null;
            }

            days.Add(converted);
        }

        return days.Count > 0 ? days : null;
    }

    private static int? MonthDay(Dictionary<string, string> parts, DateOnly start)
    {
        if (!parts.TryGetValue("BYMONTHDAY", out var raw))
        {
            return start.Day;
        }

        // Um único dia, e positivo: BYMONTHDAY=-1 ("último dia do mês") não tem
        // representação em dayOfMonth, e escrever 1 no lugar inverteria a série.
        return int.TryParse(raw, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31
            ? day
            : null;
    }

    private static int? Month(Dictionary<string, string> parts, DateOnly start)
    {
        if (!parts.TryGetValue("BYMONTH", out var raw))
        {
            return start.Month;
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12
            ? month
            : null;
    }

    private static JsonObject? Range(Dictionary<string, string> parts, DateOnly start)
    {
        var range = new JsonObject
        {
            ["startDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["recurrenceTimeZone"] = "UTC",
        };

        var hasCount = parts.TryGetValue("COUNT", out var rawCount);
        var hasUntil = parts.TryGetValue("UNTIL", out var rawUntil);

        // A norma proíbe os dois na mesma regra, e o Graph não tem como expressar a
        // combinação: o range é de um tipo só.
        if (hasCount && hasUntil)
        {
            return null;
        }

        if (hasCount)
        {
            if (!int.TryParse(rawCount, CultureInfo.InvariantCulture, out var count) || count < 1)
            {
                return null;
            }

            range["type"] = "numbered";
            range["numberOfOccurrences"] = count;
            return range;
        }

        if (hasUntil)
        {
            // O valor vem como data ("20261231") ou como instante ("20261231T235959Z"). O
            // Graph guarda data, então só a parte de data importa.
            var datePart = rawUntil!.Split('T', 2)[0];

            if (!DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var until))
            {
                return null;
            }

            range["type"] = "endDate";
            range["endDate"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return range;
        }

        range["type"] = "noEnd";
        return range;
    }

    private static string GraphDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "sunday",
        DayOfWeek.Monday => "monday",
        DayOfWeek.Tuesday => "tuesday",
        DayOfWeek.Wednesday => "wednesday",
        DayOfWeek.Thursday => "thursday",
        DayOfWeek.Friday => "friday",
        _ => "saturday",
    };

    private static string? ToGraphDay(string icalDay) => icalDay switch
    {
        "SU" => "sunday",
        "MO" => "monday",
        "TU" => "tuesday",
        "WE" => "wednesday",
        "TH" => "thursday",
        "FR" => "friday",
        "SA" => "saturday",
        _ => null,
    };
}
