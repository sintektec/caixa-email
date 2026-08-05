using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Assistant;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre os recursos de IA sobre a mensagem: o que é enviado, o que é cortado e o que
/// acontece quando o conteúdo não está disponível.
/// </summary>
public class AssistantFeaturesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly IHtmlSanitizer _sanitizer = Substitute.For<IHtmlSanitizer>();
    private readonly IAttachmentStore _attachments = Substitute.For<IAttachmentStore>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory;
    private readonly Account _account;
    private readonly Folder _inbox;
    private readonly Message _message;

    private readonly RecordingProvider _provider = new();

    public AssistantFeaturesHandlerTests()
    {
        _directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
        _inbox = Folder.Create(_account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        _message = Message.Create(_account.Id, _inbox.Id, "<msg@ext>", Now, Now, Now);
        _message.SetHeaders("Proposta comercial", EmailAddress.Parse("cliente@externo.com"), "Cliente",
            null, null, Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
        _messages.GetWithParticipantsAsync(_message.Id, Arg.Any<CancellationToken>()).Returns(_message);
        _folders.GetByIdAsync(_inbox.Id, Arg.Any<CancellationToken>()).Returns(_inbox);
    }

    /// <summary>Provedor local que guarda o que recebeu.</summary>
    private sealed class RecordingProvider : IAssistantProvider
    {
        public string Id => "local";

        public string DisplayName => "Modelo local";

        public AssistantLocality Locality => AssistantLocality.Local;

        public string? LastContent { get; private set; }

        public AssistantTask? LastTask { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<AssistantResponse> CompleteAsync(
            AssistantRequest request, CancellationToken cancellationToken = default)
        {
            LastContent = request.Content;
            LastTask = request.Task;
            return Task.FromResult(AssistantResponse.Success("resumo"));
        }
    }

    private AssistantFeaturesHandler CreateHandler() => new(
        new AssistantGateway(
            [_provider], _accounts, _directories, _audit, _unitOfWork, _clock,
            NullLogger<AssistantGateway>.Instance),
        _messages,
        TestFactories.Download(
            _messages, _folders, _unitOfWork, _imap, _sanitizer, _attachments, _clock));

    private void ArrangeDownloadedBody(string text)
    {
        var body = MessageBody.Create(_message.Id, Now);
        body.SetContent("<p>html</p>", text, "<p>html</p>", false, Now);
        _message.SetBody(body, Now);
    }

    [Fact]
    public async Task Resumir_EnviaAssuntoECorpoEmTexto()
    {
        ArrangeDownloadedBody("Segue a proposta com os valores revisados.");

        var result = await CreateHandler().SummarizeMessageAsync(_message.Id);

        result.Succeeded.Should().BeTrue();
        _provider.LastTask.Should().Be(AssistantTask.Summarize);
        _provider.LastContent.Should().Contain("Proposta comercial");
        _provider.LastContent.Should().Contain("valores revisados");

        // HTML não ajuda o modelo e infla o que sai da máquina.
        _provider.LastContent.Should().NotContain("<p>");
    }

    [Fact]
    public async Task Resumir_CorpoEnorme_EhCortadoAntesDeSair()
    {
        ArrangeDownloadedBody(new string('a', AssistantFeaturesHandler.MaxContentLength * 2));

        await CreateHandler().SummarizeMessageAsync(_message.Id);

        _provider.LastContent!.Length.Should().Be(AssistantFeaturesHandler.MaxContentLength);
    }

    [Fact]
    public async Task Resumir_CorpoAindaNaoBaixado_TentaBaixarEExplicaSeNaoConseguir()
    {
        // Sem UID não há de onde baixar; resumir a prévia não resumiria nada.
        var result = await CreateHandler().SummarizeMessageAsync(_message.Id);

        result.Succeeded.Should().BeFalse();
        result.UserMessage.Should().NotBeNullOrWhiteSpace();
        _provider.LastContent.Should().BeNull("nada pode ter sido enviado sem conteúdo");
    }

    [Fact]
    public async Task SugerirResposta_UsaATarefaCorreta()
    {
        ArrangeDownloadedBody("Podemos fechar na sexta?");

        await CreateHandler().SuggestReplyAsync(_message.Id);

        _provider.LastTask.Should().Be(AssistantTask.SuggestReply);
    }

    [Fact]
    public async Task Reescrever_TextoVazio_NaoChamaOProvedor()
    {
        var result = await CreateHandler().RewriteAsync(_account.Id, "   ");

        result.Succeeded.Should().BeFalse();
        _provider.LastContent.Should().BeNull();
    }

    [Fact]
    public async Task Reescrever_ComTexto_EnviaOQueEstaNoCompositor()
    {
        var result = await CreateHandler().RewriteAsync(_account.Id, "segue anexo obrigado");

        result.Succeeded.Should().BeTrue();
        _provider.LastTask.Should().Be(AssistantTask.Rewrite);
        _provider.LastContent.Should().Be("segue anexo obrigado");
    }

    [Fact]
    public async Task Resumir_MensagemInexistente_ExplicaSemLancar()
    {
        _messages.GetWithParticipantsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var result = await CreateHandler().SummarizeMessageAsync(Guid.CreateVersion7());

        result.Succeeded.Should().BeFalse();
        result.UserMessage.Should().Contain("não existe");
    }
}
