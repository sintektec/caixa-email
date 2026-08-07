using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Persistence.Search;

/// <summary>
/// Pesquisa de mensagens sobre o índice FTS5, com os filtros estruturais em SQL.
/// </summary>
/// <remarks>
/// <para>
/// SQL manual, e não LINQ, porque o EF Core não modela tabelas virtuais: o operador
/// <c>MATCH</c> do FTS5 simplesmente não existe na árvore de expressões. Todos os valores
/// entram por parâmetro — inclusive a expressão de MATCH, que é montada aqui a partir dos
/// termos com as aspas escapadas.
/// </para>
/// <para>
/// Datas são comparadas com <c>datetime()</c> dos dois lados: o EF grava
/// <c>DateTimeOffset</c> como texto preservando o fuso original, e a comparação de texto
/// puro erraria sempre que duas mensagens tivessem fusos diferentes.
/// </para>
/// </remarks>
public sealed class Fts5SearchService : ISearchService
{
    private readonly MailDbContext _context;

    public Fts5SearchService(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> SearchAsync(
        MessageSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasAnyCriteria)
        {
            return [];
        }

        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSql(query, command);

        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Guid.Parse(reader.GetString(0)));
        }

        return results;
    }

    private static string BuildSql(MessageSearchQuery query, DbCommand command)
    {
        var sql = new StringBuilder();
        var match = BuildMatchExpression(query);

        sql.Append("SELECT m.\"Id\" FROM \"Messages\" m");

        if (match is not null)
        {
            sql.Append(" JOIN \"MessagesSearch\" s ON s.\"MessageId\" = m.\"Id\"");
        }

        // Excluídas logicamente ficam de fora: elas já não aparecem em pasta nenhuma, e
        // uma pesquisa que as ressuscitasse confundiria mais do que ajudaria.
        sql.Append(" WHERE m.\"IsDeleted\" = 0");

        if (match is not null)
        {
            sql.Append(" AND s.\"Rowid\" IN (SELECT \"rowid\" FROM \"MessagesFts\" WHERE \"MessagesFts\" MATCH ")
                .Append(AddParameter(command, match))
                .Append(')');
        }

        // Os Guid vão como Guid mesmo, nunca como ToString(): o provider os grava em TEXT
        // maiúsculo, e um parâmetro minúsculo compararia diferente sem nenhum erro.
        if (query.AccountId is { } accountId)
        {
            sql.Append(" AND m.\"AccountId\" = ").Append(AddParameter(command, accountId));
        }

        if (query.FolderId is { } folderId)
        {
            sql.Append(" AND m.\"FolderId\" = ").Append(AddParameter(command, folderId));
        }

        if (query.DomainDirectoryId is { } domainId)
        {
            sql.Append(" AND m.\"AccountId\" IN (SELECT \"Id\" FROM \"Accounts\" WHERE \"DomainDirectoryId\" = ")
                .Append(AddParameter(command, domainId))
                .Append(')');
        }

        if (query.CategoryId is { } categoryId)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM \"MessageCategories\" mc")
                .Append(" WHERE mc.\"MessageId\" = m.\"Id\" AND mc.\"CategoryId\" = ")
                .Append(AddParameter(command, categoryId))
                .Append(')');
        }

        AppendAddressFilter(sql, command, query.Recipient, (int)AddressKind.To);
        AppendAddressFilter(sql, command, query.Cc, (int)AddressKind.Cc);

        if (query.IsRead is { } isRead)
        {
            sql.Append(" AND m.\"IsRead\" = ").Append(AddParameter(command, isRead ? 1 : 0));
        }

        if (query.IsFlagged is { } isFlagged)
        {
            sql.Append(" AND m.\"IsFlagged\" = ").Append(AddParameter(command, isFlagged ? 1 : 0));
        }

        if (query.HasAttachments is { } hasAttachments)
        {
            sql.Append(" AND m.\"HasAttachments\" = ").Append(AddParameter(command, hasAttachments ? 1 : 0));
        }

        if (query.Importance is { } importance)
        {
            sql.Append(" AND m.\"Importance\" = ").Append(AddParameter(command, (int)importance));
        }

        if (query.SyncState is { } syncState)
        {
            sql.Append(" AND m.\"SyncState\" = ").Append(AddParameter(command, (int)syncState));
        }

        if (query.ReceivedFrom is { } receivedFrom)
        {
            sql.Append(" AND datetime(m.\"ReceivedAt\") >= datetime(")
                .Append(AddParameter(command, ToUtcText(receivedFrom)))
                .Append(')');
        }

        if (query.ReceivedUntil is { } receivedUntil)
        {
            sql.Append(" AND datetime(m.\"ReceivedAt\") <= datetime(")
                .Append(AddParameter(command, ToUtcText(receivedUntil)))
                .Append(')');
        }

        sql.Append(" ORDER BY datetime(m.\"ReceivedAt\") DESC LIMIT ")
            .Append(AddParameter(command, Math.Clamp(query.Limit, 1, 1000)));

        return sql.ToString();
    }

    /// <summary>
    /// Monta a expressão de MATCH do FTS5, ou nulo se nenhum critério textual foi dado.
    /// </summary>
    /// <remarks>
    /// Cada termo vira <c>"termo"*</c>: entre aspas para ser literal — barrando a sintaxe
    /// de operadores do FTS5 vinda do usuário — e com <c>*</c> para casar por prefixo,
    /// que é o comportamento que se espera de busca incremental. Os campos específicos
    /// usam o filtro de coluna (<c>Subject : (...)</c>).
    /// </remarks>
    private static string? BuildMatchExpression(MessageSearchQuery query)
    {
        var parts = new List<string>();

        AppendMatchPart(parts, null, query.Text);
        AppendMatchPart(parts, "Subject", query.Subject);
        AppendMatchPart(parts, "TextBody", query.Body);
        AppendMatchPart(parts, "FromAddress", query.From);
        AppendMatchPart(parts, "AttachmentNames", query.AttachmentName);

        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static void AppendMatchPart(List<string> parts, string? column, string? text)
    {
        var terms = BuildTerms(text);
        if (terms is null)
        {
            return;
        }

        parts.Add(column is null ? terms : $"{column} : ({terms})");
    }

    private static string? BuildTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*")
            .ToList();

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    /// <summary>
    /// Filtro por destinatário ou cópia: são estruturais, não de texto completo, porque a
    /// coluna de participantes do índice mistura todos os campos e não sabe distinguir
    /// "Para" de "CC".
    /// </summary>
    private static void AppendAddressFilter(StringBuilder sql, DbCommand command, string? text, int kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var pattern = AddParameter(command, $"%{EscapeLike(text.Trim())}%");

        sql.Append(" AND EXISTS (SELECT 1 FROM \"MessageAddresses\" a")
            .Append(" WHERE a.\"MessageId\" = m.\"Id\" AND a.\"Kind\" = ")
            .Append(AddParameter(command, kind))
            .Append(" AND (a.\"Address\" LIKE ").Append(pattern).Append(" ESCAPE '\\'")
            .Append(" OR a.\"DisplayName\" LIKE ").Append(pattern).Append(" ESCAPE '\\'))");
    }

    private static string EscapeLike(string text)
        => text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string ToUtcText(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string AddParameter(DbCommand command, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"$p{command.Parameters.Count}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter.ParameterName;
    }
}
