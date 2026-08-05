using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.Sync;

/// <summary>
/// Cobre o ciclo completo de sincronização de uma conta. A ordem das etapas é o
/// comportamento que mais importa aqui.
/// </summary>
public class SyncAccountHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly IOutboxDrainer _drainer = Substitute.For<IOutboxDrainer>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

    public SyncAccountHandlerTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());
        _imap.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<RemoteFolder>());
        _folders.ListByAccountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());
        _messages.ListUidsByFolderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<long>());
    }

    private SyncAccountHandler CreateHandler() => new(
        _accounts,
        _folders,
        _unitOfWork,
        _imap,
        _drainer,
        new FolderMirrorService(_folders, _unitOfWork, _clock, NullLogger<FolderMirrorService>.Instance),
        new MessageSyncService(
            _messages,
            _folders,
            _unitOfWork,
            _imap,
            new MoveMessageHandler(
                _messages, _folders, _directories, _audit, _unitOfWork,
                new OutboxEnqueuer(_outbox, _clock), _clock, NullLogger<MoveMessageHandler>.Instance),
            _clock,
            NullLogger<MessageSyncService>.Instance),
        _clock,
        NullLogger<SyncAccountHandler>.Instance);

    [Fact]
    public async Task Sincronizar_FilaDeSaida_EDrenadaAntesDeLerOServidor()
    {
        // Ler primeiro traria o estado antigo e sobrescreveria a intenção do usuário: o
        // marcador que ele mudou offline voltaria atrás e a fila o refaria em seguida.
        await CreateHandler().HandleAsync(_account.Id);

        Received.InOrder(() =>
        {
            _drainer.DrainAsync(_account, Arg.Any<CancellationToken>());
            _imap.ListFoldersAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Sincronizar_SemConexao_MarcaOfflineENaoErro()
    {
        // Sem rede não é falha: é o modo offline funcionando como projetado.
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Failure("Servidor inacessível."));

        var result = await CreateHandler().HandleAsync(_account.Id);

        result.Succeeded.Should().BeFalse();
        _account.SyncStatus.Should().Be(AccountSyncStatus.Offline);
        _account.LastSyncError.Should().BeNull();

        await _drainer.DidNotReceive().DrainAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_CredencialRecusada_MarcaEstadoProprio()
    {
        // Distinto de erro comum: a ação do usuário é reautenticar, não esperar.
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.AuthenticationFailure("Senha recusada."));

        var result = await CreateHandler().HandleAsync(_account.Id);

        result.IsAuthenticationFailure.Should().BeTrue();
        _account.SyncStatus.Should().Be(AccountSyncStatus.AuthenticationFailed);
    }

    [Fact]
    public async Task Sincronizar_ContaDesativada_NemTenta()
    {
        _account.SetActive(false, Now);

        var result = await CreateHandler().HandleAsync(_account.Id);

        result.Succeeded.Should().BeFalse();
        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_ContaInexistente_RecusaComMotivo()
    {
        var result = await CreateHandler().HandleAsync(Guid.CreateVersion7());

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Sincronizar_CicloCompleto_MarcaAContaComoEmDia()
    {
        _drainer.DrainAsync(_account, Arg.Any<CancellationToken>()).Returns(3);

        var result = await CreateHandler().HandleAsync(_account.Id);

        result.Succeeded.Should().BeTrue();
        result.OutboxDrained.Should().Be(3);
        _account.SyncStatus.Should().Be(AccountSyncStatus.Online);
        _account.LastSyncAt.Should().Be(Now);

        await _imap.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_FalhaInesperada_RegistraOErroSemDerrubarOChamador()
    {
        _imap.ListFoldersAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RemoteFolder>>>(_ => throw new IOException("conexão interrompida"));

        var result = await CreateHandler().HandleAsync(_account.Id);

        result.Succeeded.Should().BeFalse();
        _account.SyncStatus.Should().Be(AccountSyncStatus.Error);
        _account.LastSyncError.Should().Contain("conexão interrompida");
    }

    [Fact]
    public void OrdenacaoDePastas_CaixaDeEntradaPrimeiro_ArquivoPorUltimo()
    {
        // Numa primeira sincronização de caixa grande, a ordem decide se o usuário vê a
        // correspondência recente em segundos ou depois do Arquivo Morto de 2019.
        var accountId = _account.Id;

        var arquivo = Folder.Create(accountId, "Arquivados", FolderType.Archive, Now, remotePath: "Archive");
        var inbox = Folder.Create(accountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        var custom = Folder.Create(accountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        var enviados = Folder.Create(accountId, "Enviados", FolderType.Sent, Now, remotePath: "Sent");
        var local = Folder.Create(accountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);

        var ordered = SyncAccountHandler.OrderForSync([arquivo, inbox, custom, enviados, local]).ToList();

        ordered.Should().ContainInOrder(inbox, enviados, custom, arquivo);
        ordered.Should().NotContain(local, "pasta local não tem contrapartida no servidor");
    }
}
