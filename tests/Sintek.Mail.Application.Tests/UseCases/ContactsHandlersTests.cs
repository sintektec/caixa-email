using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Contacts;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>Histórico de destinatários em memória, com a mesma semântica do repositório real.</summary>
internal sealed class InMemoryRecipientHistoryRepository : IRecipientHistoryRepository
{
    private readonly List<RecipientHistory> _entries = [];

    public IReadOnlyList<RecipientHistory> Entries => _entries;

    public Task<RecipientHistory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    public Task<RecipientHistory?> GetByAddressAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.FirstOrDefault(
            e => e.AccountId == accountId && e.Address == address));

    public Task<IReadOnlyList<RecipientHistory>> ListForSuggestionAsync(
        Guid accountId, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RecipientHistory>>(
            [.. _entries.Where(e => e.AccountId == accountId)
                .OrderByDescending(e => e.LastUsedAt)
                .Take(limit)]);

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

/// <summary>Catálogo de contatos em memória.</summary>
internal sealed class InMemoryContactRepository : IContactRepository
{
    private readonly List<Contact> _contacts = [];

    public IReadOnlyList<Contact> Contacts => _contacts;

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

/// <summary>
/// Cobre o histórico de destinatários e o catálogo de contatos: o registro no envio, a
/// ordem das sugestões, a remoção individual e a importação de vCard sem duplicar.
/// </summary>
public class ContactsHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRecipientHistoryRepository _history = new();
    private readonly InMemoryContactRepository _contacts = new();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

    private readonly Account _account;

    public ContactsHandlersTests()
    {
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
    }

    private RecipientHistoryHandler HistoryHandler(TimeProvider? clock = null) => new(
        _history, _contacts, _accounts, _directories, _unitOfWork, clock ?? _clock,
        NullLogger<RecipientHistoryHandler>.Instance);

    private ManageContactsHandler ContactsHandler() => new(
        _contacts, _accounts, _unitOfWork, _clock, NullLogger<ManageContactsHandler>.Instance);

    private static UsedRecipient Destinatario(string address, string? nome = null)
        => new(EmailAddress.Parse(address), nome);

    [Fact]
    public async Task RecordUse_PrimeiroEnvio_CriaAEntrada()
    {
        await HistoryHandler().RecordUseAsync(
            _account.Id, [Destinatario("ana@cliente.com.br", "Ana")]);

        _history.Entries.Should().ContainSingle()
            .Which.Address.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task RecordUse_SegundoEnvioParaOMesmoEndereco_IncrementaEmVezDeDuplicar()
    {
        var handler = HistoryHandler();

        await handler.RecordUseAsync(_account.Id, [Destinatario("ana@cliente.com.br")]);
        await handler.RecordUseAsync(_account.Id, [Destinatario("ana@cliente.com.br")]);

        _history.Entries.Should().ContainSingle()
            .Which.UseCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordUse_MesmoEnderecoEmParaEEmCopia_ContaUmaVezSo()
    {
        await HistoryHandler().RecordUseAsync(
            _account.Id,
            [Destinatario("ana@cliente.com.br"), Destinatario("ana@cliente.com.br")]);

        _history.Entries.Should().ContainSingle()
            .Which.UseCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordUse_FalhaAoGravar_NaoPropagaOErro()
    {
        // O histórico é conveniência; a mensagem é o trabalho. Uma falha aqui não pode
        // derrubar um envio que já foi enfileirado.
        var repositorio = Substitute.For<IRecipientHistoryRepository>();
        repositorio
            .GetByAddressAsync(Arg.Any<Guid>(), Arg.Any<EmailAddress>(), Arg.Any<CancellationToken>())
            .Returns<Task<RecipientHistory?>>(_ => throw new InvalidOperationException("banco fora"));

        var handler = new RecipientHistoryHandler(
            repositorio, _contacts, _accounts, _directories, _unitOfWork, _clock,
            NullLogger<RecipientHistoryHandler>.Instance);

        var gravadas = await handler.RecordUseAsync(
            _account.Id, [Destinatario("ana@cliente.com.br")]);

        gravadas.Should().Be(0);
    }

    [Fact]
    public async Task Suggest_ComHistoricoECatalogo_OrdenaPeloRanqueadorDoDominio()
    {
        var handler = HistoryHandler();
        await handler.RecordUseAsync(_account.Id, [Destinatario("historico@cliente.com.br")]);

        var contato = Contact.Create(_account.Id, "Ana Souza", Now);
        contato.AddEmail(EmailAddress.Parse("catalogo@cliente.com.br"), Now, isPrimary: true);
        await _contacts.AddAsync(contato);

        var sugestoes = await handler.SuggestAsync(_account.Id, "c");

        sugestoes.Should().HaveCount(2);
        sugestoes[0].Address.Value.Should().Be("catalogo@cliente.com.br");
    }

    [Fact]
    public async Task Suggest_EnderecoForaDoDominioDaConta_VemMarcado()
    {
        var handler = HistoryHandler();
        await handler.RecordUseAsync(_account.Id, [Destinatario("externo@outraempresa.com")]);

        var sugestoes = await handler.SuggestAsync(_account.Id, "externo");

        sugestoes.Should().ContainSingle()
            .Which.BelongsToAccountDomain.Should().BeFalse();
    }

    [Fact]
    public async Task Suggest_ContaInexistente_DevolveVazio()
    {
        var sugestoes = await HistoryHandler().SuggestAsync(Guid.CreateVersion7(), "a");

        sugestoes.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_EntradaDoHistorico_DeixaDeSerSugerida()
    {
        var handler = HistoryHandler();
        await handler.RecordUseAsync(_account.Id, [Destinatario("errado@clientte.com.br")]);

        var entrada = _history.Entries[0];
        var removeu = await handler.RemoveAsync(entrada.Id);

        removeu.Should().BeTrue();
        (await handler.SuggestAsync(_account.Id, "errado")).Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_HistoricoDaConta_ApagaTudo()
    {
        var handler = HistoryHandler();
        await handler.RecordUseAsync(
            _account.Id,
            [Destinatario("a@cliente.com.br"), Destinatario("b@cliente.com.br")]);

        var apagadas = await handler.ClearAsync(_account.Id);

        apagadas.Should().Be(2);
        _history.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_ContatoNovo_GravaComOPrimeiroEnderecoComoPrincipal()
    {
        var resultado = await ContactsHandler().SaveAsync(new ContactCommand
        {
            AccountId = _account.Id,
            DisplayName = "Ana Souza",
            Emails =
            [
                new ContactEmailInput(EmailAddress.Parse("ana@cliente.com.br"), null, false),
                new ContactEmailInput(EmailAddress.Parse("ana@pessoal.com"), null, false),
            ],
        });

        resultado.Succeeded.Should().BeTrue();
        _contacts.Contacts.Should().ContainSingle()
            .Which.PrimaryEmail!.Address.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task Save_EnderecoRetiradoDoFormulario_SaiDoContato()
    {
        var handler = ContactsHandler();

        var criado = await handler.SaveAsync(new ContactCommand
        {
            AccountId = _account.Id,
            DisplayName = "Ana Souza",
            Emails =
            [
                new ContactEmailInput(EmailAddress.Parse("ana@cliente.com.br"), null, true),
                new ContactEmailInput(EmailAddress.Parse("ana@pessoal.com"), null, false),
            ],
        });

        await handler.SaveAsync(new ContactCommand
        {
            AccountId = _account.Id,
            ContactId = criado.ContactId,
            DisplayName = "Ana Souza",
            Emails = [new ContactEmailInput(EmailAddress.Parse("ana@cliente.com.br"), null, true)],
        });

        _contacts.Contacts[0].Emails.Should().ContainSingle();
    }

    [Fact]
    public async Task Save_SemNome_Recusa()
    {
        var resultado = await ContactsHandler().SaveAsync(new ContactCommand
        {
            AccountId = _account.Id,
            DisplayName = "   ",
        });

        resultado.Succeeded.Should().BeFalse();
        resultado.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Save_ContaInexistente_Recusa()
    {
        var resultado = await ContactsHandler().SaveAsync(new ContactCommand
        {
            AccountId = Guid.CreateVersion7(),
            DisplayName = "Ana",
        });

        resultado.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Import_ArquivoNovo_CriaOsContatos()
    {
        var resultado = await ContactsHandler().ImportAsync(_account.Id, """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            UID:ana-1
            END:VCARD
            """);

        resultado.Imported.Should().Be(1);
        resultado.Updated.Should().Be(0);
        _contacts.Contacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_MesmoArquivoDuasVezes_NaoDuplicaOCatalogo()
    {
        const string arquivo = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            UID:ana-1
            END:VCARD
            """;

        var handler = ContactsHandler();
        await handler.ImportAsync(_account.Id, arquivo);
        var segunda = await handler.ImportAsync(_account.Id, arquivo);

        segunda.Imported.Should().Be(0);
        segunda.Updated.Should().Be(1);
        _contacts.Contacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_SemUid_ReconheceOContatoPeloEndereco()
    {
        const string arquivo = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            END:VCARD
            """;

        var handler = ContactsHandler();
        await handler.ImportAsync(_account.Id, arquivo);
        var segunda = await handler.ImportAsync(_account.Id, arquivo);

        segunda.Updated.Should().Be(1);
        _contacts.Contacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_ContatoJaExistente_PreservaOEnderecoAcrescentadoAMao()
    {
        var handler = ContactsHandler();

        await handler.ImportAsync(_account.Id, """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            UID:ana-1
            END:VCARD
            """);

        _contacts.Contacts[0].AddEmail(EmailAddress.Parse("ana@pessoal.com"), Now);

        await handler.ImportAsync(_account.Id, """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:ana@cliente.com.br
            UID:ana-1
            END:VCARD
            """);

        _contacts.Contacts[0].Emails.Should().HaveCount(2);
    }

    [Fact]
    public async Task Export_DepoisDeImportar_ProduzArquivoQueVoltaIgual()
    {
        var handler = ContactsHandler();

        await handler.ImportAsync(_account.Id, """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            ORG:Cliente Ltda
            EMAIL:ana@cliente.com.br
            UID:ana-1
            END:VCARD
            """);

        var exportado = await handler.ExportAsync(_account.Id);
        var relido = VCardSerializer.Read(exportado);

        relido.Contacts.Should().ContainSingle();
        relido.Contacts[0].DisplayName.Should().Be("Ana Souza");
        relido.Contacts[0].Organization.Should().Be("Cliente Ltda");
        relido.Contacts[0].Emails[0].Address.Value.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public async Task Remove_Contato_SaiDoCatalogo()
    {
        var handler = ContactsHandler();
        var criado = await handler.SaveAsync(new ContactCommand
        {
            AccountId = _account.Id,
            DisplayName = "Ana Souza",
        });

        var removeu = await handler.RemoveAsync(criado.ContactId!.Value);

        removeu.Should().BeTrue();
        _contacts.Contacts.Should().BeEmpty();
    }
}
