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
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o painel de IA: disponibilidade, resultado e a apresentação da recusa por falta
/// de consentimento — que é escolha do usuário, não erro.
/// </summary>
public class AssistantViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory;
    private readonly Account _account;
    private readonly Message _message;

    public AssistantViewModelTests()
    {
        _directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        var inbox = Folder.Create(_account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");
        _message = Message.Create(_account.Id, inbox.Id, "<msg@ext>", Now, Now, Now);
        _message.SetHeaders("Assunto", EmailAddress.Parse("cliente@externo.com"), "Cliente", null, null, Now);

        var body = MessageBody.Create(_message.Id, Now);
        body.SetContent(null, "Texto da mensagem.", null, false, Now);
        _message.SetBody(body, Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
        _messages.GetWithParticipantsAsync(_message.Id, Arg.Any<CancellationToken>()).Returns(_message);
    }

    private sealed class StubProvider(AssistantLocality locality, bool available = true) : IAssistantProvider
    {
        public string Id => locality == AssistantLocality.Local ? "local" : "cloud";

        public string DisplayName => Id;

        public AssistantLocality Locality => locality;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(available);

        public Task<AssistantResponse> CompleteAsync(
            AssistantRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(AssistantResponse.Success("Resumo em tópicos."));
    }

    private AssistantViewModel CreateViewModel(params IAssistantProvider[] providers) => new(
        new TestScopes()
            .With(new AssistantFeaturesHandler(
                new AssistantGateway(
                    providers, _accounts, _directories, _audit, _unitOfWork, _clock,
                    NullLogger<AssistantGateway>.Instance),
                _messages,
                ComposeFactory.Download(
                    _messages, _folders, _unitOfWork,
                    Substitute.For<IImapClient>(),
                    Substitute.For<IHtmlSanitizer>(),
                    Substitute.For<IAttachmentStore>(),
                    _clock)))
            .Build());

    [Fact]
    public async Task Inicializar_SemProvedor_DesligaOsRecursos()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id, _message.Id);

        // A interface consulta isto para decidir se mostra os botões de IA.
        viewModel.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Inicializar_ComModeloLocal_LigaOsRecursos()
    {
        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Local));
        await viewModel.InitializeAsync(_account.Id, _message.Id);

        viewModel.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Resumir_ComModeloLocal_ExibeOResultado()
    {
        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Local));
        await viewModel.InitializeAsync(_account.Id, _message.Id);

        await viewModel.SummarizeAsync();

        viewModel.HasResult.Should().BeTrue();
        viewModel.Result.Should().Be("Resumo em tópicos.");
        viewModel.HasStatusMessage.Should().BeFalse();
    }

    [Fact]
    public async Task Resumir_SoNuvemSemConsentimento_ExplicaOndeAutorizar()
    {
        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Cloud));
        await viewModel.InitializeAsync(_account.Id, _message.Id);

        await viewModel.SummarizeAsync();

        viewModel.HasResult.Should().BeFalse();
        viewModel.HasStatusMessage.Should().BeTrue();

        // Não é erro: é a escolha do usuário, e a tela oferece o caminho para mudá-la.
        viewModel.NeedsCloudConsent.Should().BeTrue();
    }

    [Fact]
    public async Task Resumir_ComConsentimento_VoltaAFuncionar()
    {
        _directory.SetCloudAssistantConsent(true, Now);

        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Cloud));
        await viewModel.InitializeAsync(_account.Id, _message.Id);

        await viewModel.SummarizeAsync();

        viewModel.HasResult.Should().BeTrue();
        viewModel.NeedsCloudConsent.Should().BeFalse();
    }

    [Fact]
    public async Task Reescrever_DevolveOTextoParaOCompositor()
    {
        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Local));
        await viewModel.InitializeAsync(_account.Id);

        var rewritten = await viewModel.RewriteAsync("texto original");

        rewritten.Should().Be("Resumo em tópicos.");
    }

    [Fact]
    public async Task Resumir_SemMensagemCarregada_NaoFazNada()
    {
        var viewModel = CreateViewModel(new StubProvider(AssistantLocality.Local));
        await viewModel.InitializeAsync(_account.Id);

        await viewModel.SummarizeAsync();

        viewModel.HasResult.Should().BeFalse();
    }
}
