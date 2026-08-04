using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sintek.Mail.Persistence.Interceptors;

/// <summary>
/// Interceptor that applies the SQLCipher encryption key on connection open.
/// </summary>
public sealed class SqlCipherInterceptor : DbConnectionInterceptor
{
    private readonly string _encryptionKey;

    public SqlCipherInterceptor(string encryptionKey)
    {
        _encryptionKey = encryptionKey ?? throw new ArgumentNullException(nameof(encryptionKey));
    }

    public override async Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            await using var command = sqliteConnection.CreateCommand();
            command.CommandText = $"PRAGMA key = '{_encryptionKey}';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public override void ConnectionOpened(
        System.Data.Common.DbConnection connection,
        ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            using var command = sqliteConnection.CreateCommand();
            command.CommandText = $"PRAGMA key = '{_encryptionKey}';";
            command.ExecuteNonQuery();
        }
    }
}
