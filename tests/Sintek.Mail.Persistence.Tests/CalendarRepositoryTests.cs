using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Cobre a agenda contra o banco real: as restrições do schema e a consulta por janela de
/// tempo, que compara datas e por isso cai na restrição do provedor do SQLite.
/// </summary>
public sealed class CalendarRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);
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
    public async Task Migracao_BancoNovo_CriaAsTabelasDaAgenda()
    {
        await using var context = await CreateMigratedContextAsync();

        (await context.CalendarEvents.CountAsync()).Should().Be(0);
        (await context.EventAttendees.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GravarEvento_MesmoUidNaMesmaConta_ORestringeOSchema()
    {
        // O UID é a identidade do evento na norma: duas linhas com o mesmo UID fariam a
        // atualização do organizador achar a errada.
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        context.CalendarEvents.Add(
            CalendarEvent.Create(accountId, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now));
        await context.SaveChangesAsync();

        context.CalendarEvents.Add(
            CalendarEvent.Create(accountId, "uid-1", "Outra", Inicio, Inicio.AddHours(1), Now));

        var gravar = async () => await context.SaveChangesAsync();

        await gravar.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GravarEvento_MesmoUidEmContasDiferentes_EPermitido()
    {
        // Duas contas podem legitimamente ter sido convidadas para a mesma reunião.
        await using var context = await CreateMigratedContextAsync();
        var primeira = await SeedAccountAsync(context);
        var segunda = await SeedAccountAsync(context, "cliente.com.br", "contato@cliente.com.br");

        context.CalendarEvents.Add(
            CalendarEvent.Create(primeira, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now));
        context.CalendarEvents.Add(
            CalendarEvent.Create(segunda, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now));

        await context.SaveChangesAsync();

        (await context.CalendarEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ListarNaJanela_EventosDeFusosDiferentes_ComparaPeloInstanteReal()
    {
        // A consulta por janela compara DateTimeOffset, o que o provedor do SQLite não
        // traduz sem o julianday(). Ver SqliteFunctions.
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var dentro = CalendarEvent.Create(
            accountId, "dentro", "Dentro",
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.FromHours(-3)),
            new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.FromHours(-3)), Now);

        var fora = CalendarEvent.Create(
            accountId, "fora", "Fora", Inicio.AddDays(60), Inicio.AddDays(60).AddHours(1), Now);

        context.CalendarEvents.AddRange(dentro, fora);
        await context.SaveChangesAsync();

        var eventos = await new CalendarRepository(context)
            .ListInRangeAsync(accountId, Inicio.AddDays(-1), Inicio.AddDays(1));

        eventos.Should().ContainSingle()
            .Which.Uid.Should().Be("dentro");
    }

    [Fact]
    public async Task ListarNaJanela_EventoRecorrenteAntigo_ContinuaNaLista()
    {
        // As ocorrências de um evento semanal podem cair na janela mesmo com o primeiro
        // encontro muito no passado; quem expande é o ICalendarSerializer.
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var recorrente = CalendarEvent.Create(
            accountId, "semanal", "Semanal", Inicio.AddYears(-1), Inicio.AddYears(-1).AddHours(1), Now);
        recorrente.SetRecurrence("FREQ=WEEKLY", Now);

        context.CalendarEvents.Add(recorrente);
        await context.SaveChangesAsync();

        var eventos = await new CalendarRepository(context)
            .ListInRangeAsync(accountId, Inicio.AddDays(-1), Inicio.AddDays(1));

        eventos.Should().ContainSingle();
    }

    [Fact]
    public async Task GetByUid_EventoGravado_VoltaComOsParticipantes()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var evento = CalendarEvent.Create(
            accountId, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.SetOrganizer(EmailAddress.Parse("ana@cliente.com.br"), "Ana", Now);
        evento.AddAttendee(EmailAddress.Parse("contato@sintek.com.br"), Now);
        evento.AddAttendee(EmailAddress.Parse("bruno@cliente.com.br"), Now);

        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        var lido = await new CalendarRepository(context).GetByUidAsync(accountId, "uid-1");

        lido.Should().NotBeNull();
        lido!.Attendees.Should().HaveCount(2);
        lido.OrganizerAddress!.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task RemoverConta_ComAgenda_LevaOsCompromissosJunto()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var evento = CalendarEvent.Create(
            accountId, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.AddAttendee(EmailAddress.Parse("ana@cliente.com.br"), Now);

        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        context.Accounts.Remove(await context.Accounts.SingleAsync(a => a.Id == accountId));
        await context.SaveChangesAsync();

        (await context.CalendarEvents.CountAsync()).Should().Be(0);
        (await context.EventAttendees.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ApagarAMensagemDeOrigem_NaoApagaOCompromisso()
    {
        // A limpeza de cache apaga mensagens antigas; a agenda não depende delas para
        // existir, e um compromisso que some junto seria perda silenciosa.
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var folder = Folder.Create(accountId, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        var mensagem = Message.Create(accountId, folder.Id, "<convite@cliente>", Now, Now, Now);
        context.Messages.Add(mensagem);
        await context.SaveChangesAsync();

        var evento = CalendarEvent.Create(
            accountId, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.LinkToMessage(mensagem.Id, Now);

        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        context.Messages.Remove(mensagem);
        await context.SaveChangesAsync();

        var lido = await new CalendarRepository(context).GetByUidAsync(accountId, "uid-1");

        lido.Should().NotBeNull();
        lido!.SourceMessageId.Should().BeNull();
    }

    [Fact]
    public async Task GetBySourceMessage_ConviteImportado_EEncontrado()
    {
        await using var context = await CreateMigratedContextAsync();
        var accountId = await SeedAccountAsync(context);

        var folder = Folder.Create(accountId, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        var mensagem = Message.Create(accountId, folder.Id, "<convite@cliente>", Now, Now, Now);
        context.Messages.Add(mensagem);
        await context.SaveChangesAsync();

        var evento = CalendarEvent.Create(
            accountId, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.LinkToMessage(mensagem.Id, Now);

        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        var lido = await new CalendarRepository(context).GetBySourceMessageAsync(mensagem.Id);

        lido!.Uid.Should().Be("uid-1");
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
