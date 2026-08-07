using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o compositor: preparação a partir de mensagem existente, validação de endereço e o
/// aviso de anexo esquecido no fluxo de envio.
/// </summary>
public class ComposerViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

    private readonly Account _account;

    public ComposerViewModelTests()
    {
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
        _contacts.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Contact>());
        _recipientHistory
            .ListForSuggestionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RecipientHistory>());

        _folders.GetByTypeAsync(_account.Id, FolderType.Drafts, Arg.Any<CancellationToken>())
            .Returns(Folder.Create(_account.Id, "Rascunhos", FolderType.Drafts, Now, remotePath: "Drafts"));
        _folders.GetByTypeAsync(_account.Id, FolderType.Outbox, Arg.Any<CancellationToken>())
            .Returns(Folder.Create(_account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true));
    }

    private readonly Sintek.Mail.Application.Abstractions.Persistence.IMessageTemplateRepository _templates =
        Substitute.For<Sintek.Mail.Application.Abstractions.Persistence.IMessageTemplateRepository>();

    private readonly IRecipientHistoryRepository _recipientHistory =
        Substitute.For<IRecipientHistoryRepository>();

    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();

    private readonly IDomainDirectoryRepository _directories =
        Substitute.For<IDomainDirectoryRepository>();

    private ComposerViewModel CreateViewModel(TimeProvider? clock = null)
    {
        _templates.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MessageTemplate>());

        var recipients = ComposeFactory.RecipientHistory(
            _recipientHistory, _contacts, _accounts, _directories, _unitOfWork, _clock);

        return new ComposerViewModel(
            _messages,
            _accounts,
            ComposeFactory.Create(
                _messages, _folders, _accounts, _unitOfWork,
                new OutboxEnqueuer(_outbox, _clock), ComposeFactory.InertRecipientHistory(_unitOfWork, _clock),
                _clock),
            new Sintek.Mail.Application.UseCases.Organization.ManageTemplatesHandler(
                _templates, _unitOfWork, _clock),
            recipients,
            clock ?? _clock);
    }

    /// <summary>Relógio ajustável, para simular a passagem do tempo entre digitações.</summary>
    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    // ----- Rascunho automático ---------------------------------------------------------

    [Fact]
    public async Task RascunhoAutomatico_DigitacaoParada_GravaSozinho()
    {
        var clock = new MovingClock(Now);
        var viewModel = CreateViewModel(clock);
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Subject = "Proposta em andamento";

        clock.Current = Now + ComposerViewModel.AutoSaveQuietPeriod;
        await viewModel.AutoSaveTickAsync();

        viewModel.DraftId.Should().NotBeNull("a digitação parou pelo período de silêncio");
    }

    [Fact]
    public async Task RascunhoAutomatico_AindaDigitando_NaoGrava()
    {
        var clock = new MovingClock(Now);
        var viewModel = CreateViewModel(clock);
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Subject = "Proposta";

        // O tique chega antes do período de silêncio: gravar aqui seria gravar a cada
        // tecla, com um rascunho novo na fila a cada instante.
        clock.Current = Now + ComposerViewModel.AutoSaveQuietPeriod - TimeSpan.FromSeconds(1);
        await viewModel.AutoSaveTickAsync();

        viewModel.DraftId.Should().BeNull();
    }

    [Fact]
    public async Task RascunhoAutomatico_AberturaSemDigitacao_NaoDeixaRascunhoParaTras()
    {
        var clock = new MovingClock(Now);
        var viewModel = CreateViewModel(clock);

        // Abrir uma resposta preenche assunto e corpo — mas preencher não é digitar.
        var source = Message.Create(_account.Id, Guid.CreateVersion7(), "<orig@ext>", Now, Now, Now);
        source.SetHeaders("Proposta", EmailAddress.Parse("cliente@externo.com"), "Cliente", null, null, Now);
        _messages.GetWithParticipantsAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);

        await viewModel.InitializeAsync(_account.Id, DraftKind.Reply, source.Id);

        clock.Current = Now + TimeSpan.FromMinutes(10);
        await viewModel.AutoSaveTickAsync();

        viewModel.DraftId.Should().BeNull();
    }

    [Fact]
    public async Task RascunhoAutomatico_CompositorEsvaziado_NaoCriaRascunhoEmBranco()
    {
        var clock = new MovingClock(Now);
        var viewModel = CreateViewModel(clock);
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Subject = "a";
        viewModel.Subject = string.Empty;

        clock.Current = Now + TimeSpan.FromMinutes(1);
        await viewModel.AutoSaveTickAsync();

        viewModel.DraftId.Should().BeNull();
    }

    [Fact]
    public async Task RascunhoAutomatico_SemNovaDigitacao_NaoRegravaOMesmoRascunho()
    {
        var clock = new MovingClock(Now);
        var viewModel = CreateViewModel(clock);
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Subject = "Proposta";
        clock.Current = Now + TimeSpan.FromMinutes(1);
        await viewModel.AutoSaveTickAsync();

        var firstDraftId = viewModel.DraftId;
        firstDraftId.Should().NotBeNull();

        clock.Current = Now + TimeSpan.FromMinutes(2);
        await viewModel.AutoSaveTickAsync();

        // Sem alteração nova, o segundo tique não grava de novo: uma gravação por pausa,
        // não uma por tique.
        await _unitOfWork.Received(1)
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        viewModel.DraftId.Should().Be(firstDraftId);
    }

    [Fact]
    public async Task Preparar_Resposta_PreencheDestinatarioEAssunto()
    {
        var source = Message.Create(_account.Id, Guid.CreateVersion7(), "<orig@ext>", Now, Now, Now);
        source.SetHeaders("Proposta", EmailAddress.Parse("cliente@externo.com"), "Cliente", null, null, Now);
        _messages.GetWithParticipantsAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id, DraftKind.Reply, source.Id);

        viewModel.To.Should().Be("cliente@externo.com");
        viewModel.Subject.Should().Be("Re: Proposta");
    }

    [Fact]
    public async Task Preparar_ComAssinaturaDaConta_ATrazParaOCorpo()
    {
        _account.SetSignature("Contato — Sintek", Now);

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.BodyText.Should().Contain("Contato — Sintek");
    }

    [Fact]
    public async Task Enviar_EnderecoInvalido_ApontaOTokenErrado()
    {
        // "Há um endereço inválido" obriga a caçar qual; apontar o token resolve na
        // primeira olhada.
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.To = "cliente@externo.com; endereco invalido";

        await viewModel.SendAsync();

        viewModel.StatusMessage.Should().Contain("endereco invalido");
        viewModel.IsCompleted.Should().BeFalse();
        await _messages.DidNotReceive().AddAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enviar_TextoPrometeAnexoSemAnexo_SeguraUmaVezESegueNaSegunda()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.To = "cliente@externo.com";
        viewModel.BodyText = "Segue em anexo a proposta.";

        await viewModel.SendAsync();

        viewModel.ShowForgottenAttachmentWarning.Should().BeTrue();
        viewModel.IsCompleted.Should().BeFalse("o primeiro envio é segurado pelo aviso");

        await viewModel.SendAsync();

        viewModel.IsCompleted.Should().BeTrue("a segunda confirmação envia mesmo sem anexo");
    }

    [Fact]
    public async Task Enviar_AnexoAcrescentadoAposOAviso_LimpaOAvisoEEnvia()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.To = "cliente@externo.com";
        viewModel.BodyText = "Segue em anexo a proposta.";

        await viewModel.SendAsync();
        viewModel.ShowForgottenAttachmentWarning.Should().BeTrue();

        viewModel.AddAttachment("proposta.pdf", "/docs/proposta.pdf", "application/pdf", 1024);
        viewModel.ShowForgottenAttachmentWarning.Should().BeFalse();

        await viewModel.SendAsync();

        viewModel.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Enviar_MensagemValida_ConcluiEEnfileiraOEnvio()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.To = "cliente@externo.com";
        viewModel.Subject = "Proposta";
        viewModel.BodyText = "Conforme conversamos.";

        await viewModel.SendAsync();

        viewModel.IsCompleted.Should().BeTrue();

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GravarRascunho_GuardaOIdentificadorParaAsProximasGravacoes()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);

        viewModel.Subject = "Rascunho";

        await viewModel.SaveDraftAsync();

        viewModel.DraftId.Should().NotBeNull();
        viewModel.IsCompleted.Should().BeFalse("salvar rascunho não fecha o compositor");
    }

    private void ArrangeHistory(params (string Address, string? Name)[] entries)
        => _recipientHistory
            .ListForSuggestionAsync(_account.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(entries
                .Select(e => RecipientHistory.Create(
                    _account.Id, EmailAddress.Parse(e.Address), Now, e.Name))
                .ToList());

    [Theory]
    [InlineData("ana@x.com; jo", "jo")]
    [InlineData("ana@x.com, jo", "jo")]
    [InlineData("jo", "jo")]
    [InlineData("ana@x.com; ", "")]
    public void LastToken_CampoComVariosEnderecos_DevolveOTrechoEmDigitacao(string raw, string esperado)
        => ComposerViewModel.LastToken(raw).Should().Be(esperado);

    [Fact]
    public void ReplaceLastToken_CampoComEnderecoAnterior_PreservaOQueJaEstavaLa()
        => ComposerViewModel.ReplaceLastToken("ana@x.com; jo", "joao@y.com")
            .Should().Be("ana@x.com; joao@y.com; ");

    [Fact]
    public void ReplaceLastToken_CampoComUmUnicoTrecho_TrocaTudo()
        => ComposerViewModel.ReplaceLastToken("jo", "joao@y.com").Should().Be("joao@y.com; ");

    [Fact]
    public async Task Sugestoes_TrechoDigitado_ListaOsCandidatos()
    {
        ArrangeHistory(("ana@cliente.com.br", "Ana Souza"), ("bruno@cliente.com.br", "Bruno"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "ana";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        viewModel.RecipientSuggestions.Should().ContainSingle()
            .Which.Address.Should().Be("ana@cliente.com.br");
        viewModel.HasRecipientSuggestions.Should().BeTrue();
    }

    [Fact]
    public async Task Sugestoes_CampoVazio_NaoAbreALista()
    {
        ArrangeHistory(("ana@cliente.com.br", "Ana Souza"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = string.Empty;

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        viewModel.RecipientSuggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task Sugestoes_EnderecoJaPresenteNoCampo_NaoESugeridoDeNovo()
    {
        ArrangeHistory(("ana@cliente.com.br", "Ana Souza"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "ana@cliente.com.br; ana";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        viewModel.RecipientSuggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task Sugestoes_EnderecoForaDoDominioDaConta_ApareceMarcado()
    {
        // Marcar, não esconder: e-mail externo legítimo é a maior parte do trabalho de quem
        // atende clientes, mas um domínio sósia precisa ser visível antes do envio.
        ArrangeHistory(("cobranca@sintek-com.br", "Cobrança"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "cobranca";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        var sugestao = viewModel.RecipientSuggestions.Should().ContainSingle().Subject;
        sugestao.IsOutsideAccountDomain.Should().BeTrue();
        sugestao.DomainWarning.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Sugestoes_EnderecoDoDominioDaConta_NaoRecebeAviso()
    {
        ArrangeHistory(("colega@sintek.com.br", "Colega"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "colega";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        viewModel.RecipientSuggestions[0].DomainWarning.Should().BeEmpty();
    }

    [Fact]
    public async Task AplicarSugestao_CampoComEnderecoAnterior_AcrescentaSemApagar()
    {
        ArrangeHistory(("bruno@cliente.com.br", "Bruno"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "ana@cliente.com.br; bru";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);
        viewModel.ApplyRecipientSuggestion(RecipientField.To, viewModel.RecipientSuggestions[0]);

        viewModel.To.Should().Be("ana@cliente.com.br; bruno@cliente.com.br; ");
        viewModel.RecipientSuggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task AplicarSugestao_EmCopiaOculta_MexeSoNaqueleCampo()
    {
        ArrangeHistory(("bruno@cliente.com.br", "Bruno"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "ana@cliente.com.br";
        viewModel.Bcc = "bru";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.Bcc);
        viewModel.ApplyRecipientSuggestion(RecipientField.Bcc, viewModel.RecipientSuggestions[0]);

        viewModel.Bcc.Should().Be("bruno@cliente.com.br; ");
        viewModel.To.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task Sugestoes_SemContaDefinida_NaoConsultaNada()
    {
        var viewModel = CreateViewModel();
        viewModel.To = "ana";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);

        viewModel.RecipientSuggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task AplicarSugestao_CampoJaSobrescritoPeloControle_PreservaOsOutrosDestinatarios()
    {
        // O AutoSuggestBox do WinUI escreve o item escolhido na caixa ANTES de avisar quem
        // o escolheu, e a ligação de duas vias empurra isso para a propriedade. Aplicar a
        // troca sobre a propriedade nessa hora apagaria os destinatários anteriores.
        ArrangeHistory(("bruno@cliente.com.br", "Bruno"));

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync(_account.Id);
        viewModel.To = "ana@cliente.com.br; bru";

        await viewModel.UpdateRecipientSuggestionsAsync(RecipientField.To);
        var sugestao = viewModel.RecipientSuggestions[0];

        viewModel.To = sugestao.Address;
        viewModel.ApplyRecipientSuggestion(RecipientField.To, sugestao);

        viewModel.To.Should().Be("ana@cliente.com.br; bruno@cliente.com.br; ");
    }
}
