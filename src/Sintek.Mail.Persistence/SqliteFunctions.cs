namespace Sintek.Mail.Persistence;

/// <summary>
/// Funções do SQLite expostas ao LINQ.
/// </summary>
/// <remarks>
/// <para>
/// Existem por causa de uma recusa explícita do provedor: <b>o SQLite não ordena por
/// <see cref="DateTimeOffset"/></b>. O EF Core grava o tipo como texto no formato
/// <c>yyyy-MM-dd HH:mm:ss.fffffffzzz</c>, que preserva o fuso original — e por isso a
/// ordem lexicográfica desse texto não é a ordem cronológica quando duas linhas têm fusos
/// diferentes. Em vez de ordenar errado em silêncio, o provedor lança
/// <see cref="NotSupportedException"/> ao traduzir o <c>ORDER BY</c>.
/// </para>
/// <para>
/// A saída é normalizar dentro do banco, que é o que <see cref="DateTimeText"/> faz: a
/// função <c>datetime()</c> do SQLite converte o texto para UTC, e a comparação passa a
/// ser cronológica. É a mesma solução que o <c>Fts5SearchService</c> já usava no SQL
/// manual; aqui ela fica disponível para as consultas em LINQ.
/// </para>
/// <para>
/// <c>datetime()</c> trunca em segundos, então quem ordena por tempo acrescenta um
/// desempate estável — normalmente o identificador, que é GUID v7 e portanto ordenado no
/// tempo.
/// </para>
/// </remarks>
public static class SqliteFunctions
{
    /// <summary>
    /// A função <c>datetime()</c> do SQLite: devolve o instante normalizado em UTC como
    /// texto ordenável.
    /// </summary>
    /// <remarks>
    /// Nunca é executada em memória — só existe para ser traduzida em SQL. Chamá-la fora
    /// de uma consulta é erro de programação, e a exceção o denuncia na hora.
    /// </remarks>
    public static string DateTimeText(DateTimeOffset value)
        => throw new NotSupportedException(
            "SqliteFunctions.DateTimeText só pode ser usada dentro de uma consulta LINQ.");
}
