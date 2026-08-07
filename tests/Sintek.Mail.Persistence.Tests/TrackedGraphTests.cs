using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Cobre o que acontece quando um filho <b>novo</b> é pendurado num agregado já rastreado.
/// </summary>
/// <remarks>
/// <para>
/// Este é o teste que faltava, e a ausência dele custou caro. Toda entidade recebe a chave no
/// construtor (<c>Entity</c> usa <c>Guid.CreateVersion7()</c>, nunca <c>Guid.Empty</c>) — é a
/// convenção do projeto, escolhida porque identificadores ordenados no tempo preservam a
/// localidade dos índices do SQLite.
/// </para>
/// <para>
/// A consequência não é óbvia: o EF Core decide entre <c>Added</c> e <c>Modified</c> por
/// <c>IsKeySet</c>. Chave preenchida faz ele assumir que a linha já existe, e emitir
/// <c>UPDATE ... WHERE Id = @p</c> para uma linha que nunca foi inserida — zero linhas
/// afetadas, <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// Nenhum teste alcançava isso porque todos montavam o grafo inteiro antes do primeiro
/// <c>SaveChanges</c>, e aí o pai também é <c>Added</c> e o filho vai junto. O caso real é o
/// oposto: a mensagem foi lida do banco (rastreada), e só depois o corpo baixado é pendurado
/// nela.
/// </para>
/// </remarks>
public sealed class TrackedGraphTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 23, 0, 0, TimeSpan.Zero);
    private const string EncryptionKey = "chave-de-teste-do-grafo-rastreado";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"sintek-grafo-{Guid.CreateVersion7():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pendurar pela navegação, só, <b>não grava</b> — é a armadilha, e fica presa aqui.
    /// </summary>
    /// <remarks>
    /// Este teste espera a falha de propósito. Ele existe para que a armadilha tenha um lugar
    /// executável: quem "simplificar" o caso de uso removendo a chamada ao repositório vai ver
    /// este teste continuar verde e o irmão abaixo ficar vermelho, e a mensagem dirá por quê.
    /// </remarks>
    [Fact]
    public async Task PendurarCorpoSoNaNavegacao_FalhaPorqueOEfDecideAtualizar()
    {
        await using var context = await CreateMigratedContextAsync();
        var folderId = await SeedMessageAsync(context);

        var message = await context.Messages.FirstAsync(m => m.FolderId == folderId);

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent("<p>Olá</p>", "Olá", "<p>Olá</p>", false, Now);
        message.SetBody(body, Now);

        var gravar = async () => await context.SaveChangesAsync();

        (await gravar.Should().ThrowAsync<DbUpdateConcurrencyException>())
            .WithMessage("*affect 1 row*actually affected 0*");
    }

    /// <summary>
    /// Com a inserção explícita, o corpo persiste.
    /// </summary>
    /// <remarks>
    /// É o caminho do clique numa mensagem: o painel carrega a mensagem do banco e o download
    /// pendura nela o corpo recém-criado. Sem a inserção, a gravação falhava, o corpo nunca
    /// persistia, <c>DownloadedAt</c> nunca era gravado, e todo clique seguinte refazia o
    /// download pela rede e falhava igual — inclusive depois de fechar e reabrir.
    /// </remarks>
    [Fact]
    public async Task RegistrarCorpoNoRepositorio_Persiste()
    {
        await using var context = await CreateMigratedContextAsync();
        var folderId = await SeedMessageAsync(context);

        var repository = new MessageRepository(context);
        var message = await context.Messages.FirstAsync(m => m.FolderId == folderId);

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent("<p>Olá</p>", "Olá", "<p>Olá</p>", false, Now);

        message.SetBody(body, Now);
        repository.AddBody(body);

        await context.SaveChangesAsync();

        await using var conferencia = CreateContext();
        var persistido = await conferencia.MessageBodies.FirstOrDefaultAsync(b => b.MessageId == message.Id);

        persistido.Should().NotBeNull();
        persistido!.DownloadedAt.Should().NotBeNull();
        persistido.SanitizedHtml.Should().Be("<p>Olá</p>");
    }

    /// <summary>Anexo novo em mensagem rastreada, mesmo caminho.</summary>
    [Fact]
    public async Task RegistrarAnexoNoRepositorio_Persiste()
    {
        await using var context = await CreateMigratedContextAsync();
        var folderId = await SeedMessageAsync(context);

        var repository = new MessageRepository(context);
        var message = await context.Messages.FirstAsync(m => m.FolderId == folderId);

        var attachment = Attachment.Create(
            message.Id, "contrato.pdf", "application/pdf", 1024, "2", Now);

        message.AddAttachment(attachment);
        repository.AddAttachment(attachment);

        await context.SaveChangesAsync();

        await using var conferencia = CreateContext();
        (await conferencia.Attachments.CountAsync(a => a.MessageId == message.Id)).Should().Be(1);
    }

    /// <summary>Participante novo em mensagem rastreada — o caminho do rascunho reeditado.</summary>
    [Fact]
    public async Task RegistrarParticipanteNoRepositorio_Persiste()
    {
        await using var context = await CreateMigratedContextAsync();
        var folderId = await SeedMessageAsync(context);

        var repository = new MessageRepository(context);
        var message = await context.Messages.FirstAsync(m => m.FolderId == folderId);

        var address = MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("destino@sintek.com.br"), Now);

        message.AddAddress(address);
        repository.AddAddress(address);

        await context.SaveChangesAsync();

        await using var conferencia = CreateContext();
        (await conferencia.MessageAddresses.CountAsync(a => a.MessageId == message.Id)).Should().Be(1);
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

    /// <summary>Deixa no banco uma conta, uma pasta e uma mensagem — e limpa o rastreador.</summary>
    /// <remarks>
    /// O <c>ChangeTracker.Clear</c> no fim é o que torna o teste fiel: sem ele a mensagem
    /// continuaria rastreada como <c>Added</c> desta gravação, e o filho iria junto sem
    /// revelar nada. O caso real relê a mensagem de um contexto que não a criou.
    /// </remarks>
    private static async Task<Guid> SeedMessageAsync(MailDbContext context)
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Conta", Now);
        directory.AttachAccount(account, Now);

        var folder = Folder.Create(account.Id, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        var message = Message.Create(account.Id, folder.Id, "<1@servidor>", Now, Now, Now);
        message.SetRemoteIdentity(42, null, Now);

        context.DomainDirectories.Add(directory);
        context.Folders.Add(folder);
        context.Messages.Add(message);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        return folder.Id;
    }
}
