using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Folders;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;
using System.Text.Json;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a gestão de pastas e a marcação de spam — a fase 5, em que a regra de domínio
/// chega à interface de pastas.
/// </summary>
public class FolderAndSpamHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 16, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<OutboxOperation> _enqueued = [];

    public FolderAndSpamHandlersTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _outbox.AddAsync(Arg.Do<OutboxOperation>(_enqueued.Add), Arg.Any<CancellationToken>());

        _folders.ListByAccountAsync(AccountId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());
        _messages.GetParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MessageParticipant>());
    }

    private ManageFolderHandler FolderHandler() => new(
        _folders, _unitOfWork, new OutboxEnqueuer(_outbox, _clock), _clock,
        NullLogger<ManageFolderHandler>.Instance);

    private MarkAsSpamHandler SpamHandler() => new(
        _messages,
        _folders,
        new MoveMessageHandler(
            _messages, _folders, _directories, _audit, _unitOfWork,
            new OutboxEnqueuer(_outbox, _clock), _clock, NullLogger<MoveMessageHandler>.Instance),
        new OutboxEnqueuer(_outbox, _clock),
        _unitOfWork,
        NullLogger<MarkAsSpamHandler>.Instance);

    private void ArrangeFolders(params Folder[] folders)
    {
        _folders.ListByAccountAsync(AccountId, Arg.Any<CancellationToken>()).Returns(folders);

        foreach (var folder in folders)
        {
            _folders.GetByIdAsync(folder.Id, Arg.Any<CancellationToken>()).Returns(folder);
        }
    }

    // ----- Criação --------------------------------------------------------------------

    [Fact]
    public async Task CriarPasta_NaRaiz_GravaEEnfileiraOComando()
    {
        var result = await FolderHandler().CreateAsync(AccountId, "Clientes");

        result.Succeeded.Should().BeTrue();

        await _folders.Received(1).AddAsync(
            Arg.Is<Folder>(f => f.RemotePath == "Clientes" && f.FolderType == FolderType.Custom),
            Arg.Any<CancellationToken>());

        _enqueued.Should().ContainSingle()
            .Which.OperationType.Should().Be(OutboxOperationType.CreateFolder);
    }

    [Fact]
    public async Task CriarPasta_DentroDeOutra_MontaOCaminhoComODelimitadorDaMae()
    {
        var parent = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        ArrangeFolders(parent);

        Folder? created = null;
        await _folders.AddAsync(Arg.Do<Folder>(f => created = f), Arg.Any<CancellationToken>());

        var result = await FolderHandler().CreateAsync(AccountId, "2026", parent.Id);

        result.Succeeded.Should().BeTrue();
        created!.RemotePath.Should().Be("Clientes/2026");
        created.ParentFolderId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task CriarPasta_DentroDeRamoRestrito_NasceComARestricaoHerdada()
    {
        // Sem a herança, a subpasta nova aceitaria o que o ramo existe para recusar.
        var directoryId = Guid.CreateVersion7();
        var parent = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        parent.SetExplicitRestriction(directoryId, Now);
        parent.ApplyEffectiveRestriction(null, Now);
        ArrangeFolders(parent);

        Folder? created = null;
        await _folders.AddAsync(Arg.Do<Folder>(f => created = f), Arg.Any<CancellationToken>());

        await FolderHandler().CreateAsync(AccountId, "2026", parent.Id);

        created!.EffectiveRestrictionDomainDirectoryId.Should().Be(directoryId);
        created.IsRestrictionInherited.Should().BeTrue();
    }

    [Fact]
    public async Task CriarPasta_NomeJaExistenteNoLocal_Recusa()
    {
        ArrangeFolders(Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes"));

        var result = await FolderHandler().CreateAsync(AccountId, "Clientes");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Clientes");
    }

    // ----- Renomeação -----------------------------------------------------------------

    [Fact]
    public async Task RenomearPasta_ComSubpastas_AtualizaOsCaminhosDasDescendentes()
    {
        // O RENAME do IMAP renomeia a subárvore no servidor; sem acompanhar localmente, a
        // próxima sincronização trataria as descendentes como sumidas.
        var root = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        var child = Folder.Create(
            AccountId, "2026", FolderType.Custom, Now, parentFolderId: root.Id, remotePath: "Clientes/2026");

        ArrangeFolders(root, child);

        var result = await FolderHandler().RenameAsync(root.Id, "Carteira");

        result.Succeeded.Should().BeTrue();
        root.RemotePath.Should().Be("Carteira");
        child.RemotePath.Should().Be("Carteira/2026");

        var payload = JsonSerializer.Deserialize<FolderOperationPayload>(_enqueued.Single().PayloadJson);
        payload.RemotePath.Should().Be("Clientes");
        payload.NewRemotePath.Should().Be("Carteira");
    }

    [Fact]
    public async Task RenomearPasta_Padrao_Recusa()
    {
        // O servidor recria as pastas padrão na sincronização seguinte; renomeá-las faria a
        // pasta "voltar" com o nome antigo, parecendo defeito.
        var inbox = Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        ArrangeFolders(inbox);

        var result = await FolderHandler().RenameAsync(inbox.Id, "Entrada");

        result.Succeeded.Should().BeFalse();
        _enqueued.Should().BeEmpty();
    }

    // ----- Exclusão -------------------------------------------------------------------

    [Fact]
    public async Task ExcluirPasta_ComConteudoSemConfirmacao_RecusaEExplica()
    {
        var folder = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        ArrangeFolders(folder);
        _folders.CountMessagesAsync(folder.Id, Arg.Any<CancellationToken>()).Returns(7);

        var result = await FolderHandler().DeleteAsync(folder.Id, confirmed: false);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("7");
        _folders.DidNotReceive().Remove(Arg.Any<Folder>());
    }

    [Fact]
    public async Task ExcluirPasta_Vazia_DispensaConfirmacao()
    {
        var folder = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        ArrangeFolders(folder);

        var result = await FolderHandler().DeleteAsync(folder.Id, confirmed: false);

        result.Succeeded.Should().BeTrue();
        _folders.Received(1).Remove(folder);
        _enqueued.Should().ContainSingle()
            .Which.OperationType.Should().Be(OutboxOperationType.DeleteFolder);
    }

    [Fact]
    public async Task ExcluirPasta_Confirmada_RemoveASubarvoreDaFolhaParaARaiz()
    {
        var root = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        var child = Folder.Create(
            AccountId, "2026", FolderType.Custom, Now, parentFolderId: root.Id, remotePath: "Clientes/2026");

        ArrangeFolders(root, child);
        _folders.CountMessagesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);

        var removed = new List<Folder>();
        _folders.When(f => f.Remove(Arg.Any<Folder>())).Do(call => removed.Add(call.Arg<Folder>()));

        var result = await FolderHandler().DeleteAsync(root.Id, confirmed: true);

        result.Succeeded.Should().BeTrue();
        removed.Should().ContainInOrder(child, root);
    }

    [Fact]
    public async Task ExcluirPasta_Padrao_Recusa()
    {
        var trash = Folder.Create(AccountId, "Lixeira", FolderType.Trash, Now, remotePath: "Trash");
        ArrangeFolders(trash);

        var result = await FolderHandler().DeleteAsync(trash.Id, confirmed: true);

        result.Succeeded.Should().BeFalse();
    }

    // ----- Spam -----------------------------------------------------------------------

    private (Message Message, Folder Junk, Folder Inbox) ArrangeSpamScenario()
    {
        var inbox = Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        var junk = Folder.Create(AccountId, "Spam", FolderType.Junk, Now, remotePath: "Junk");

        var message = Message.Create(AccountId, inbox.Id, "<1@servidor>", Now, Now, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _folders.GetByIdAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(inbox);
        _folders.GetByIdAsync(junk.Id, Arg.Any<CancellationToken>()).Returns(junk);
        _folders.GetByTypeAsync(AccountId, FolderType.Junk, Arg.Any<CancellationToken>()).Returns(junk);
        _folders.GetByTypeAsync(AccountId, FolderType.Inbox, Arg.Any<CancellationToken>()).Returns(inbox);

        return (message, junk, inbox);
    }

    [Fact]
    public async Task MarcarComoSpam_MoveEEnfileiraAPalavraChave()
    {
        // As duas metades juntas: mover é a visível, a palavra-chave é a que treina o
        // filtro do servidor. Só mover deixa o servidor errando para sempre.
        var (message, junk, _) = ArrangeSpamScenario();

        var result = await SpamHandler().HandleAsync(message.Id, isSpam: true);

        result.Succeeded.Should().BeTrue();
        message.FolderId.Should().Be(junk.Id);

        var flagOperation = _enqueued.Single(o => o.OperationType == OutboxOperationType.SetFlag);
        JsonSerializer.Deserialize<FlagChangePayload>(flagOperation.PayloadJson).Junk.Should().BeTrue();

        _enqueued.Should().Contain(o => o.OperationType == OutboxOperationType.MoveMessage);
    }

    [Fact]
    public async Task MarcarComoSpam_APalavraChaveEEnfileiradaAntesDaMovimentacao()
    {
        // A fila é sequencial, e o marcador precisa ser aplicado enquanto o servidor ainda
        // encontra a mensagem na pasta atual — depois do MOVE, o UID antigo aponta para nada.
        var (message, _, _) = ArrangeSpamScenario();

        await SpamHandler().HandleAsync(message.Id, isSpam: true);

        var flagIndex = _enqueued.FindIndex(o => o.OperationType == OutboxOperationType.SetFlag);
        var moveIndex = _enqueued.FindIndex(o => o.OperationType == OutboxOperationType.MoveMessage);

        flagIndex.Should().BeLessThan(moveIndex);
    }

    [Fact]
    public async Task NaoESpam_DevolveParaACaixaDeEntradaComNotJunk()
    {
        var (message, junk, inbox) = ArrangeSpamScenario();
        message.MoveTo(junk.Id, Now);

        var result = await SpamHandler().HandleAsync(message.Id, isSpam: false);

        result.Succeeded.Should().BeTrue();
        message.FolderId.Should().Be(inbox.Id);

        var flagOperation = _enqueued.Single(o => o.OperationType == OutboxOperationType.SetFlag);
        JsonSerializer.Deserialize<FlagChangePayload>(flagOperation.PayloadJson).Junk.Should().BeFalse();
    }

    [Fact]
    public async Task NaoESpam_CaixaDeEntradaRestritaEMensagemIncompativel_VaiParaPendencias()
    {
        // A movimentação passa pelo MoveMessageHandler como qualquer outra: se a Caixa de
        // Entrada é restrita e a mensagem não pertence, pendências é o destino certo.
        var directory = DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"), Now, invalidEmailAction: InvalidEmailAction.MoveToPending);

        var (message, junk, inbox) = ArrangeSpamScenario();
        message.MoveTo(junk.Id, Now);

        inbox.SetExplicitRestriction(directory.Id, Now);
        inbox.ApplyEffectiveRestriction(null, Now);

        var pending = Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);
        _folders.GetByTypeAsync(AccountId, FolderType.Pending, Arg.Any<CancellationToken>()).Returns(pending);

        _messages.GetParticipantsAsync(message.Id, Arg.Any<CancellationToken>())
            .Returns(new[] { new MessageParticipant(AddressKind.From, EmailDomain.Parse("externo.com")) });

        var result = await SpamHandler().HandleAsync(message.Id, isSpam: false);

        result.Succeeded.Should().BeTrue();
        message.FolderId.Should().Be(pending.Id);
    }

    [Fact]
    public async Task MarcarComoSpam_SemPastaDeLixoEletronico_ExplicaARecusa()
    {
        var inbox = Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        var message = Message.Create(AccountId, inbox.Id, "<1@servidor>", Now, Now, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        var result = await SpamHandler().HandleAsync(message.Id, isSpam: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("lixo eletrônico");
    }
}
