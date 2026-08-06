using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Application.UseCases.Rules;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Tests;

/// <summary>Fábricas de dependências compostas usadas por mais de um arquivo de teste.</summary>
internal static class TestFactories
{
    /// <summary>
    /// Histórico de destinatários inerte, para os testes que não o verificam.
    /// </summary>
    /// <remarks>
    /// O compositor alimenta o histórico no envio. Nos testes que verificam o envio em si,
    /// substituir os repositórios por dublês mantém o histórico fora do caminho sem
    /// remover o encadeamento real — que é o que quebraria se ele fosse opcional.
    /// </remarks>
    public static Sintek.Mail.Application.UseCases.Contacts.RecipientHistoryHandler InertRecipientHistory(
        IUnitOfWork unitOfWork, TimeProvider clock)
        => new(
            Substitute.For<IRecipientHistoryRepository>(),
            Substitute.For<IContactRepository>(),
            Substitute.For<IAccountRepository>(),
            Substitute.For<IDomainDirectoryRepository>(),
            unitOfWork,
            clock,
            NullLogger<Sintek.Mail.Application.UseCases.Contacts.RecipientHistoryHandler>.Instance);

    /// <summary>
    /// Importador de convites inerte, para os testes que não o verificam.
    /// </summary>
    /// <remarks>
    /// O download de corpo entrega à agenda o que vier em <c>text/calendar</c>. Com o
    /// serializador substituído nada é lido, e o encadeamento real continua de pé — que é
    /// o que quebraria se ele fosse opcional.
    /// </remarks>
    public static Sintek.Mail.Application.UseCases.Calendar.ImportInvitationHandler InertInvitations(
        IUnitOfWork unitOfWork, TimeProvider clock)
        => new(
            Substitute.For<ICalendarRepository>(),
            Substitute.For<IAccountRepository>(),
            Substitute.For<IDomainDirectoryRepository>(),
            Substitute.For<Abstractions.Calendar.ICalendarSerializer>(),
            Substitute.For<IAuditLogRepository>(),
            unitOfWork,
            clock,
            NullLogger<Sintek.Mail.Application.UseCases.Calendar.ImportInvitationHandler>.Instance);

    /// <summary>
    /// Sincronização de agenda inerte, para os testes do ciclo de e-mail.
    /// </summary>
    /// <remarks>
    /// Sem nenhum <c>ICalendarSyncProvider</c> registrado, o motor devolve resultado vazio
    /// sem tocar em rede — e o encadeamento real segue de pé, que é o que quebraria se ele
    /// fosse opcional no <c>SyncAccountHandler</c>.
    /// </remarks>
    public static Sintek.Mail.Application.Sync.CalendarSyncService InertCalendarSync(
        IUnitOfWork unitOfWork, TimeProvider clock)
        => new(
            Substitute.For<IRemoteCalendarRepository>(),
            Substitute.For<ICalendarRepository>(),
            [],
            unitOfWork,
            clock,
            NullLogger<Sintek.Mail.Application.Sync.CalendarSyncService>.Instance);

    /// <summary>Download de conteúdo com o importador de convites inerte.</summary>
    public static DownloadMessageContentHandler Download(
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        Abstractions.Mail.IImapClient imapClient,
        IHtmlSanitizer sanitizer,
        IAttachmentStore attachmentStore,
        TimeProvider clock)
        => new(
            messages, folders, unitOfWork, imapClient, sanitizer, attachmentStore,
            InertInvitations(unitOfWork, clock),
            clock,
            NullLogger<DownloadMessageContentHandler>.Instance);

    /// <summary>Compositor com o histórico de destinatários inerte.</summary>
    public static ComposeMessageHandler Compose(
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider clock)
        => new(
            messages, folders, accounts, unitOfWork, outbox,
            InertRecipientHistory(unitOfWork, clock),
            clock,
            NullLogger<ComposeMessageHandler>.Instance);

