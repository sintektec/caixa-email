using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Contacts;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o "adicionar aos contatos" do painel de leitura — o gesto que leva o remetente de
/// uma mensagem aberta para o catálogo, e que fecha o ciclo do autocompletar.
/// </summary>
public class ReadingPaneContactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IHtmlSanitizer _sanitizer = Substitute.For<IHtmlSanitizer>();
    private readonly FakeContactRepository _contacts = new();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly Account _account = Account.Create(
        Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

    public ReadingPaneContactsTests()
    {
        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);

        _sanitizer.Sanitize(Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(call => new SanitizedHtmlResult(
                call.Arg<string?>() ?? string.Empty, HasRemoteContent: false, RemovedRemoteReferences: 0));
    }

    /// <summary>
    /// Monta o painel com os casos de uso de verdade.
    /// </summary>
    /// <remarks>
    /// Os handlers são <c>sealed</c> e não podem ser substituídos; a substituição fica nos
    /// repositórios, que é onde ela pertence de todo modo.
    /// </remarks>
    private ReadingPaneViewModel CreateViewModel()
    {
        var folders = Substitute.For<IFolderRepository>();
        var outbox = new Sintek.Mail.Application.Services.OutboxEnqueuer(
            Substitute.For<IOutboxRepository>(), _clock);

        return new ReadingPaneViewModel(
            _messages,
            _sanitizer,
            new DownloadMessageContentHandler(
                _messages,
                folders,
                _unitOfWork,
                Substitute.For<Sintek.Mail.Application.Abstractions.Mail.IImapClient>(),
                _sanitizer,
                Substitute.For<IAttachmentStore>(),
                _clock,
                NullLogger<DownloadMessageContentHandler>.Instance),
            new Sintek.Mail.Application.UseCases.Organization.ManageSenderReputationHandler(
                Substitute.For<ISenderReputationRepository>(), _unitOfWork, _clock),
            new ReadReceiptHandler(
                _messages,
                _accounts,
                ComposeFactory.Create(
                    _messages, folders, _accounts, _unitOfWork, outbox,
                    ComposeFactory.InertRecipientHistory(_unitOfWork, _clock), _clock),
                _unitOfWork,
                _clock,
                NullLogger<ReadReceiptHandler>.Instance),
            new ManageContactsHandler(
                _contacts, _accounts, _unitOfWork, _clock, NullLogger<ManageContactsHandler>.Instance));
    }

    private Message ArrangeMessage(string from = "ana@cliente.com.br", string? displayName = "Ana Souza")
    {
        var message = Message.Create(
            _account.Id, Guid.CreateVersion7(), "<msg@cliente>", Now, Now, Now);
        message.SetHeaders("Assunto", EmailAddress.Parse(from), displayName, null, null, Now);

        _messages.GetWithParticipantsAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        return message;
    }

    [Fact]
    public async Task AdicionarAosContatos_RemetenteNovo_EntraNoCatalogo()
    {
        var message = ArrangeMessage();
        var viewModel = CreateViewModel();
        await viewModel.LoadMessageAsync(message.Id);

        await viewModel.AddSenderToContactsAsync();

        var contatos = await _contacts.ListAsync(_account.Id);
        contatos.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ana Souza");
        contatos[0].PrimaryEmail!.Address.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task AdicionarAosContatos_RemetenteSemNome_UsaOEnderecoComoRotulo()
    {
        var message = ArrangeMessage(displayName: null);
        var viewModel = CreateViewModel();
        await viewModel.LoadMessageAsync(message.Id);

        await viewModel.AddSenderToContactsAsync();

        (await _contacts.ListAsync(_account.Id))[0].DisplayName.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task AdicionarAosContatos_RemetenteJaNoCatalogo_NaoDuplicaNemSobrescreve()
    {
        // O contato pode ter sido editado à mão desde então; trocar o nome curado pelo que
        // veio no cabeçalho seria perder a edição sem avisar.
        var existente = Contact.Create(_account.Id, "Ana Souza (Financeiro)", Now);
        existente.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        await _contacts.AddAsync(existente);

        var message = ArrangeMessage();
        var viewModel = CreateViewModel();
        await viewModel.LoadMessageAsync(message.Id);

        await viewModel.AddSenderToContactsAsync();

        var contatos = await _contacts.ListAsync(_account.Id);
        contatos.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ana Souza (Financeiro)");
        viewModel.ContactMessage.Should().Contain("já está nos contatos");
    }

    [Fact]
    public async Task AdicionarAosContatos_SemMensagemAberta_NaoFazNada()
    {
        var viewModel = CreateViewModel();

        await viewModel.AddSenderToContactsAsync();

        viewModel.CanAddSenderToContacts.Should().BeFalse();
        (await _contacts.ListAsync(_account.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task AbrirMensagem_ComRemetente_HabilitaOBotao()
    {
        var message = ArrangeMessage();
        var viewModel = CreateViewModel();

        await viewModel.LoadMessageAsync(message.Id);

        viewModel.CanAddSenderToContacts.Should().BeTrue();
    }
}
