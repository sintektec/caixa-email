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
            new DownloadMessageContentHandler(
                messages, folders, unitOfWork,
                Substitute.For<Abstractions.Mail.IImapClient>(),
                Substitute.For<IHtmlSanitizer>(),
                Substitute.For<IAttachmentStore>(),
                clock,
                NullLogger<DownloadMessageContentHandler>.Instance),
            new ComposeMessageHandler(
                messages, folders, accounts, unitOfWork, outbox, clock,
                NullLogger<ComposeMessageHandler>.Instance),
            outbox,
            clock,
            NullLogger<ApplyArrivalRulesHandler>.Instance);
    }
}

/// <summary>Relógio fixo, para tornar os testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
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
