using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Maintenance;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre o acabamento da fase 9: envio agendado, confirmação de leitura, marcadores
/// rápidos e limpeza de cache.
/// </summary>
public class FinishingTouchesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory;
    private readonly Account _account;
    private readonly Folder _inbox;

    public FinishingTouchesTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
        _inbox = Folder.Create(_account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _folders.GetByTypeAsync(_account.Id, FolderType.Outbox, Arg.Any<CancellationToken>())
            .Returns(Folder.Create(_account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true));
    }

    private OutboxEnqueuer Enqueuer() => new(_outbox, _clock);

    private MoveMessageHandler MoveHandler() => new(
        _messages, _folders, _directories, _audit, _unitOfWork,
        Enqueuer(), _clock, NullLogger<MoveMessageHandler>.Instance);

    private ComposeMessageHandler ComposeHandler() => TestFactories.Compose(
        _messages, _folders, _accounts, _unitOfWork, Enqueuer(), _clock);

    private Message CreateMessage(bool readReceiptRequested = false)
    {
        var message = Message.Create(_account.Id, _inbox.Id, "<msg@ext>", Now, Now, Now);
        message.SetHeaders("Proposta", EmailAddress.Parse("cliente@externo.com"), "Cliente", null, null, Now);
        message.SetContentMetadata(
            "prévia", 1024, false, MessageImportance.Normal, readReceiptRequested, Now);

        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _messages.GetWithParticipantsAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        return message;
    }

    // ----- Envio agendado --------------------------------------------------------------

    [Fact]
    public async Task EnvioAgendado_ParaOFuturo_EnfileiraComADataDeElegibilidade()
    {
        OutboxOperation? enqueued = null;
        await _outbox.AddAsync(Arg.Do<OutboxOperation>(o => enqueued = o), Arg.Any<CancellationToken>());

        var sendAt = Now.AddHours(8);

        var result = await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = _account.Id,
            Subject = "Bom dia",
            TextBody = "Segue conforme combinado.",
            Recipients = [new DraftRecipient(AddressKind.To, EmailAddress.Parse("cliente@externo.com"), null)],
            ScheduledSendAt = sendAt,
        });

        result.Succeeded.Should().BeTrue();

        // A fila já sabe respeitar NextAttemptAt: agendar é enfileirar com a data certa,
        // não criar um segundo mecanismo de espera.
        enqueued.Should().NotBeNull();
        enqueued!.NextAttemptAt.Should().Be(sendAt);
        enqueued.IsReady(Now).Should().BeFalse();
        enqueued.IsReady(sendAt).Should().BeTrue();
    }

    [Fact]
    public async Task EnvioAgendado_NoPassado_ERecusadoComTextoUtil()
    {
        var result = await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = _account.Id,
            Subject = "Atrasado",
            Recipients = [new DraftRecipient(AddressKind.To, EmailAddress.Parse("cliente@externo.com"), null)],
            ScheduledSendAt = Now.AddMinutes(-1),
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("futuro");
        await _outbox.DidNotReceive().AddAsync(Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvioSemAgendamento_ContinuaElegivelImediatamente()
    {
        OutboxOperation? enqueued = null;
        await _outbox.AddAsync(Arg.Do<OutboxOperation>(o => enqueued = o), Arg.Any<CancellationToken>());

        await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = _account.Id,
            Subject = "Agora",
            Recipients = [new DraftRecipient(AddressKind.To, EmailAddress.Parse("cliente@externo.com"), null)],
        });

        enqueued!.IsReady(Now).Should().BeTrue();
    }

    // ----- Confirmação de leitura ------------------------------------------------------

    [Fact]
    public async Task ConfirmacaoDeLeitura_Enviada_MarcaComoDecididaEEncadeia()
    {
        var message = CreateMessage(readReceiptRequested: true);

        var handler = new ReadReceiptHandler(
            _messages, _accounts, ComposeHandler(), _unitOfWork, _clock,
            NullLogger<ReadReceiptHandler>.Instance);

        var result = await handler.SendAsync(message.Id);

        result.Succeeded.Should().BeTrue();
        message.ReadReceiptHandled.Should().BeTrue();

        await _outbox.Received().AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmacaoDeLeitura_Recusada_TambemFicaDecidida()
    {
        var message = CreateMessage(readReceiptRequested: true);

        var handler = new ReadReceiptHandler(
            _messages, _accounts, ComposeHandler(), _unitOfWork, _clock,
            NullLogger<ReadReceiptHandler>.Instance);

        (await handler.DeclineAsync(message.Id)).Should().BeTrue();

        // Recusar é decisão: perguntar de novo trataria o "não" como um "ainda não".
        message.ReadReceiptHandled.Should().BeTrue();

        await _outbox.DidNotReceive().AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());
    }

    // ----- Marcadores rápidos ----------------------------------------------------------

    [Fact]
    public async Task MarcarComoLida_GravaEEnfileiraNaMesmaTransacao()
    {
        var message = CreateMessage();

        var handler = new MessageFlagsHandler(
            _messages, _folders, _unitOfWork, Enqueuer(), MoveHandler(), _clock,
            NullLogger<MessageFlagsHandler>.Instance);

        (await handler.SetReadAsync(message.Id, isRead: true)).Should().BeTrue();

        message.IsRead.Should().BeTrue();
        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.MarkAsRead),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarcarComoLida_JaLida_NaoEnfileiraNada()
    {
        var message = CreateMessage();
        message.SetRead(true, Now);

        var handler = new MessageFlagsHandler(
            _messages, _folders, _unitOfWork, Enqueuer(), MoveHandler(), _clock,
            NullLogger<MessageFlagsHandler>.Instance);

        (await handler.SetReadAsync(message.Id, isRead: true)).Should().BeFalse();

        await _outbox.DidNotReceive().AddAsync(Arg.Any<OutboxOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoverParaLixeira_SemPastaDeLixeira_ExplicaEmVezDeLancar()
    {
        var message = CreateMessage();

        var handler = new MessageFlagsHandler(
            _messages, _folders, _unitOfWork, Enqueuer(), MoveHandler(), _clock,
            NullLogger<MessageFlagsHandler>.Instance);

        var result = await handler.MoveToTrashAsync(message.Id);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("lixeira");
    }

    [Fact]
    public async Task MoverParaLixeira_ComPasta_PassaPeloMoveMessageHandler()
    {
        var message = CreateMessage();
        var trash = Folder.Create(_account.Id, "Lixeira", FolderType.Trash, Now, remotePath: "Trash");

        _folders.GetByTypeAsync(_account.Id, FolderType.Trash, Arg.Any<CancellationToken>()).Returns(trash);
        _folders.GetByIdAsync(trash.Id, Arg.Any<CancellationToken>()).Returns(trash);

        var handler = new MessageFlagsHandler(
            _messages, _folders, _unitOfWork, Enqueuer(), MoveHandler(), _clock,
            NullLogger<MessageFlagsHandler>.Instance);

        var result = await handler.MoveToTrashAsync(message.Id);

        result.Succeeded.Should().BeTrue();
        message.FolderId.Should().Be(trash.Id);
    }

    // ----- Limpeza de cache ------------------------------------------------------------

    [Fact]
    public async Task LimpezaDeCache_DescartaCorpoEAnexoMasPreservaOsMetadados()
    {
        var message = CreateMessage();
        message.SetRemoteIdentity(42, null, Now);

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent("<p>html</p>", "texto", "<p>html</p>", false, Now.AddDays(-40));
        message.SetBody(body, Now);

        var attachment = Attachment.Create(
            message.Id, "contrato.pdf", "application/pdf", 2048, "2", Now);
        attachment.MarkDownloaded("/anexos/contrato.pdf", Now.AddDays(-40));
        message.AddAttachment(attachment);

        _messages.ListCachedContentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        var store = new InMemoryAttachmentStore();
        var handler = new CacheMaintenanceHandler(
            _messages, store, _unitOfWork, _clock, NullLogger<CacheMaintenanceHandler>.Instance);

        var impact = await handler.AnalyzeAsync(TimeSpan.FromDays(30));
        impact.BodyCount.Should().Be(1);
        impact.AttachmentCount.Should().Be(1);
        impact.HasAnything.Should().BeTrue();

        await handler.CleanAsync(TimeSpan.FromDays(30));

        // O conteúdo sai; os metadados ficam, e a mensagem volta a ser baixável.
        message.Body!.DownloadedAt.Should().BeNull();
        message.Body.TextBody.Should().BeNull();
        attachment.IsDownloaded.Should().BeFalse();
        attachment.FileName.Should().Be("contrato.pdf");
        store.Deleted.Should().Contain(attachment.Id);
    }

    [Fact]
    public async Task LimpezaDeCache_PreservaAAutorizacaoDeConteudoRemoto()
    {
        var message = CreateMessage();
        message.SetRemoteIdentity(42, null, Now);

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent("<p>html</p>", "texto", "<p>html</p>", true, Now.AddDays(-40));
        body.AllowRemoteContent(Now.AddDays(-39));
        message.SetBody(body, Now);

        _messages.ListCachedContentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        var handler = new CacheMaintenanceHandler(
            _messages, new InMemoryAttachmentStore(), _unitOfWork, _clock,
            NullLogger<CacheMaintenanceHandler>.Instance);

        await handler.CleanAsync(TimeSpan.FromDays(30));

        // A autorização é decisão sobre o remetente, não pedaço do cache: refazê-la a cada
        // limpeza seria pedir a mesma permissão duas vezes.
        message.Body!.RemoteContentAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task LimpezaDeCache_SemNadaParaLimpar_InformaSemAgir()
    {
        _messages.ListCachedContentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var handler = new CacheMaintenanceHandler(
            _messages, new InMemoryAttachmentStore(), _unitOfWork, _clock,
            NullLogger<CacheMaintenanceHandler>.Instance);

        var impact = await handler.AnalyzeAsync(TimeSpan.FromDays(30));

        impact.HasAnything.Should().BeFalse();
        impact.Summary.Should().Contain("Não há conteúdo");
    }
}
