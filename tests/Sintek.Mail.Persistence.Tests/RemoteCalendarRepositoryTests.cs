using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// Cobre a sincronização de agenda contra o banco real: o schema novo, as consultas por
/// estado — que ordenam por data e por isso caem na restrição do provedor do SQLite — e a
/// unicidade do <c>href</c>, que é a identidade de rede do recurso.
/// </summary>
public sealed class RemoteCalendarRepositoryTests : IAsyncLifetime
{
    private const string EncryptionKey = "chave-de-teste-nao-usada-em-producao";
    private const string Colecao = "https://dav.exemplo.com/calendars/joao/agenda/";

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inicio = new(2026, 8, 10, 17, 0, 0, TimeSpan.Zero);

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
    public async Task Migracao_BancoNovo_CriaATabelaDeCalendariosRemotos()
    {
        await using var context = await CreateMigratedContextAsync();

        (await context.RemoteCalendars.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ListByAccountAsync_ColecoesDaConta_DevolveApenasAsDela()
    {
        await using var context = await CreateMigratedContextAsync();
        var contaA = await SeedAccountAsync(context);
        var contaB = await SeedAccountAsync(context, "outra.com.br", "contato@outra.com.br");

        context.RemoteCalendars.Add(RemoteCalendar.Create(
            contaA, CalendarProviderKind.CalDav, Colecao, "Agenda", Now));
        context.RemoteCalendars.Add(RemoteCalendar.Create(
            contaB, CalendarProviderKind.CalDav, Colecao, "Agenda", Now));
        await context.SaveChangesAsync();

        var lidas = await new RemoteCalendarRepository(context).ListByAccountAsync(contaA);

        lidas.Should().ContainSingle().Which.AccountId.Should().Be(contaA);
    }

    /// <summary>
    /// O mesmo endereço em duas contas é legítimo — dois usuários do mesmo servidor. Duas
    /// vezes na mesma conta é o espelhamento duplicando a coleção.
    /// </summary>
    [Fact]
    public async Task Persistencia_MesmaColecaoDuasVezesNaMesmaConta_ERecusada()
    {
        await using var context = await CreateMigratedContextAsync();
        var conta = await SeedAccountAsync(context);

        context.RemoteCalendars.Add(RemoteCalendar.Create(
            conta, CalendarProviderKind.CalDav, Colecao, "Agenda", Now));
        context.RemoteCalendars.Add(RemoteCalendar.Create(
            conta, CalendarProviderKind.CalDav, Colecao, "Agenda repetida", Now));

        var acao = async () => await context.SaveChangesAsync();

        await acao.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// O token é opaco e vai e volta sem interpretação — inclusive quando é uma URI, que é
    /// o formato do SabreDAV.
    /// </summary>
    [Fact]
    public async Task MarkSynced_TokenOpaco_VoltaDoBancoIntacto()
    {
        const string token = "http://sabredav.org/ns/sync-token/3145";

        await using (var escrita = await CreateMigratedContextAsync())
        {
            var conta = await SeedAccountAsync(escrita);
            var calendario = RemoteCalendar.Create(
                conta, CalendarProviderKind.CalDav, Colecao, "Agenda", Now);

            calendario.MarkSynced(token, "3145", Now);
            escrita.RemoteCalendars.Add(calendario);
            await escrita.SaveChangesAsync();
        }

        await using var leitura = CreateContext();
        var lido = await leitura.RemoteCalendars.SingleAsync();

        lido.SyncToken.Should().Be(token);
        lido.CTag.Should().Be("3145");
    }

    [Fact]
    public async Task GetByRemoteHrefAsync_RecursoConhecido_EEncontrado()
    {
        await using var context = await CreateMigratedContextAsync();
        var (conta, calendario) = await SeedCalendarAsync(context);

        var evento = CalendarEvent.Create(conta, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.BindToRemoteCalendar(calendario, Now);
        evento.MarkRemoteSynced($"{Colecao}1.ics", "\"1\"", "BEGIN:VCALENDAR", Now);

        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        var lido = await new CalendarRepository(context)
            .GetByRemoteHrefAsync(calendario, $"{Colecao}1.ics");

        lido!.Uid.Should().Be("uid-1");
        lido.RemoteETag.Should().Be("\"1\"");
        lido.RawICalendar.Should().Be("BEGIN:VCALENDAR");
    }

    /// <summary>
    /// A ordenação por data é o ponto em que o provedor do SQLite lança: sem o
    /// <c>julianday()</c>, esta consulta quebra na primeira execução, não na compilação.
    /// </summary>
    [Fact]
    public async Task ListPendingAsync_ComparaEOrdenaPorData_SemLancar()
    {
        await using var context = await CreateMigratedContextAsync();
        var (conta, calendario) = await SeedCalendarAsync(context);

        var pendente = CalendarEvent.Create(conta, "uid-1", "Pendente", Inicio, Inicio.AddHours(1), Now);
        pendente.BindToRemoteCalendar(calendario, Now);

        var sincronizado = CalendarEvent.Create(
            conta, "uid-2", "Sincronizado", Inicio, Inicio.AddHours(1), Now);
        sincronizado.BindToRemoteCalendar(calendario, Now);
        sincronizado.MarkRemoteSynced($"{Colecao}2.ics", "\"1\"", null, Now);

        context.CalendarEvents.AddRange(pendente, sincronizado);
        await context.SaveChangesAsync();

        var lidos = await new CalendarRepository(context).ListPendingAsync(calendario);

        lidos.Should().ContainSingle().Which.Uid.Should().Be("uid-1");
    }

    [Fact]
    public async Task ListConflictedAsync_OrdenaPorInicio_SemLancar()
    {
        await using var context = await CreateMigratedContextAsync();
        var (conta, calendario) = await SeedCalendarAsync(context);

        var tarde = CalendarEvent.Create(conta, "uid-2", "Depois", Inicio.AddDays(1), Inicio.AddDays(1).AddHours(1), Now);
        tarde.BindToRemoteCalendar(calendario, Now);
        tarde.MarkConflicted(Now);

        var cedo = CalendarEvent.Create(conta, "uid-1", "Antes", Inicio, Inicio.AddHours(1), Now);
        cedo.BindToRemoteCalendar(calendario, Now);
        cedo.MarkConflicted(Now);

        context.CalendarEvents.AddRange(tarde, cedo);
        await context.SaveChangesAsync();

        var lidos = await new CalendarRepository(context).ListConflictedAsync(conta);

        lidos.Select(e => e.Uid).Should().Equal("uid-1", "uid-2");
    }

    /// <summary>
    /// O <c>href</c> é a identidade de rede: dois compromissos apontando para o mesmo
    /// recurso na mesma coleção são a duplicação que a sincronização produziria sem esta
    /// restrição.
    /// </summary>
    [Fact]
    public async Task Persistencia_MesmoHrefNaMesmaColecao_ERecusado()
    {
        await using var context = await CreateMigratedContextAsync();
        var (conta, calendario) = await SeedCalendarAsync(context);

        var primeiro = CalendarEvent.Create(conta, "uid-1", "Um", Inicio, Inicio.AddHours(1), Now);
        primeiro.BindToRemoteCalendar(calendario, Now);
        primeiro.MarkRemoteSynced($"{Colecao}1.ics", "\"1\"", null, Now);

        var segundo = CalendarEvent.Create(conta, "uid-2", "Dois", Inicio, Inicio.AddHours(1), Now);
        segundo.BindToRemoteCalendar(calendario, Now);
        segundo.MarkRemoteSynced($"{Colecao}1.ics", "\"1\"", null, Now);

        context.CalendarEvents.AddRange(primeiro, segundo);

        var acao = async () => await context.SaveChangesAsync();

        await acao.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// Compromisso sem calendário remoto é o caso comum — a agenda local funciona sem
    /// servidor. Se a restrição de unicidade tratasse os nulos como iguais, o segundo
    /// convite importado por e-mail seria recusado.
    /// </summary>
    [Fact]
    public async Task Persistencia_VariosCompromissosSemCalendarioRemoto_SaoAceitos()
    {
        await using var context = await CreateMigratedContextAsync();
        var conta = await SeedAccountAsync(context);

        context.CalendarEvents.Add(
            CalendarEvent.Create(conta, "uid-1", "Um", Inicio, Inicio.AddHours(1), Now));
        context.CalendarEvents.Add(
            CalendarEvent.Create(conta, "uid-2", "Dois", Inicio, Inicio.AddHours(1), Now));

        await context.SaveChangesAsync();

        (await context.CalendarEvents.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// A coleção sai do banco e os compromissos ficam: apagar o espelho remoto não é o
    /// mesmo que apagar a agenda de quem o usava.
    /// </summary>
    [Fact]
    public async Task Remove_ColecaoApagada_PreservaOsCompromissos()
    {
        await using var context = await CreateMigratedContextAsync();
        var (conta, calendario) = await SeedCalendarAsync(context);

        var evento = CalendarEvent.Create(conta, "uid-1", "Reunião", Inicio, Inicio.AddHours(1), Now);
        evento.BindToRemoteCalendar(calendario, Now);
        evento.MarkRemoteSynced($"{Colecao}1.ics", "\"1\"", null, Now);
        context.CalendarEvents.Add(evento);
        await context.SaveChangesAsync();

        context.RemoteCalendars.Remove(await context.RemoteCalendars.SingleAsync());
        await context.SaveChangesAsync();

        var restante = await context.CalendarEvents.SingleAsync();
        restante.RemoteCalendarId.Should().BeNull();
    }

    /// <summary>
    /// O endereço do servidor de agenda entra na tabela; a credencial, nunca — ela vive no
    /// cofre do Windows, referenciada só pela chave.
    /// </summary>
    [Fact]
    public async Task ConfigureCalendar_ServidorDaConta_VoltaDoBanco()
    {
        await using (var escrita = await CreateMigratedContextAsync())
        {
            var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
            var account = Account.Create(
                directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Conta", Now);

            account.ConfigureCalendar(CalendarProviderKind.CalDav, Colecao, syncEnabled: true, Now);
            directory.AttachAccount(account, Now);

            escrita.DomainDirectories.Add(directory);
            await escrita.SaveChangesAsync();
        }

        await using var leitura = CreateContext();
        var lida = await leitura.Accounts.SingleAsync();

        lida.CalendarProvider.Should().Be(CalendarProviderKind.CalDav);
        lida.CalendarUrl.Should().Be(Colecao);
        lida.CalendarSyncEnabled.Should().BeTrue();
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

    private static async Task<(Guid AccountId, Guid CalendarId)> SeedCalendarAsync(MailDbContext context)
    {
        var accountId = await SeedAccountAsync(context);
        var calendar = RemoteCalendar.Create(
            accountId, CalendarProviderKind.CalDav, Colecao, "Agenda", Now);

        context.RemoteCalendars.Add(calendar);
        await context.SaveChangesAsync();

        return (accountId, calendar.Id);
    }
}
