using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Cobre as consultas que ordenam por tempo.
/// </summary>
/// <remarks>
/// O provedor do SQLite recusa <c>ORDER BY</c> sobre <c>DateTimeOffset</c> e lança
/// <see cref="NotSupportedException"/> ao traduzir a consulta. Nada disso aparece na
/// compilação: quebra em tempo de execução, na primeira vez que a tela abre. Estes testes
/// existem para que a quebra apareça aqui, e não na mão do usuário.
/// </remarks>
public sealed class DateOrderingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string EncryptionKey = "chave-de-teste-nao-usada-em-producao";

    private string _directoryPath = string.Empty;
    private string _databasePath = string.Empty;

    public Task InitializeAsync()
    {
        _directoryPath = Path.Combine(
            Path.GetTempPath(), "sintek-mail-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _databasePath = Path.Combine(_directoryPath, "mail.db");

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza é conveniência, não parte do que está sendo verificado.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ListIdsByFolder_MensagensDeFusosDiferentes_OrdenaPeloInstanteReal()
    {
        // A mensagem mais nova tem o texto lexicograficamente menor por causa do fuso:
        // 09:00-03:00 é 12:00 UTC, depois de 11:00+00:00. Ordenar pelo texto cru a
        // colocaria no lugar errado.
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAsync(context);

        var antiga = await AddMessageAsync(
            context, accountId, folderId, new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero));
        var recente = await AddMessageAsync(
            context, accountId, folderId,
            new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.FromHours(-3)));

        var ids = await new MessageRepository(context).ListIdsByFolderAsync(folderId);

        ids.Should().Equal(recente.Id, antiga.Id);
    }

    [Fact]
    public async Task ListRecent_RegistroDeAuditoria_TrazOsEventosMaisNovosPrimeiro()
    {
        await using var context = await CreateMigratedContextAsync();

        var antigo = AuditLogEntry.Record(
            AuditEventType.MessageMovedToPending, "Evento antigo", Now.AddHours(-2));
        var novo = AuditLogEntry.Record(
            AuditEventType.MessageMovedToPending, "Evento novo", Now);

        context.AuditLog.AddRange(antigo, novo);
        await context.SaveChangesAsync();

        var eventos = await new AuditLogRepository(context).ListRecentAsync(10);

        eventos.Select(e => e.Id).Should().Equal(novo.Id, antigo.Id);
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

    private async Task<MailDbContext> CreateMigratedContextAsync()
    {
        var context = CreateContext();
        await context.Database.MigrateAsync();

        return context;
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedAsync(MailDbContext context)
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Conta", Now);
        directory.AttachAccount(account, Now);

        var folder = Folder.Create(
            account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        context.DomainDirectories.Add(directory);
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        return (account.Id, folder.Id);
    }

    private static async Task<Message> AddMessageAsync(
        MailDbContext context, Guid accountId, Guid folderId, DateTimeOffset receivedAt)
    {
        var message = Message.Create(
            accountId, folderId, $"<{Guid.CreateVersion7():N}@teste.local>", receivedAt, receivedAt, Now);

        context.Messages.Add(message);
        await context.SaveChangesAsync();

        return message;
    }
}
