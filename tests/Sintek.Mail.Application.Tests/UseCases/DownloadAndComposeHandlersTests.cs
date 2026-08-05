using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>Cofre de anexos em memória, que registra o que gravou.</summary>
internal sealed class InMemoryAttachmentStore : IAttachmentStore
{
    public List<(Guid MessageId, Guid AttachmentId, string FileName)> Saved { get; } = [];

    /// <summary>Anexos cujo arquivo foi apagado, na ordem.</summary>
    public List<Guid> Deleted { get; } = [];

    public Task<string> SaveAsync(
        Guid messageId, Guid attachmentId, string fileName, Stream content,
        CancellationToken cancellationToken = default)
    {
        Saved.Add((messageId, attachmentId, fileName));
        return Task.FromResult($"/anexos/{messageId:N}/{attachmentId:N}");
    }

    public Task DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        Deleted.Add(attachmentId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cobre o download sob demanda de corpo e anexos e a gravação/envio pelo compositor.
/// </summary>
public class DownloadAndComposeHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly IHtmlSanitizer _sanitizer = Substitute.For<IHtmlSanitizer>();
    private readonly InMemoryAttachmentStore _store = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public DownloadAndComposeHandlersTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _sanitizer.Sanitize(Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(call => new SanitizedHtmlResult(
                call.Arg<string?>() ?? string.Empty, HasRemoteContent: false, RemovedRemoteReferences: 0));
    }

    private DownloadMessageContentHandler DownloadHandler() => new(
        _messages, _folders, _unitOfWork, _imap, _sanitizer, _store, _clock,
        NullLogger<DownloadMessageContentHandler>.Instance);

    private ComposeMessageHandler ComposeHandler() => TestFactories.Compose(
        _messages, _folders, _accounts, _unitOfWork,
        new OutboxEnqueuer(_outbox, _clock), _clock);

    private (Message Message, Folder Folder) ArrangeSyncedMessage()
    {
        var folder = Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        var message = Message.Create(AccountId, folder.Id, "<1@servidor>", Now, Now, Now);
        message.SetRemoteIdentity(42, null, Now);

        _messages.GetWithParticipantsAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _folders.GetByIdAsync(folder.Id, Arg.Any<CancellationToken>()).Returns(folder);

        return (message, folder);
    }

    // ----- Download de corpo ----------------------------------------------------------

    [Fact]
    public async Task BaixarCorpo_MensagemSemCorpo_BuscaHigienizaEGrava()
    {
        var (message, _) = ArrangeSyncedMessage();

        _imap.FetchBodyAsync("INBOX", 42, Arg.Any<CancellationToken>())
            .Returns(new FetchedBody { HtmlBody = "<p>Olá</p>", TextBody = "Olá" });

        var result = await DownloadHandler().DownloadBodyAsync(message.Id);

        result.Succeeded.Should().BeTrue();
        message.Body.Should().NotBeNull();
        message.Body!.HtmlBody.Should().Be("<p>Olá</p>");
        message.Body.SanitizedHtml.Should().Be("<p>Olá</p>");

        _sanitizer.Received(1).Sanitize("<p>Olá</p>", false);
    }

    [Fact]
    public async Task BaixarCorpo_CorpoJaPresente_NaoTocaNaRede()
    {
        // Idempotência é o que torna seguro chamar o download em todo clique de mensagem.
        var (message, _) = ArrangeSyncedMessage();

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent("<p>Já baixado</p>", "Já baixado", "<p>Já baixado</p>", false, Now);
        message.SetBody(body, Now);

        var result = await DownloadHandler().DownloadBodyAsync(message.Id);

        result.Succeeded.Should().BeTrue();
        await _imap.DidNotReceive()
            .FetchBodyAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BaixarCorpo_MensagemLocalSemServidor_ExplicaEmVezDeFalhar()
    {
        var pending = Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);
        var message = Message.Create(AccountId, pending.Id, "<local@sintek>", Now, Now, Now);

        _messages.GetWithParticipantsAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        _folders.GetByIdAsync(pending.Id, Arg.Any<CancellationToken>()).Returns(pending);

        var result = await DownloadHandler().DownloadBodyAsync(message.Id);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("servidor");
    }

    [Fact]
    public async Task BaixarCorpo_SegundaTentativaAposFalhaParcial_NaoDuplicaAnexos()
    {
        var (message, _) = ArrangeSyncedMessage();

        message.AddAttachment(Attachment.Create(
            message.Id, "contrato.pdf", "application/pdf", 1024, "2", Now));

        _imap.FetchBodyAsync("INBOX", 42, Arg.Any<CancellationToken>())
            .Returns(new FetchedBody
            {
                TextBody = "corpo",
                Attachments = [new FetchedAttachment("contrato.pdf", "application/pdf", 1024, "2", null, false)],
            });

        await DownloadHandler().DownloadBodyAsync(message.Id);

        message.Attachments.Should().ContainSingle();
    }

    // ----- Download de anexo ----------------------------------------------------------

    [Fact]
    public async Task BaixarAnexo_ConteudoDisponivel_GravaNoDiscoEMarca()
    {
        var (message, _) = ArrangeSyncedMessage();

        var attachment = Attachment.Create(message.Id, "contrato.pdf", "application/pdf", 4, "2", Now);
        message.AddAttachment(attachment);

        _imap.FetchAttachmentAsync("INBOX", 42, "2", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream([1, 2, 3, 4]));

        var result = await DownloadHandler().DownloadAttachmentAsync(message.Id, attachment.Id);

        result.Succeeded.Should().BeTrue();
        attachment.IsDownloaded.Should().BeTrue();
        attachment.StoragePath.Should().NotBeNullOrWhiteSpace();
        _store.Saved.Should().ContainSingle();
    }

    [Fact]
    public async Task BaixarAnexo_JaBaixado_NaoBaixaDeNovo()
    {
        var (message, _) = ArrangeSyncedMessage();

        var attachment = Attachment.Create(message.Id, "contrato.pdf", "application/pdf", 4, "2", Now);
        attachment.MarkDownloaded("/anexos/existente.pdf", Now);
        message.AddAttachment(attachment);

        var result = await DownloadHandler().DownloadAttachmentAsync(message.Id, attachment.Id);

        result.Succeeded.Should().BeTrue();
        _store.Saved.Should().BeEmpty();
    }

    // ----- Compositor -----------------------------------------------------------------

    private Account ArrangeAccount(out Folder drafts, out Folder outboxFolder)
    {
        var account = Account.Create(
            Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        drafts = Folder.Create(account.Id, "Rascunhos", FolderType.Drafts, Now, remotePath: "Drafts");
        outboxFolder = Folder.Create(account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true);

        _accounts.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _folders.GetByTypeAsync(account.Id, FolderType.Drafts, Arg.Any<CancellationToken>()).Returns(drafts);
        _folders.GetByTypeAsync(account.Id, FolderType.Outbox, Arg.Any<CancellationToken>()).Returns(outboxFolder);

        return account;
    }

    private static DraftRecipient To(string address)
        => new(AddressKind.To, EmailAddress.Parse(address), null);

    [Fact]
    public async Task Enviar_SemDestinatario_RecusaAntesDeGravar()
    {
        var account = ArrangeAccount(out _, out _);

        var result = await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Sem ninguém",
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("destinatário");
        await _messages.DidNotReceive().AddAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enviar_MensagemValida_GravaNaCaixaDeSaidaEEnfileiraOEnvio()
    {
        var account = ArrangeAccount(out _, out var outboxFolder);

        Message? stored = null;
        await _messages.AddAsync(Arg.Do<Message>(m => stored = m), Arg.Any<CancellationToken>());

        var result = await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Proposta",
            TextBody = "Segue a proposta discutida.",
            Recipients = [To("cliente@externo.com")],
        });

        result.Succeeded.Should().BeTrue();
        stored!.FolderId.Should().Be(outboxFolder.Id);
        stored.FromAddress!.Value.Should().Be("contato@sintek.com.br");

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GravarRascunho_MensagemNova_VaiParaRascunhosEEnfileiraOAppend()
    {
        var account = ArrangeAccount(out var drafts, out _);

        Message? stored = null;
        await _messages.AddAsync(Arg.Do<Message>(m => stored = m), Arg.Any<CancellationToken>());

        var result = await ComposeHandler().SaveDraftAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Rascunho",
            TextBody = "Ainda escrevendo…",
        });

        result.Succeeded.Should().BeTrue();
        stored!.FolderId.Should().Be(drafts.Id);
        stored.IsDraft.Should().BeTrue();

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.AppendDraft),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GravarRascunho_Reedicao_SubstituiDestinatariosEmVezDeSomar()
    {
        var account = ArrangeAccount(out var drafts, out _);

        var existing = Message.Create(account.Id, drafts.Id, "<rascunho@local>", Now, Now, Now);
        existing.AddAddress(MessageAddress.Create(
            existing.Id, AddressKind.To, EmailAddress.Parse("antigo@externo.com"), Now));

        _messages.GetWithParticipantsAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        await ComposeHandler().SaveDraftAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            DraftId = existing.Id,
            Subject = "Rascunho",
            Recipients = [To("novo@externo.com")],
        });

        existing.Addresses.Should().ContainSingle()
            .Which.Address.Value.Should().Be("novo@externo.com");
    }

    [Fact]
    public async Task Enviar_ComAnexoLocal_MarcaOAnexoComoDisponivel()
    {
        // O montador de envio recusa anexo não baixado; o arquivo escolhido no compositor
        // já está no disco e precisa nascer utilizável.
        var account = ArrangeAccount(out _, out _);

        Message? stored = null;
        await _messages.AddAsync(Arg.Do<Message>(m => stored = m), Arg.Any<CancellationToken>());

        await ComposeHandler().SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Com anexo",
            Recipients = [To("cliente@externo.com")],
            Attachments = [new ComposedAttachment("contrato.pdf", "/docs/contrato.pdf", "application/pdf", 2048)],
        });

        var attachment = stored!.Attachments.Single();
        attachment.IsDownloaded.Should().BeTrue();
        attachment.StoragePath.Should().Be("/docs/contrato.pdf");
    }

    /// <summary>
    /// Compositor com um histórico de destinatários de verdade, para verificar o que ele
    /// grava no envio.
    /// </summary>
    private ComposeMessageHandler ComposeHandlerWith(
        InMemoryRecipientHistoryRepository history)
        => new(
            _messages, _folders, _accounts, _unitOfWork,
            new OutboxEnqueuer(_outbox, _clock),
            new Sintek.Mail.Application.UseCases.Contacts.RecipientHistoryHandler(
                history,
                Substitute.For<IContactRepository>(),
                _accounts,
                Substitute.For<IDomainDirectoryRepository>(),
                _unitOfWork,
                _clock,
                NullLogger<Sintek.Mail.Application.UseCases.Contacts.RecipientHistoryHandler>.Instance),
            _clock,
            NullLogger<ComposeMessageHandler>.Instance);

    [Fact]
    public async Task Enviar_MensagemValida_RegistraOsDestinatariosNoHistorico()
    {
        var account = ArrangeAccount(out _, out _);
        var history = new InMemoryRecipientHistoryRepository();

        await ComposeHandlerWith(history).SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Proposta",
            Recipients =
            [
                To("cliente@externo.com"),
                new DraftRecipient(AddressKind.Cc, EmailAddress.Parse("copia@externo.com"), null),
            ],
        });

        history.Entries.Select(e => e.Address.Value)
            .Should().BeEquivalentTo(["cliente@externo.com", "copia@externo.com"]);
    }

    [Fact]
    public async Task GravarRascunho_ComDestinatarios_NaoAlimentaOHistorico()
    {
        // Rascunho abandonado não é intenção de escrever para ninguém. Registrar aqui
        // encheria o autocompletar de endereços que o usuário desistiu de usar.
        var account = ArrangeAccount(out _, out _);
        var history = new InMemoryRecipientHistoryRepository();

        await ComposeHandlerWith(history).SaveDraftAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Rascunho",
            Recipients = [To("cliente@externo.com")],
        });

        history.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Enviar_SemDestinatario_NaoTocaNoHistorico()
    {
        var account = ArrangeAccount(out _, out _);
        var history = new InMemoryRecipientHistoryRepository();

        await ComposeHandlerWith(history).SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = "Sem ninguém",
            Recipients = [],
        });

        history.Entries.Should().BeEmpty();
    }
}
