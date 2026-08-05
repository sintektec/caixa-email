using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Application.UseCases.Domains;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre as telas de configuração: editor de Diretórios de Domínio e lista de contas.
/// O foco é a confirmação antes de destruir dados — o ponto em que um clique desatento
/// custaria a caixa postal inteira.
/// </summary>
public class ConfigurationViewModelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();
    private readonly IOAuthProviderRegistry _oauth = Substitute.For<IOAuthProviderRegistry>();
    private readonly FakeTimeProvider _clock = new(Now);

    public ConfigurationViewModelsTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<DomainDirectory>());
        _accounts.ListByDomainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Account>());
        _folders.ListByAccountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Folder>());
        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxOperation>());
    }

    private ChangeDomainNameHandler ChangeDomain() => new(
        _directories,
        _accounts,
        _folders,
        Substitute.For<IMessageRepository>(),
        _audit,
        _unitOfWork,
        new OutboxEnqueuer(_outbox, _clock),
        _clock,
        NullLogger<ChangeDomainNameHandler>.Instance);

    private AccountRemover Remover() => new(
        _accounts,
        _folders,
        new AccountCredentialRevoker(_credentials, _oauth, NullLogger<AccountCredentialRevoker>.Instance),
        NullLogger<AccountRemover>.Instance);

    private DomainDirectoryEditorViewModel Editor() => new(
        _directories,
        new CreateDomainDirectoryHandler(
            _directories, _audit, _unitOfWork, _clock, NullLogger<CreateDomainDirectoryHandler>.Instance),
        new UpdateDomainDirectoryHandler(
            _directories, _audit, _unitOfWork, _clock, NullLogger<UpdateDomainDirectoryHandler>.Instance),
        new RemoveDomainDirectoryHandler(
            _directories, _accounts, _audit, _unitOfWork, Remover(), _clock,
            NullLogger<RemoveDomainDirectoryHandler>.Instance),
        ChangeDomain());

    private AccountsViewModel AccountsList() => new(
        _accounts,
        _directories,
        new UpdateAccountHandler(
            _accounts,
            _unitOfWork,
            _credentials,
            new TestAccountConnectionHandler(
                Substitute.For<Application.Abstractions.Mail.IImapClient>(),
                Substitute.For<Application.Abstractions.Mail.ISmtpSender>(),
                _credentials,
                _oauth,
                _clock,
                NullLogger<TestAccountConnectionHandler>.Instance),
            _clock,
            NullLogger<UpdateAccountHandler>.Instance),
        new RemoveAccountHandler(
            _accounts, _outbox, _audit, _unitOfWork, Remover(), _clock,
            NullLogger<RemoveAccountHandler>.Instance));

    // ----- Editor de Diretórios ------------------------------------------------------

    [Theory]
    [InlineData("sintek..com.br")]
    [InlineData("-sintek.com.br")]
    [InlineData("contato@sintek.com.br")]
    public void ErroDoDominio_ValorInvalido_ExibeMotivoEBloqueiaAGravacao(string domainName)
    {
        var editor = Editor();
        editor.DomainName = domainName;

        editor.DomainNameError.Should().NotBeNullOrWhiteSpace();
        editor.CanSave.Should().BeFalse();
    }

    [Fact]
    public void ErroDoDominio_ValorValido_LiberaAGravacao()
    {
        var editor = Editor();
        editor.DomainName = "sintek.com.br";

        editor.DomainNameError.Should().BeEmpty();
        editor.CanSave.Should().BeTrue();
    }

    [Fact]
    public void ErroDoDominio_CampoVazio_NaoAcusaErroMasBloqueiaAGravacao()
    {
        // Acusar erro antes de o usuário digitar qualquer coisa é ruído.
        var editor = Editor();

        editor.DomainNameError.Should().BeEmpty();
        editor.CanSave.Should().BeFalse();
    }

    [Fact]
    public void AcrescentarDominioAdicional_ValorInvalido_NaoEntraNaLista()
    {
        var editor = Editor();

        editor.AddAlias("dominio invalido");

        editor.Aliases.Should().BeEmpty();
        editor.StatusMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AcrescentarDominioAdicional_Repetido_NaoDuplica()
    {
        var editor = Editor();

        editor.AddAlias("sintek.net.br");
        editor.AddAlias("SINTEK.NET.BR");

        editor.Aliases.Should().ContainSingle().Which.Should().Be("sintek.net.br");
    }

    [Fact]
    public async Task Gravar_NovoDiretorio_CriaEGuardaOIdentificador()
    {
        var editor = Editor();
        editor.DomainName = "sintek.com.br";
        editor.Description = "Matriz";
        editor.AddAlias("sintek.net.br");

        await editor.SaveAsync();

        editor.StatusMessage.Should().BeNull();
        editor.DomainDirectoryId.Should().NotBeNull();

        await _directories.Received(1).AddAsync(
            Arg.Is<DomainDirectory>(d => d.DomainName.Value == "sintek.com.br" && d.Aliases.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmarRemocao_SemTerMedidoOImpacto_Recusa()
    {
        // Confirmar sem ter visto o que será perdido é justamente o que a especificação
        // proíbe; deixar o caminho aberto permitiria que uma tela futura o usasse por engano.
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        var editor = Editor();
        await editor.LoadAsync(directory.Id);

        var removed = await editor.ConfirmRemovalAsync();

        removed.Should().BeFalse();
        editor.StatusMessage.Should().Contain("antes de confirmar");
        _directories.DidNotReceive().Remove(Arg.Any<DomainDirectory>());
    }

    [Fact]
    public async Task ConfirmarRemocao_ComImpactoMedido_Remove()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        var editor = Editor();
        await editor.LoadAsync(directory.Id);
        await editor.PrepareRemovalAsync();

        editor.PendingRemovalImpact!.DomainName.Should().Be("sintek.com.br");

        var removed = await editor.ConfirmRemovalAsync();

        removed.Should().BeTrue();
        _directories.Received(1).Remove(directory);
    }

    [Fact]
    public async Task Carregar_DiretorioExistente_PreencheOFormularioInteiro()
    {
        var directory = DomainDirectory.Create(
            EmailDomain.Parse("sintek.com.br"),
            Now,
            "Matriz",
            DomainValidationMode.SenderOnly,
            InvalidEmailAction.MoveToPending,
            allowSubdomains: true);

        directory.AddAlias(EmailDomain.Parse("sintek.net.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        var editor = Editor();
        await editor.LoadAsync(directory.Id);

        editor.DomainName.Should().Be("sintek.com.br");
        editor.Description.Should().Be("Matriz");
        editor.ValidationMode.Should().Be(DomainValidationMode.SenderOnly);
        editor.InvalidEmailAction.Should().Be(InvalidEmailAction.MoveToPending);
        editor.AllowSubdomains.Should().BeTrue();
        editor.Aliases.Should().BeEquivalentTo(["sintek.net.br"]);
    }

    // ----- Lista de contas -----------------------------------------------------------

    [Fact]
    public async Task CarregarContas_ComDoisDiretorios_ListaTodasComODominioDeOrigem()
    {
        var matriz = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var filial = DomainDirectory.Create(EmailDomain.Parse("sintek.net.br"), Now);

        var contaMatriz = Account.Create(
            matriz.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
        var contaFilial = Account.Create(
            filial.Id, EmailAddress.Parse("vendas@sintek.net.br"), "Vendas", Now);

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { matriz, filial });
        _accounts.ListByDomainAsync(matriz.Id, Arg.Any<CancellationToken>()).Returns(new[] { contaMatriz });
        _accounts.ListByDomainAsync(filial.Id, Arg.Any<CancellationToken>()).Returns(new[] { contaFilial });

        var viewModel = AccountsList();
        await viewModel.LoadAsync();

        viewModel.Accounts.Should().HaveCount(2);
        viewModel.Accounts.Select(a => a.DomainName).Should().BeEquivalentTo(["sintek.com.br", "sintek.net.br"]);
    }

    [Fact]
    public async Task ConfirmarRemocaoDeConta_SemTerMedidoOImpacto_Recusa()
    {
        var viewModel = await ArrangeAccountsWithSelectionAsync();

        var removed = await viewModel.ConfirmRemovalAsync();

        removed.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("antes de confirmar");
        _accounts.DidNotReceive().Remove(Arg.Any<Account>());
    }

    [Fact]
    public async Task ConfirmarRemocaoDeConta_ComImpactoMedido_RemoveEDesapareceDaLista()
    {
        var viewModel = await ArrangeAccountsWithSelectionAsync();

        await viewModel.PrepareRemovalAsync();
        var removed = await viewModel.ConfirmRemovalAsync();

        removed.Should().BeTrue();
        viewModel.Accounts.Should().BeEmpty();
        viewModel.SelectedAccount.Should().BeNull();
    }

    [Fact]
    public async Task DesativarConta_NaoExigeTesteDeConexao()
    {
        // Desativar uma conta cujo servidor saiu do ar precisa funcionar: exigir conexão
        // prenderia o usuário justamente à conta que ele quer parar de usar.
        var viewModel = await ArrangeAccountsWithSelectionAsync();

        await viewModel.ToggleSelectedAccountAsync();

        viewModel.Accounts[0].IsActive.Should().BeFalse();
        viewModel.Accounts[0].SyncStatus.Should().Be(AccountSyncStatus.Disabled);
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void DescricaoDeSituacao_CredencialRecusada_DizQuePrecisaReautenticar()
    {
        // Distinto de erro comum: a ação do usuário é outra.
        var item = new AccountListItemViewModel
        {
            AccountId = Guid.CreateVersion7(),
            EmailAddress = "contato@sintek.com.br",
            DisplayName = "Contato",
            DomainName = "sintek.com.br",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
            AuthenticationType = AuthenticationType.Password,
            SyncStatus = AccountSyncStatus.AuthenticationFailed,
        };

        item.StatusDescription.Should().Contain("reautenticar");
    }

    private async Task<AccountsViewModel> ArrangeAccountsWithSelectionAsync()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        account.ConfigureServers(
            "imap.sintek.com.br", 993, SecureSocketMode.SslOnConnect,
            "smtp.sintek.com.br", 587, SecureSocketMode.StartTls, Now);

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { directory });
        _accounts.ListByDomainAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(new[] { account });
        _accounts.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);

        var viewModel = AccountsList();
        await viewModel.LoadAsync();
        viewModel.SelectedAccount = viewModel.Accounts[0];

        return viewModel;
    }
}
