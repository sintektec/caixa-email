using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Cobre o catálogo de contatos e o histórico de destinatários contra o banco real: o que
/// está em verificação são as restrições do schema, que só existem no banco migrado.
/// </summary>
public sealed class ContactRepositoriesTests : IAsyncLifetime
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
    public async Task Migracao_BancoNovo_CriaAsTabelasDeContatoEHistorico()
    {
        await using var context = await CreateMigratedContextAsync();

        (await context.Contacts.CountAsync()).Should().Be(0);
        (await context.ContactEmails.CountAsync()).Should().Be(0);
        (await context.RecipientHistory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GravarHistorico_MesmoEnderecoDuasVezesNaMesmaConta_ORestringeOSchema()
    {
        // A entrada acumula usos em vez de se repetir; o índice único é a rede que segura
        // uma corrida entre dois envios simultâneos.
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        context.RecipientHistory.Add(
            RecipientHistory.Create(accountId, EmailAddress.Parse("ana@cliente.com.br"), Now));
        await context.SaveChangesAsync();

        context.RecipientHistory.Add(
            RecipientHistory.Create(accountId, EmailAddress.Parse("ana@cliente.com.br"), Now));

        var gravar = async () => await context.SaveChangesAsync();

        await gravar.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GravarHistorico_MesmoEnderecoEmContasDiferentes_EPermitido()
    {
        await using var context = await CreateMigratedContextAsync();
        var primeira = await SeedAccountAsync(context);
        var segunda = await SeedAccountAsync(context, "cliente.com.br", "contato@cliente.com.br");

        context.RecipientHistory.Add(
            RecipientHistory.Create(primeira, EmailAddress.Parse("ana@cliente.com.br"), Now));
        context.RecipientHistory.Add(
            RecipientHistory.Create(segunda, EmailAddress.Parse("ana@cliente.com.br"), Now));

        await context.SaveChangesAsync();

        (await context.RecipientHistory.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RemoverConta_ComHistoricoEContatos_LevaTudoJunto()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        context.RecipientHistory.Add(
            RecipientHistory.Create(accountId, EmailAddress.Parse("ana@cliente.com.br"), Now));

        var contato = Contact.Create(accountId, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        context.Contacts.Add(contato);
        await context.SaveChangesAsync();

        context.Accounts.Remove(await context.Accounts.SingleAsync(a => a.Id == accountId));
        await context.SaveChangesAsync();

        (await context.RecipientHistory.CountAsync()).Should().Be(0);
        (await context.Contacts.CountAsync()).Should().Be(0);
        (await context.ContactEmails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ContactRepository_ContatoGravado_VoltaComOsEnderecos()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var contato = Contact.Create(accountId, "Ana Souza", Now, externalId: "ana-1");
        contato.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        contato.AddEmail(EmailAddress.Parse("ana@pessoal.com"), Now);
        context.Contacts.Add(contato);
        await context.SaveChangesAsync();

        var repositorio = new ContactRepository(context);
        var lido = await repositorio.GetByExternalIdAsync(accountId, "ana-1");

        lido.Should().NotBeNull();
        lido!.Emails.Should().HaveCount(2);
        lido.PrimaryEmail!.Address.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task ContactRepository_BuscaPorEndereco_EncontraPeloSecundario()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var contato = Contact.Create(accountId, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        contato.AddEmail(EmailAddress.Parse("ana@pessoal.com"), Now);
        context.Contacts.Add(contato);
        await context.SaveChangesAsync();

        var encontrado = await new ContactRepository(context)
            .GetByEmailAsync(accountId, EmailAddress.Parse("ana@pessoal.com"));

        encontrado!.Id.Should().Be(contato.Id);
    }

    [Fact]
    public async Task RecipientHistoryRepository_ListaParaSugestao_TrazOsMaisRecentesPrimeiro()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        context.RecipientHistory.Add(RecipientHistory.Create(
            accountId, EmailAddress.Parse("antigo@cliente.com.br"), Now.AddDays(-40)));
        context.RecipientHistory.Add(RecipientHistory.Create(
            accountId, EmailAddress.Parse("recente@cliente.com.br"), Now));
        await context.SaveChangesAsync();

        var entradas = await new RecipientHistoryRepository(context)
            .ListForSuggestionAsync(accountId, limit: 10);

        entradas[0].Address.Value.Should().Be("recente@cliente.com.br");
    }

    [Fact]
    public async Task ContactRepository_ContatosDeOutraConta_NaoAparecemNaLista()
    {
        // O catálogo é por conta de propósito: em um cliente organizado por Diretório de
        // Domínio, ver os contatos de um cliente ao escrever para outro é vazamento de
        // contexto.
        await using var context = await CreateMigratedContextAsync();
        var primeira = await SeedAccountAsync(context);
        var segunda = await SeedAccountAsync(context, "cliente.com.br", "contato@cliente.com.br");

        context.Contacts.Add(Contact.Create(primeira, "Ana Souza", Now));
        context.Contacts.Add(Contact.Create(segunda, "Bruno Lima", Now));
        await context.SaveChangesAsync();

        var lista = await new ContactRepository(context).ListAsync(primeira);

        lista.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ana Souza");
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

    private static async Task<Guid> SeedAccountAsync(
        MailDbContext context,
        string domain = "sintek.com.br",
        string address = "contato@sintek.com.br")
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse(domain), Now);
        var account = Account.Create(directory.Id, EmailAddress.Parse(address), "Conta", Now);
        directory.AttachAccount(account, Now);

        context.DomainDirectories.Add(directory);
        await context.SaveChangesAsync();

        return account.Id;
    }
}