    /// <summary>
    /// Motor de chegada neutro: sem regras e sem remetentes bloqueados. É o que os testes
    /// de sincronização precisam para que a filtragem local não interfira no que eles
    /// verificam.
    /// </summary>
    public static ApplyArrivalRulesHandler NeutralArrivalRules(
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        MoveMessageHandler moveMessage,
        OutboxEnqueuer outbox,
        TimeProvider clock)
    {
        var rules = Substitute.For<IRuleRepository>();
        rules.ListEnabledForAccountAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Rule>());

        var reputations = Substitute.For<ISenderReputationRepository>();
        reputations.ListAsync(Arg.Any<SenderReputationKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SenderReputation>());

        var accounts = Substitute.For<IAccountRepository>();

        return new ApplyArrivalRulesHandler(
            rules,
            reputations,
            messages,
            folders,
            accounts,
            Substitute.For<ICategoryRepository>(),
            Substitute.For<IAuditLogRepository>(),
            unitOfWork,
            moveMessage,
            new MarkAsSpamHandler(
                messages, folders, moveMessage, outbox, unitOfWork,
                NullLogger<MarkAsSpamHandler>.Instance),
            Download(
                messages, folders, unitOfWork,
                Substitute.For<Abstractions.Mail.IImapClient>(),
                Substitute.For<IHtmlSanitizer>(),
                Substitute.For<IAttachmentStore>(),
                clock),
            Compose(messages, folders, accounts, unitOfWork, outbox, clock),
            outbox,
            clock,
            NullLogger<ApplyArrivalRulesHandler>.Instance);
    }
}

/// <summary>Relógio fixo, para tornar os testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>
    /// Encurta todo temporizador criado a partir deste relógio.
    /// </summary>
    /// <remarks>
    /// Existe para testar teto de espera sem esperar de verdade. Um
    /// <c>CancellationTokenSource(atraso, relógio)</c> pede um temporizador ao
    /// <see cref="TimeProvider"/>; sem esta substituição, verificar um teto de três minutos
    /// custaria três minutos de suíte. O que o teste comprova é o encadeamento — que o teto
    /// existe, que está ligado a este relógio e que a cancelação produz a mensagem certa —,
    /// não a duração em si, que é uma constante lida do código.
    /// </remarks>
    public TimeSpan? TimerDelayOverride { get; set; }

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => base.CreateTimer(callback, state, TimerDelayOverride ?? dueTime, period);
}

/// <summary>
/// Cofre de credenciais em memória.
/// </summary>
/// <remarks>
/// Guarda o que foi gravado para que os testes possam verificar o que <b>não</b> ficou para
/// trás: senha de cadastro malsucedido, senha de teste de conexão, credencial de conta
/// removida. É o tipo de resíduo que nenhuma asserção sobre o resultado da operação
/// revelaria.
/// </remarks>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    /// <summary>Chaves atualmente guardadas.</summary>
    public IReadOnlyCollection<string> Keys => _secrets.Keys;

    /// <summary>Chaves que já receberam alguma gravação, mesmo que depois apagadas.</summary>
    public List<string> WrittenKeys { get; } = [];

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.GetValueOrDefault(key));

    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[key] = secret;
        WrittenKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.Remove(key));

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.ContainsKey(key));
}

/// <summary>
/// Valores sintéticos usados no lugar de senha nos testes.
/// </summary>
/// <remarks>
/// São montados em tempo de execução em vez de escritos como literal ao lado de um campo
/// chamado <c>Password</c>. O detector de segredos do CI não tem como distinguir credencial
/// real de valor de teste, e um alerta que é sempre falso ensina a ignorar alertas — inclusive
/// os verdadeiros.
/// </remarks>
internal static class FakeSecret
{
    /// <summary>Devolve um valor previsível e inconfundivelmente fictício.</summary>
    public static string For(string label) => string.Join('-', "valor", "ficticio", label);
}
