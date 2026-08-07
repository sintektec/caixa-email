using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sintek.Mail.Infrastructure.Calendar.Rest;

/// <summary>
/// O que o provedor do Graph guarda entre duas passadas.
/// </summary>
/// <param name="Since">
/// Instante da alteração mais recente já vista. É o que vai no <c>$filter</c> da passada
/// seguinte.
/// </param>
/// <param name="LastFullPassAt">Quando a coleção foi enumerada por inteiro pela última vez.</param>
/// <remarks>
/// <para>
/// <b>O token é opaco para o motor, e é o provedor quem escolhe o que cabe nele.</b> No
/// CalDAV é a URI que o servidor emitiu; na Google, o <c>syncToken</c> dela. Aqui não há
/// token de servidor nenhum — o Graph não oferece um que preserve o mestre de série —, então
/// o campo guarda o estado que este provedor precisa para decidir a próxima passada.
/// </para>
/// <para>
/// Os dois campos existem por motivos diferentes. <see cref="Since"/> é o que torna a passada
/// incremental. <see cref="LastFullPassAt"/> é o que garante que a exclusão feita no servidor
/// seja notada: a consulta por <c>$filter</c> não reporta o que sumiu, e sem uma passada
/// completa periódica o compromisso apagado lá ficaria aqui para sempre.
/// </para>
/// </remarks>
internal readonly record struct GraphSyncToken(DateTimeOffset? Since, DateTimeOffset? LastFullPassAt)
{
    /// <summary>Se esta passada precisa enumerar a coleção inteira.</summary>
    /// <remarks>
    /// A primeira passada sempre é completa: sem marca-d'água não há o que filtrar, e é a
    /// oportunidade de estabelecer a base contra a qual as ausências passam a significar
    /// exclusão.
    /// </remarks>
    internal bool NeedsFullPass(DateTimeOffset now, TimeSpan interval)
        => Since is null || LastFullPassAt is not { } last || now - last >= interval;

    /// <summary>Lê o token guardado, tolerando qualquer formato anterior.</summary>
    /// <remarks>
    /// Token ilegível devolve o vazio, e o vazio força uma passada completa. É o lado certo
    /// do erro: uma passada completa a mais custa tráfego, e uma incremental sobre marca
    /// inventada perderia alterações em silêncio.
    /// </remarks>
    internal static GraphSyncToken Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            var node = JsonNode.Parse(raw);

            return new GraphSyncToken(
                ReadStamp(node?["since"]?.GetValue<string>()),
                ReadStamp(node?["fullPass"]?.GetValue<string>()));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var node = new JsonObject();

        if (Since is { } since)
        {
            node["since"] = Write(since);
        }

        if (LastFullPassAt is { } full)
        {
            node["fullPass"] = Write(full);
        }

        return node.ToJsonString();
    }

    private static string Write(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadStamp(string? raw)
        => raw is not null
            && DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
}
