using Microsoft.Data.Sqlite;

namespace Sintek.Mail.Persistence;

/// <summary>Onde o banco local vive e como ele é aberto.</summary>
/// <param name="DatabasePath">Caminho do arquivo do banco.</param>
/// <param name="EncryptionKey">
/// Chave do SQLCipher, obtida do Windows Credential Manager. Nunca lida de arquivo nem
/// derivada de senha do usuário.
/// </param>
public readonly record struct DatabaseOptions(string DatabasePath, string EncryptionKey);

/// <summary>
/// Monta conexões SQLite criptografadas com SQLCipher.
/// </summary>
/// <remarks>
/// <para>
/// A criptografia depende de dois detalhes que, se errados, falham em silêncio — o banco
/// abre normalmente, só que sem proteção alguma:
/// </para>
/// <list type="number">
/// <item>
/// O provider registrado precisa ser o <c>e_sqlcipher</c>. Por isso o projeto referencia
/// <c>Microsoft.Data.Sqlite.Core</c> (sem bundle próprio) junto de
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c>: o pacote <c>Microsoft.Data.Sqlite</c> completo
/// traria o <c>bundle_e_sqlite3</c>, sem suporte a criptografia, e venceria a corrida de
/// inicialização.
/// </item>
/// <item>
/// A chave precisa ser aplicada na abertura da conexão. O <c>Password</c> da connection
/// string faz o Microsoft.Data.Sqlite emitir <c>PRAGMA key</c> automaticamente quando o
/// provider suporta criptografia.
/// </item>
/// </list>
/// <para>
/// <see cref="VerifyEncryptionAsync"/> existe justamente para transformar essa falha
/// silenciosa em erro visível.
/// </para>
/// </remarks>
public static class SqlCipherConnectionFactory
{
    private static bool _initialized;
    private static readonly Lock InitLock = new();

    /// <summary>
    /// Registra o provider SQLCipher. Idempotente e seguro entre threads.
    /// </summary>
    public static void EnsureProviderInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }

    /// <summary>Monta a connection string do banco criptografado.</summary>
    public static string BuildConnectionString(DatabaseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EncryptionKey);

        return new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Cache privado (o padrão), NÃO compartilhado. O modo compartilhado é
            // desaconselhado pela própria Microsoft fora de bancos em memória e, com WAL
            // e várias conexões, chega a derrubar o processo em falha nativa. O
            // isolamento entre leitores e escritores já vem do WAL.
            ForeignKeys = true,
            Password = options.EncryptionKey,
        }.ToString();
    }

    /// <summary>Abre uma conexão criptografada, já com os PRAGMAs de desempenho aplicados.</summary>
    public static async Task<SqliteConnection> OpenAsync(
        DatabaseOptions options, CancellationToken cancellationToken = default)
    {
        EnsureProviderInitialized();

        var connection = new SqliteConnection(BuildConnectionString(options));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// Aplica os PRAGMAs de desempenho e integridade.
    /// </summary>
    /// <remarks>
    /// <c>journal_mode=WAL</c> é o que permite a sincronização escrever enquanto a
    /// interface lê, sem travar a listagem a cada mensagem recebida.
    /// <c>busy_timeout</c> evita que uma escrita concorrente falhe de imediato com
    /// "database is locked".
    /// </remarks>
    public static async Task ApplyPragmasAsync(
        SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirma que o arquivo está realmente criptografado.
    /// </summary>
    /// <remarks>
    /// Tenta abrir o banco <b>sem</b> chave e executar uma consulta. Em um arquivo
    /// cifrado isso falha; se conseguir ler, a criptografia não está ativa — provavelmente
    /// porque o provider registrado não é o SQLCipher. É o único jeito de detectar essa
    /// falha, já que ela não produz erro algum no caminho normal.
    /// </remarks>
    /// <returns><see langword="true"/> quando o arquivo está cifrado.</returns>
    public static async Task<bool> VerifyEncryptionAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath))
        {
            return false;
        }

        EnsureProviderInitialized();

        var plainConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(plainConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            // Leu o catálogo sem chave: o arquivo está em claro.
            return false;
        }
        catch (SqliteException)
        {
            // "file is not a database": exatamente o que um arquivo cifrado responde a
            // quem tenta abri-lo sem a chave.
            return true;
        }
    }
}
