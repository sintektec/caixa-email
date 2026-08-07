using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Prova que a exigência de criptografia da especificação funciona de fato: banco real em
/// arquivo, migrações aplicadas e SQLCipher ativo.
/// </summary>
/// <remarks>
/// Estes testes usam arquivo em disco, não SQLite em memória, porque o que está sendo
/// verificado é justamente o formato do arquivo gravado — algo que o modo em memória não
/// permitiria observar.
/// </remarks>
public sealed class SqlCipherDatabaseTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private const string EncryptionKey = "chave-de-teste-nao-usada-em-producao";

    private string _directory = string.Empty;
    private string _databasePath = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sintek-mail-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "mail.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // O pool de conexões mantém o arquivo aberto; sem limpá-lo, a exclusão falha no
        // Windows e o teste deixa lixo para trás.
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza é conveniência, não parte do que está sendo verificado.
        }

        return Task.CompletedTask;
    }

    private MailDbContext CreateContext()
    {
        SqlCipherConnectionFactory.EnsureProviderInitialized();

        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite(SqlCipherConnectionFactory.BuildConnectionString(
                new DatabaseOptions(_databasePath, EncryptionKey)))
            .Options;

        return new MailDbContext(options);
    }

    [Fact]
    public async Task Migracoes_CriamOSchemaCompleto()
    {
        await using var context = CreateContext();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.EndsWith("InitialSchema", StringComparison.Ordinal));
        applied.Should().Contain(m => m.EndsWith("FullTextSearchIndex", StringComparison.Ordinal));
        applied.Should().Contain(m => m.EndsWith("RebuildSearchIndex", StringComparison.Ordinal));
        File.Exists(_databasePath).Should().BeTrue();
    }

    [Fact]
    public async Task BancoGravado_EstaRealmenteCriptografado()
    {
        // O teste central da exigência de segurança. Se o provider registrado não fosse o
        // SQLCipher, tudo funcionaria normalmente — só que o arquivo ficaria em claro,
        // sem nenhum erro para denunciar.
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            context.DomainDirectories.Add(
                DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now));
            await context.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();

        (await SqlCipherConnectionFactory.VerifyEncryptionAsync(_databasePath))
            .Should().BeTrue("o banco local precisa estar criptografado com SQLCipher");
    }

    [Fact]
    public async Task ArquivoCifrado_NaoExpoeTextoEmClaro()
    {
        const string secretSubject = "Proposta confidencial de aquisicao";

        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();

            var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
            var account = Account.Create(
                directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
            directory.AttachAccount(account, Now);

            var folder = Folder.Create(account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");
            var message = Message.Create(account.Id, folder.Id, "<sigilo@sintek.com.br>", Now, Now, Now);
            message.SetHeaders(secretSubject, EmailAddress.Parse("cliente@outro.com"), "Cliente", null, null, Now);

            context.DomainDirectories.Add(directory);
            context.Folders.Add(folder);
            context.Messages.Add(message);
            await context.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();

        // Varre os bytes crus do arquivo: o assunto não pode aparecer em lugar nenhum.
        var bytes = await File.ReadAllBytesAsync(_databasePath);
        var raw = System.Text.Encoding.UTF8.GetString(bytes);

        raw.Should().NotContain(secretSubject);
        raw.Should().NotContain("sintek.com.br");
    }

    [Fact]
    public async Task Banco_NaoAbreComChaveErrada()
    {
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
        }

        SqliteConnection.ClearAllPools();

        var wrongKeyOptions = new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite(SqlCipherConnectionFactory.BuildConnectionString(
                new DatabaseOptions(_databasePath, "chave-errada")))
            .Options;

        await using var wrongKeyContext = new MailDbContext(wrongKeyOptions);

        var act = async () => await wrongKeyContext.DomainDirectories.ToListAsync();

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task RegraDeDominio_EhPreservadaAtravesDoBanco()
    {
        var directoryId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();

            var directory = DomainDirectory.Create(
                EmailDomain.Parse("SINTEK.COM.BR"),
                Now,
                validationMode: DomainValidationMode.SenderAndRecipient,
                invalidEmailAction: InvalidEmailAction.MoveToPending,
                allowSubdomains: true,
                id: directoryId);
            directory.AddAlias(EmailDomain.Parse("sintek.tec.br"), Now);

            context.DomainDirectories.Add(directory);
            await context.SaveChangesAsync();
        }

        await using var reader = CreateContext();
        var loaded = await reader.DomainDirectories
            .Include(d => d.Aliases)
            .FirstAsync(d => d.Id == directoryId);

        // O value object precisa voltar normalizado e a regra intacta: é sobre este
        // estado que toda a validação de domínio opera.
        loaded.DomainName.Value.Should().Be("sintek.com.br");
        loaded.ValidationMode.Should().Be(DomainValidationMode.SenderAndRecipient);
        loaded.InvalidEmailAction.Should().Be(InvalidEmailAction.MoveToPending);
        loaded.AllowSubdomains.Should().BeTrue();
        loaded.Aliases.Should().ContainSingle();
        loaded.Accepts(EmailAddress.Parse("contato@vendas.sintek.com.br")).Should().BeTrue();
        loaded.Accepts(EmailAddress.Parse("contato@sintek.tec.br")).Should().BeTrue();
        loaded.Accepts(EmailAddress.Parse("contato@outro.com")).Should().BeFalse();
    }

    [Fact]
    public async Task IndiceFts_EncontraMensagemPeloAssunto()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = Message.Create(accountId, folderId, "<busca@sintek.com.br>", Now, Now, Now);
        message.SetHeaders("Orçamento de manutenção predial", null, null, null, null, Now);
        context.Messages.Add(message);
        await context.SaveChangesAsync();

        var found = await QueryFtsAsync(context, "orcamento");

        // Sem 'remove_diacritics 2' no tokenizador, buscar "orcamento" não acharia
        // "Orçamento" — o caso mais comum de busca em português.
        found.Should().Contain(message.Id);
    }

    [Fact]
    public async Task IndiceFts_AcompanhaExclusaoDeMensagem()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = Message.Create(accountId, folderId, "<remover@sintek.com.br>", Now, Now, Now);
        message.SetHeaders("Contrato de prestacao", null, null, null, null, Now);
        context.Messages.Add(message);
        await context.SaveChangesAsync();

        (await QueryFtsAsync(context, "contrato")).Should().Contain(message.Id);

        context.Messages.Remove(message);
        await context.SaveChangesAsync();

        (await QueryFtsAsync(context, "contrato")).Should().NotContain(message.Id);
    }

    [Fact]
    public async Task FilaDeSaida_ExigeSequenciaUnicaPorConta()
    {
        // A sequência é o que ordena a aplicação das operações no servidor. Duas
        // operações com o mesmo número tornariam a ordem indefinida.
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var (accountId, _) = await SeedAccountAsync(context);

        context.OutboxOperations.Add(OutboxOperation.Enqueue(
            accountId, OutboxOperationType.MarkAsRead, Guid.CreateVersion7(), "{}", 1, Now));
        await context.SaveChangesAsync();

        context.OutboxOperations.Add(OutboxOperation.Enqueue(
            accountId, OutboxOperationType.MoveMessage, Guid.CreateVersion7(), "{}", 1, Now));

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Pastas_AceitamMaisDeUmaPastaLocalPorConta()
    {
        // Pendências e Caixa de Saída são locais e têm RemotePath vazio. Sem o índice
        // único filtrado, a segunda delas seria recusada.
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var (accountId, _) = await SeedAccountAsync(context);

        context.Folders.Add(Folder.Create(accountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true));
        context.Folders.Add(Folder.Create(accountId, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true));

        var act = async () => await context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedAccountAsync(MailDbContext context)
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
        directory.AttachAccount(account, Now);

        var folder = Folder.Create(account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        context.DomainDirectories.Add(directory);
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        return (account.Id, folder.Id);
    }

    private static async Task<List<Guid>> QueryFtsAsync(MailDbContext context, string term)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s."MessageId"
            FROM "MessagesFts" fts
            JOIN "MessagesSearch" s ON s."Rowid" = fts."rowid"
            WHERE "MessagesFts" MATCH $term;
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$term";
        parameter.Value = term;
        command.Parameters.Add(parameter);

        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(Guid.Parse(reader.GetString(0)));
        }

        return results;
    }
}
