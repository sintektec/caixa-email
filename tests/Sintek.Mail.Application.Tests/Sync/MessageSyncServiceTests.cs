using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.Sync;

/// <summary>
/// Cobre a sincronização incremental de uma pasta: o caminho por onde toda mensagem entra
/// no banco local.
/// </summary>
public class MessageSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly FakeTimeProvider _clock = new(Now);

    public MessageSyncServiceTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _messages.ListUidsByFolderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<long>());
        _messages.GetParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MessageParticipant>());
    }

    private MessageSyncService CreateService() => new(
        _messages,
        _folders,
        _unitOfWork,
        _imap,
        new MoveMessageHandler(
            _messages, _folders, _directories, _audit, _unitOfWork,
            new OutboxEnqueuer(_outbox, _clock), _clock, NullLogger<MoveMessageHandler>.Instance),
        _clock,
        NullLogger<MessageSyncService>.Instance);

    private static Folder Inbox() => Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");

    private void ArrangeServer(FolderSyncState state, params FetchedMessage[] headers)
    {
        _imap.OpenFolderAsync("INBOX", Arg.Any<CancellationToken>()).Returns(state);
        _imap.FetchHeadersAsync("INBOX", Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(headers);
    }

    private static FetchedMessage Header(long uid, string from = "cliente@externo.com", bool isRead = false) => new()
    {
        Uid = uid,
        MessageId = $"<{uid}@servidor>",
        Subject = "Assunto",
        FromAddress = from,
        SentAt = Now,
        ReceivedAt = Now,
        IsRead = isRead,
        Addresses = [new FetchedAddress(AddressKind.From, from, null)],
    };

    [Fact]
    public async Task Sincronizar_PastaLocal_NaoTocaNoServidor()
    {
        var pending = Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);

        await CreateService().SyncFolderAsync(pending);

        await _imap.DidNotReceive().OpenFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_MensagemNova_EGravadaComOsCabecalhos()
    {
        var inbox = Inbox();
        ArrangeServer(new FolderSyncState(1, null, 2, 1, 1), Header(1));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.Added.Should().Be(1);

        await _messages.Received(1).AddAsync(
            Arg.Is<Message>(m => m.Uid == 1 && m.MessageId == "<1@servidor>" && m.FolderId == inbox.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_UidValidityMudou_LeAPastaDoZero()
    {
        // UIDs reatribuídos apontam para mensagens diferentes das originais. Seguir
        // incremental faria marcadores e exclusões caírem sobre mensagens erradas.
        var inbox = Inbox();
        inbox.UpdateSyncState(uidValidity: 100, highestModSeq: null, lastSeenUid: 50, Now);

        var antiga = Message.Create(AccountId, inbox.Id, "<antiga@servidor>", Now, Now, Now);
        antiga.SetRemoteIdentity(50, null, Now);

        _messages.ListUidsByFolderAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(new long[] { 50 });
        _messages.GetByUidAsync(inbox.Id, 50, Arg.Any<CancellationToken>()).Returns(antiga);

        ArrangeServer(new FolderSyncState(200, null, 2, 1, 0), Header(1));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.FullResync.Should().BeTrue();
        antiga.Uid.Should().Be(0, "o UID local não corresponde mais a nada no servidor");
        inbox.LastSeenUid.Should().Be(1);
    }

    [Fact]
    public async Task Sincronizar_MensagemJaConhecida_AtualizaMarcadoresDoServidor()
    {
        var inbox = Inbox();

        var existente = Message.Create(AccountId, inbox.Id, "<1@servidor>", Now, Now, Now);
        existente.SetRemoteIdentity(1, null, Now);
        existente.MarkSynced(Now);

        _messages.GetByUidAsync(inbox.Id, 1, Arg.Any<CancellationToken>()).Returns(existente);
        ArrangeServer(new FolderSyncState(1, null, 2, 1, 0), Header(1, isRead: true));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.Updated.Should().Be(1);
        existente.IsRead.Should().BeTrue();
        existente.SyncState.Should().Be(MessageSyncState.Synced);
    }

    [Fact]
    public async Task Sincronizar_AlteracaoLocalPendente_NaoEDesfeitaPeloServidor()
    {
        // O usuário marcou como lida offline e a fila ainda não empurrou. Deixar o servidor
        // vencer desfaria a ação diante dos olhos dele, e a fila em seguida a refaria.
        var inbox = Inbox();

        var pendente = Message.Create(AccountId, inbox.Id, "<1@servidor>", Now, Now, Now);
        pendente.SetRemoteIdentity(1, null, Now);
        pendente.SetRead(true, Now);

        _messages.GetByUidAsync(inbox.Id, 1, Arg.Any<CancellationToken>()).Returns(pendente);
        ArrangeServer(new FolderSyncState(1, null, 2, 1, 1), Header(1, isRead: false));

        await CreateService().SyncFolderAsync(inbox);

        pendente.IsRead.Should().BeTrue("a intenção local ainda não chegou ao servidor");
    }

    [Fact]
    public async Task Sincronizar_MensagemIncompativelEmPastaRestrita_VaiParaPendencias()
    {
        var directory = DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"), Now, invalidEmailAction: InvalidEmailAction.Block);

        var inbox = Inbox();
        inbox.SetExplicitRestriction(directory.Id, Now);
        inbox.ApplyEffectiveRestriction(null, Now);

        var pending = Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);
        _folders.GetByTypeAsync(AccountId, FolderType.Pending, Arg.Any<CancellationToken>()).Returns(pending);

        _messages.GetParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new MessageParticipant(AddressKind.From, EmailDomain.Parse("externo.com")),
            });

        Message? gravada = null;
        await _messages.AddAsync(Arg.Do<Message>(m => gravada = m), Arg.Any<CancellationToken>());

        ArrangeServer(new FolderSyncState(1, null, 2, 1, 1), Header(1));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.RedirectedToPending.Should().Be(1);
        gravada!.FolderId.Should().Be(pending.Id);
    }

    [Fact]
    public async Task Sincronizar_MensagemCompativelEmPastaRestrita_Permanece()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

        var inbox = Inbox();
        inbox.SetExplicitRestriction(directory.Id, Now);
        inbox.ApplyEffectiveRestriction(null, Now);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        _messages.GetParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new MessageParticipant(AddressKind.From, EmailDomain.Parse("sintek.com.br")),
            });

        Message? gravada = null;
        await _messages.AddAsync(Arg.Do<Message>(m => gravada = m), Arg.Any<CancellationToken>());

        ArrangeServer(new FolderSyncState(1, null, 2, 1, 1), Header(1, from: "contato@sintek.com.br"));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.RedirectedToPending.Should().Be(0);
        gravada!.FolderId.Should().Be(inbox.Id);
    }

    [Fact]
    public async Task Sincronizar_MensagemApagadaForaDoCliente_ERemovidaLocalmente()
    {
        var inbox = Inbox();

        var removida = Message.Create(AccountId, inbox.Id, "<9@servidor>", Now, Now, Now);
        removida.SetRemoteIdentity(9, null, Now);
        removida.MarkSynced(Now);

        _messages.ListUidsByFolderAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(new long[] { 9 });
        _messages.GetByUidAsync(inbox.Id, 9, Arg.Any<CancellationToken>()).Returns(removida);

        // O servidor diz ter zero mensagens; localmente há uma.
        ArrangeServer(new FolderSyncState(1, null, 10, 0, 0));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.RemovedRemotely.Should().Be(1);
        _messages.Received(1).Remove(removida);
    }

    [Fact]
    public async Task Sincronizar_MensagemComAlteracaoPendente_NaoERemovidaPelaReconciliacao()
    {
        // Ela pode ter sido movida localmente e ainda não sincronizada; apagá-la aqui
        // descartaria a ação do usuário.
        var inbox = Inbox();

        var pendente = Message.Create(AccountId, inbox.Id, "<9@servidor>", Now, Now, Now);
        pendente.SetRemoteIdentity(9, null, Now);
        pendente.SetRead(true, Now);

        _messages.ListUidsByFolderAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(new long[] { 9 });
        _messages.GetByUidAsync(inbox.Id, 9, Arg.Any<CancellationToken>()).Returns(pendente);

        ArrangeServer(new FolderSyncState(1, null, 10, 0, 0));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.RemovedRemotely.Should().Be(0);
        _messages.DidNotReceive().Remove(Arg.Any<Message>());
    }

    [Fact]
    public async Task Sincronizar_MensagemMovidaSemUidplus_EReconciliadaPeloMessageId()
    {
        // Depois de um MOVE em servidor sem UIDPLUS, a mensagem reaparece com UID novo. Sem
        // reconciliar pelo Message-ID, ela seria gravada de novo como se fosse outra.
        var inbox = Inbox();

        var existente = Message.Create(AccountId, inbox.Id, "<1@servidor>", Now, Now, Now);
        existente.SetRemoteIdentity(0, null, Now);
        existente.MarkSynced(Now);

        _messages.GetByUidAsync(inbox.Id, 77, Arg.Any<CancellationToken>()).Returns((Message?)null);
        _messages.GetByMessageIdAsync(AccountId, "<77@servidor>", Arg.Any<CancellationToken>())
            .Returns(existente);

        ArrangeServer(new FolderSyncState(1, null, 78, 1, 0), Header(77));

        var result = await CreateService().SyncFolderAsync(inbox);

        result.Added.Should().Be(0);
        existente.Uid.Should().Be(77);
        await _messages.DidNotReceive().AddAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sincronizar_AoFim_AtualizaOsContadoresDaPasta()
    {
        var inbox = Inbox();

        _folders.CountMessagesAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(12);
        _messages.CountUnreadAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(3);

        ArrangeServer(new FolderSyncState(1, null, 2, 12, 3));

        await CreateService().SyncFolderAsync(inbox);

        inbox.TotalCount.Should().Be(12);
        inbox.UnreadCount.Should().Be(3);
    }
}
