namespace Sintek.Mail.Persistence;

/// <summary>
/// Funções do SQLite expostas ao LINQ.
/// </summary>
/// <remarks>
/// <para>
/// Existem por causa de uma recusa do provedor: <b>o SQLite não ordena nem compara
/// <see cref="DateTimeOffset"/></b>. O EF Core grava o tipo como texto no formato
/// <c>yyyy-MM-dd HH:mm:ss.fffffffzzz</c>, que preserva o fuso original — e por isso a ordem
/// lexicográfica desse texto não é a ordem cronológica quando duas linhas têm fusos
/// diferentes. Em vez de comparar errado em silêncio, o provedor recusa: <c>ORDER BY</c>
/// lança <see cref="NotSupportedException"/> e <c>&lt;=</c> não traduz.
/// </para>
/// <para>
/// A saída é normalizar dentro do banco. <see cref="JulianDay"/> mapeia para a função
/// <c>julianday()</c> do SQLite, que interpreta o texto com o fuso declarado e devolve um
/// número — comparável e ordenável, e portanto aceito pelo provedor nos dois casos. É o
/// mesmo princípio que o <c>Fts5SearchService</c> já aplicava com <c>datetime()</c> no SQL
/// manual.
/// </para>
/// <para>
/// Quem ordena por tempo acrescenta um desempate estável — normalmente o identificador,
/// que é GUID v7 e portanto ordenado no tempo.
/// </para>
/// </remarks>
public static class SqliteFunctions
{
    /// <summary>
    /// A função <c>julianday()</c> do SQLite: devolve o instante como número de dias
    /// julianos, já normalizado a partir do fuso declarado no texto.
    /// </summary>
    /// <remarks>
    /// Nunca é executada em memória — só existe para ser traduzida em SQL. Chamá-la fora
    /// de uma consulta é erro de programação, e a exceção o denuncia na hora.
    /// </remarks>
    public static double JulianDay(DateTimeOffset value)
        => throw new NotSupportedException(
            "SqliteFunctions.JulianDay só pode ser usada dentro de uma consulta LINQ.");
}
