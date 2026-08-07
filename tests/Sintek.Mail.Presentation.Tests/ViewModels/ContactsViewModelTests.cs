using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Contacts;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre a tela de contatos: gravação a partir do formulário, importação de vCard e a
/// remoção individual de uma entrada do histórico — que é o que resolve o endereço digitado
/// errado que passa a ser sugerido para sempre.
/// </summary>
public class ContactsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeContactRepository _contacts = new();
    private readonly FakeRecipientHistoryRepository _history = new();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

    private readonly Account _account;

    public ContactsViewModelTests()
    {
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
    }

    private ContactsViewModel CreateViewModel() => new(
        new ManageContactsHandler(
            _contacts, _accounts, _unitOfWork, _clock, NullLogger<ManageContactsHandler>.Instance),
        new RecipientHistoryHandler(
            _history, _contacts, _accounts, _directories, _unitOfWork, _clock,
            NullLogger<RecipientHistoryHandler>.Instance));

    [Fact]
    public async Task Load_ContaComCatalogoEHistorico_PreencheAsDuasListas()
    {
        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        await _contacts.AddAsync(contato);
        await _history.AddAsync(
            RecipientHistory.Create(_account.Id, EmailAddress.Parse("bruno@cliente.com.br"), Now));

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        viewModel.Contacts.Should().ContainSingle()
            .Which.PrimaryAddress.Should().Be("ana@cliente.com.br");
        viewModel.History.Should().ContainSingle();
    }

    [Fact]
    public async Task Save_FormularioPreenchido_AcrescentaOContatoNaLista()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        viewModel.DisplayName = "Ana Souza";
        viewModel.Emails = "ana@cliente.com.br; ana@pessoal.com";

        await viewModel.SaveAsync();

        viewModel.Contacts.Should().ContainSingle()
            .Which.PrimaryAddress.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task Save_EnderecoInvalido_AvisaSemGravar()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        viewModel.DisplayName = "Ana Souza";
        viewModel.Emails = "ana@cliente.com.br; isto-nao-e-endereco";

        await viewModel.SaveAsync();

        viewModel.StatusMessage.Should().Contain("isto-nao-e-endereco");
        viewModel.Contacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_SemNome_AvisaSemGravar()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        viewModel.Emails = "ana@cliente.com.br";

        await viewModel.SaveAsync();

        viewModel.StatusMessage.Should().NotBeEmpty();
        viewModel.Contacts.Should().BeEmpty();
    }

    [Fact]
    public async Task EditSelected_ContatoComVariosEnderecos_TrazOPrincipalPrimeiro()
    {
        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("secundario@cliente.com.br"), Now);
        contato.AddEmail(EmailAddress.Parse("principal@cliente.com.br"), Now, isPrimary: true);
        await _contacts.AddAsync(contato);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);
        viewModel.SelectedContact = viewModel.Contacts[0];

        await viewModel.EditSelectedAsync();

        viewModel.Emails.Should().StartWith("principal@cliente.com.br");
        viewModel.EditingContactId.Should().Be(contato.Id);
    }

    [Fact]
    public async Task Import_ArquivoVCard_AcrescentaOsContatosEResumeOResultado()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        await viewModel.ImportAsync("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            END:VCARD
            """);

        viewModel.Contacts.Should().ContainSingle();
        viewModel.StatusMessage.Should().Contain("1 contato(s) novo(s)");
    }

    [Fact]
    public async Task Import_ArquivoSemCartao_Avisa()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        await viewModel.ImportAsync("isto nao e um vcard");

        viewModel.StatusMessage.Should().Contain("nenhum contato");
    }

    [Fact]
    public async Task Export_CatalogoComContato_ProduzTextoVCard()
    {
        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("ana@cliente.com.br"), Now, isPrimary: true);
        await _contacts.AddAsync(contato);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        var exportado = await viewModel.ExportAsync();

        exportado.Should().Contain("FN:Ana Souza");
        exportado.Should().Contain("ana@cliente.com.br");
    }

    [Fact]
    public async Task RemoveHistoryEntry_EntradaEscolhida_SaiDaLista()
    {
        var entrada = RecipientHistory.Create(
            _account.Id, EmailAddress.Parse("errado@clientte.com.br"), Now);
        await _history.AddAsync(entrada);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        await viewModel.RemoveHistoryEntryAsync(entrada.Id);

        viewModel.History.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearHistory_ContaComEntradas_EsvaziaEInforma()
    {
        await _history.AddAsync(
            RecipientHistory.Create(_account.Id, EmailAddress.Parse("a@cliente.com.br"), Now));
        await _history.AddAsync(
            RecipientHistory.Create(_account.Id, EmailAddress.Parse("b@cliente.com.br"), Now));

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);

        await viewModel.ClearHistoryAsync();

        viewModel.History.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("2 entrada(s)");
    }

    [Fact]
    public async Task StartNewContact_DepoisDeEditar_LimpaOFormulario()
    {
        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        await _contacts.AddAsync(contato);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);
        viewModel.SelectedContact = viewModel.Contacts[0];
        await viewModel.EditSelectedAsync();

        viewModel.StartNewContact();

        viewModel.EditingContactId.Should().BeNull();
        viewModel.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSelected_ContatoEmEdicao_LimpaOFormularioTambem()
    {
        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        await _contacts.AddAsync(contato);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync(_account.Id);
        viewModel.SelectedContact = viewModel.Contacts[0];
        await viewModel.EditSelectedAsync();

        await viewModel.RemoveSelectedAsync();

        viewModel.Contacts.Should().BeEmpty();
        viewModel.EditingContactId.Should().BeNull();
    }
}

/// <summary>Catálogo de contatos em memória, para as verificações de tela.</summary>
internal sealed class FakeContactRepository : IContactRepository
{
    private readonly List<Contact> _contacts = [];

    public Task<Contact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_contacts.FirstOrDefault(c => c.Id == id));

    public Task<Contact?> GetByExternalIdAsync(
        Guid accountId, string externalId, CancellationToken cancellationToken = default)
        => Task.FromResult(_contacts.FirstOrDefault(
            c => c.AccountId == accountId && c.ExternalId == externalId));

    public Task<Contact?> GetByEmailAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default)
        => Task.FromResult(_contacts.FirstOrDefault(
            c => c.AccountId == accountId && c.Emails.Any(e => e.Address == address)));

    public Task<IReadOnlyList<Contact>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Contact>>(
            [.. _contacts.Where(c => c.AccountId == accountId).OrderBy(c => c.DisplayName)]);

    public Task AddAsync(Contact contact, CancellationToken cancellationToken = default)
    {
        _contacts.Add(contact);
        return Task.CompletedTask;
    }

    public void Remove(Contact contact) => _contacts.Remove(contact);
}

/// <summary>Histórico de destinatários em memória, para as verificações de tela.</summary>
internal sealed class FakeRecipientHistoryRepository : IRecipientHistoryRepository
{
    private readonly List<RecipientHistory> _entries = [];

    public Task<RecipientHistory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    public Task<RecipientHistory?> GetByAddressAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.FirstOrDefault(
            e => e.AccountId == accountId && e.Address == address));

    public Task<IReadOnlyList<RecipientHistory>> ListForSuggestionAsync(
        Guid accountId, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RecipientHistory>>(
            [.. _entries.Where(e => e.AccountId == accountId).Take(limit)]);

    public Task<IReadOnlyList<RecipientHistory>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RecipientHistory>>(
            [.. _entries.Where(e => e.AccountId == accountId)]);

    public Task AddAsync(RecipientHistory entry, CancellationToken cancellationToken = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public void Remove(RecipientHistory entry) => _entries.Remove(entry);
}
