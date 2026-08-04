using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a imposição da regra de Diretório de Domínio na movimentação de mensagens —
/// o caminho por onde passa também o arrastar e soltar da interface.
/// </summary>
public class MoveMessageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();

    public MoveMessageHandlerTests()
    {
        // A transação é executada de verdade: o teste precisa observar os efeitos do
        // corpo, não apenas que ele foi agendado.
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
    }

    private MoveMessageHandler CreateHandler()
    {
        var timeProvider = new FakeTimeProvider(Now);

        return new MoveMessageHandler(
            _messages,
            _folders,
            _directories,
            _audit,
            _unitOfWork,
            new OutboxEnqueuer(_outbox, timeProvider),
            timeProvider,
            NullLogger<MoveMessageHandler>.Instance);
    }

    private static DomainDirectory Directory(InvalidEmailAction action)
        => DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"),
            Now,
            validationMode: DomainValidationMode.AnyParticipant,
            invalidEmailAction: action);

    private Message ArrangeScenario(
        InvalidEmailAction action,
        string senderAddress,
        bool withPendingFolder,
        out Folder restrictedFolder,
        out Folder? pendingFolder)
    {
        var directory = Directory(action);

        var inbox = Folder.Create(AccountId, "Caixa de Entrada", FolderType.Inbox, Now);
        restrictedFolder = Folder.Create(AccountId, "Clientes Sintek", FolderType.Custom, Now);
        restrictedFolder.SetExplicitRestriction(directory.Id, Now);
        restrictedFolder.ApplyEffectiveRestriction(null, Now);

        pendingFolder = withPendingFolder
            ? Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true)
            : null;

        var message = Message.Create(AccountId, inbox.Id, "<id@teste>", Now, Now, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _messages.GetParticipantsAsync(message.Id, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<MessageParticipant>>(_ =>
            [
                new MessageParticipant(AddressKind.From, EmailAddress.Parse(senderAddress).Domain),
            ]);

        _folders.GetByIdAsync(restrictedFolder.Id, Arg.Any<CancellationToken>()).Returns(restrictedFolder);
        _folders.GetByTypeAsync(AccountId, FolderType.Pending, Arg.Any<CancellationToken>()).Returns(pendingFolder);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        return message;
    }

    [Fact]
    public async Task Move_Permite_QuandoAMensagemPertenceAoDominio()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.Block, "contato@sintek.com.br", true, out var folder, out _);

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.Moved);
        message.FolderId.Should().Be(folder.Id);
        message.SyncState.Should().Be(MessageSyncState.PendingMove);
    }

    [Fact]
    public async Task Move_Bloqueia_ComAMensagemExigidaPelaEspecificacao()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.Block, "externo@outro.com", true, out var folder, out _);
        var originalFolderId = message.FolderId;

        var act = () => CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        (await act.Should().ThrowAsync<FolderDomainRestrictionException>())
            .WithMessage(
                "Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local.");

        message.FolderId.Should().Be(originalFolderId, "a mensagem não pode sair do lugar quando é bloqueada");
    }

    [Fact]
    public async Task Move_Bloqueado_RegistraAuditoria()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.Block, "externo@outro.com", true, out var folder, out _);

        var act = () => CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));
        await act.Should().ThrowAsync<FolderDomainRestrictionException>();

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.MessageMoveBlockedByDomainRule),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_PedeConfirmacao_QuandoConfiguradoParaAlertar()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.WarnAndConfirm, "externo@outro.com", true, out var folder, out _);
        var originalFolderId = message.FolderId;

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.RequiresConfirmation);
        message.FolderId.Should().Be(originalFolderId, "nada pode mudar antes da confirmação");
    }

    [Fact]
    public async Task Move_Conclui_QuandoOUsuarioConfirma()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.WarnAndConfirm, "externo@outro.com", true, out var folder, out _);

        var result = await CreateHandler().HandleAsync(
            new MoveMessageCommand(message.Id, folder.Id, UserConfirmed: true));

        result.Outcome.Should().Be(MoveMessageOutcome.Moved);
        message.FolderId.Should().Be(folder.Id);

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.MessageMoveOverridden),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_DesviaParaPendencias_QuandoConfigurado()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.MoveToPending, "externo@outro.com", true, out var folder, out var pending);

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.MovedToPending);
        message.FolderId.Should().Be(pending!.Id);
        message.FolderId.Should().NotBe(folder.Id, "a pasta restrita é justamente o que a regra protege");
    }

    [Fact]
    public async Task Move_Bloqueia_QuandoNaoHaPastaDePendencias()
    {
        // Sem pasta de pendências, desviar é impossível. Deixar a mensagem seguir para a
        // pasta restrita seria o pior resultado possível: exatamente o que a regra proíbe.
        var message = ArrangeScenario(
            InvalidEmailAction.MoveToPending, "externo@outro.com", withPendingFolder: false,
            out var folder, out _);

        var act = () => CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        await act.Should().ThrowAsync<FolderDomainRestrictionException>();
        message.FolderId.Should().NotBe(folder.Id);
    }

    [Fact]
    public async Task Move_Permite_MasAudita_QuandoConfiguradoParaApenasRegistrar()
    {
        var message = ArrangeScenario(
            InvalidEmailAction.LogOnly, "externo@outro.com", true, out var folder, out _);

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.Moved);
        message.FolderId.Should().Be(folder.Id);

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.MessageMoveOverridden),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_NaoConsultaRegraDeDominio_QuandoAPastaNaoEhRestrita()
    {
        var inbox = Folder.Create(AccountId, "Caixa de Entrada", FolderType.Inbox, Now);
        var plain = Folder.Create(AccountId, "Arquivo", FolderType.Custom, Now);
        var message = Message.Create(AccountId, inbox.Id, "<id@teste>", Now, Now, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _folders.GetByIdAsync(plain.Id, Arg.Any<CancellationToken>()).Returns(plain);

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, plain.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.Moved);
        await _messages.DidNotReceive().GetParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_EnfileiraOperacaoDeSincronizacao()
    {
        // A gravação local sem enfileiramento deixaria o servidor eternamente
        // desatualizado — é a promessa do modo offline-first que se quebraria.
        var message = ArrangeScenario(
            InvalidEmailAction.Block, "contato@sintek.com.br", true, out var folder, out _);

        await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, folder.Id));

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o =>
                o.OperationType == OutboxOperationType.MoveMessage && o.EntityId == message.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_NaoFazNada_QuandoADestinoEhAPastaAtual()
    {
        var inbox = Folder.Create(AccountId, "Caixa de Entrada", FolderType.Inbox, Now);
        var message = Message.Create(AccountId, inbox.Id, "<id@teste>", Now, Now, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _folders.GetByIdAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(inbox);

        var result = await CreateHandler().HandleAsync(new MoveMessageCommand(message.Id, inbox.Id));

        result.Outcome.Should().Be(MoveMessageOutcome.Moved);
        message.SyncState.Should().Be(MessageSyncState.Synced, "não houve alteração a sincronizar");
    }
}
