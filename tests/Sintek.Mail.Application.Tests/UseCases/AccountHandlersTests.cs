using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre o ciclo de vida de uma conta: cadastro (com senha e com OAuth), teste de
/// configuração, alteração e remoção.
/// </summary>
public class AccountHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly ISmtpSender _smtp = Substitute.For<ISmtpSender>();
    private readonly IAutodiscoverService _autodiscover = Substitute.For<IAutodiscoverService>();
    private readonly IOAuthProviderRegistry _oauthRegistry = Substitute.For<IOAuthProviderRegistry>();
    private readonly IOAuthProvider _oauthProvider = Substitute.For<IOAuthProvider>();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public AccountHandlersTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());
        _smtp.TestConnectionAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxOperation>());
        _folders.ListByAccountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());

        _oauthProvider.Provider.Returns(OAuthProviderKind.Microsoft);
        _oauthProvider.IsConfigured.Returns(true);
        _oauthProvider
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthAccessToken("token", Now.AddHours(1)));

        // Conta nova não tem token no cofre, e é isso que leva ao consentimento interativo.
        // Sem este padrão o dublê devolveria um token vazio e o caminho silencioso passaria,
        // escondendo justamente o defeito que fecha o laço do assistente.
        _oauthProvider
            .GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ReauthenticationRequiredException("contato@sintek.com.br"));

        _oauthRegistry.Resolve(OAuthProviderKind.Microsoft).Returns(_oauthProvider);
    }

    private AddAccountHandler AddHandler() => new(
        _directories, _accounts, _folders, _audit, _unitOfWork, _credentials,
        _autodiscover, _imap, _oauthRegistry, _clock, NullLogger<AddAccountHandler>.Instance);

    private TestAccountConnectionHandler ConnectionTestHandler() => new(
        _imap, _smtp, _credentials, _oauthRegistry, [], _clock,
        NullLogger<TestAccountConnectionHandler>.Instance);

    private UpdateAccountHandler UpdateHandler() => new(
        _accounts, _unitOfWork, _credentials, ConnectionTestHandler(), _clock,
        NullLogger<UpdateAccountHandler>.Instance);

    private RemoveAccountHandler RemoveHandler() => new(
        _accounts,
        _outbox,
        _audit,
        _unitOfWork,
        new AccountRemover(
            _accounts,
            _folders,
            new AccountCredentialRevoker(_credentials, _oauthRegistry, NullLogger<AccountCredentialRevoker>.Instance),
            NullLogger<AccountRemover>.Instance),
        _clock,
        NullLogger<RemoveAccountHandler>.Instance);

    private DomainDirectory ArrangeDirectory()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);
        return directory;
    }

    private static AddAccountCommand PasswordCommand(Guid directoryId) => new()
    {
        DomainDirectoryId = directoryId,
        EmailAddress = "contato@sintek.com.br",
        DisplayName = "Contato",
        ImapHost = "imap.sintek.com.br",
        SmtpHost = "smtp.sintek.com.br",
        Password = FakeSecret.For("cadastro"),
    };

    // ----- Cadastro ------------------------------------------------------------------

    [Fact]
    public async Task CadastrarConta_ComSenha_GuardaSegredoNoCofreENaoNaEntidade()
    {
        var directory = ArrangeDirectory();

        var result = await AddHandler().HandleAsync(PasswordCommand(directory.Id));

        result.Succeeded.Should().BeTrue();
        _credentials.Keys.Should().ContainSingle().Which.Should().Be("Sintek.Mail:contato@sintek.com.br");

        await _accounts.Received(1).AddAsync(
            Arg.Is<Account>(a => a.CredentialKey == "Sintek.Mail:contato@sintek.com.br"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CadastrarConta_DominioDivergente_LancaERegistraRecusa()
    {
        var directory = ArrangeDirectory();

        var command = PasswordCommand(directory.Id) with { EmailAddress = "usuario@gmail.com" };

        await Assert.ThrowsAsync<DomainMismatchException>(() => AddHandler().HandleAsync(command));

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.AccountRejectedByDomainRule),
            Arg.Any<CancellationToken>());

        // Nada de rede antes da regra: testar credenciais de uma conta que a regra recusa
        // gastaria tempo e poderia disparar bloqueio por tentativa malsucedida.
        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CadastrarConta_ConexaoRecusada_NaoDeixaSenhaNoCofre()
    {
        var directory = ArrangeDirectory();

        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.AuthenticationFailure("Senha incorreta."));

        var result = await AddHandler().HandleAsync(PasswordCommand(directory.Id));

        result.Succeeded.Should().BeFalse();
        _credentials.WrittenKeys.Should().NotBeEmpty("a senha precisa existir durante o teste");
        _credentials.Keys.Should().BeEmpty("mas não pode sobrar depois da falha");
    }

    [Fact]
    public async Task CadastrarConta_ComOAuth_PedeConsentimentoAntesDeTestarAConexao()
    {
        var directory = ArrangeDirectory();

        var command = PasswordCommand(directory.Id) with
        {
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
            Password = null,
        };

        var result = await AddHandler().HandleAsync(command);

        result.Succeeded.Should().BeTrue();

        await _oauthProvider.Received(1)
            .AuthenticateInteractivelyAsync("contato@sintek.com.br", Arg.Any<CancellationToken>());

        _credentials.WrittenKeys.Should().BeEmpty("token de OAuth não passa pelo cofre desta camada");
    }

    [Fact]
    public async Task CadastrarConta_ProvedorOAuthSemClientId_ExplicaQueFaltaConfigurar()
    {
        var directory = ArrangeDirectory();
        _oauthProvider.IsConfigured.Returns(false);

        var command = PasswordCommand(directory.Id) with
        {
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
            Password = null,
        };

        var result = await AddHandler().HandleAsync(command);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Client ID");

        await _oauthProvider.DidNotReceive()
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CadastrarConta_ConsentimentoCancelado_RecusaSemMensagemDeErroTecnica()
    {
        var directory = ArrangeDirectory();

        _oauthProvider
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var command = PasswordCommand(directory.Id) with
        {
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
            Password = null,
        };

        var result = await AddHandler().HandleAsync(command);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelada");
    }

    [Fact]
    public async Task CadastrarConta_ComOAuthEConexaoRecusada_RevogaOConsentimento()
    {
        var directory = ArrangeDirectory();

        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Failure("Servidor inacessível."));

        var command = PasswordCommand(directory.Id) with
        {
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
            Password = null,
        };

        await AddHandler().HandleAsync(command);

        // Um consentimento deixado para trás autorizaria o aplicativo a ler a caixa de uma
        // conta que o usuário nunca chegou a cadastrar.
        await _oauthProvider.Received(1)
            .SignOutAsync("contato@sintek.com.br", Arg.Any<CancellationToken>());
    }

    // ----- Teste de configuração -----------------------------------------------------

    [Fact]
    public async Task TestarConexao_ComSenha_NaoDeixaSegredoNoCofre()
    {
        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
            Password = FakeSecret.For("cadastro"),
        });

        result.Succeeded.Should().BeTrue();
        _credentials.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task TestarConexao_UsaChaveDeCredencialPropria_NaoADaContaReal()
    {
        // Reutilizar a chave definitiva faria o teste sobrescrever, e depois apagar, a
        // senha de uma conta real já cadastrada com o mesmo endereço.
        await _credentials.SetSecretAsync("Sintek.Mail:contato@sintek.com.br", FakeSecret.For("em-uso"));

        await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
            Password = FakeSecret.For("tentativa"),
        });

        (await _credentials.GetSecretAsync("Sintek.Mail:contato@sintek.com.br")).Should().Be(FakeSecret.For("em-uso"));
    }

    [Fact]
    public async Task TestarConexao_ImapFalha_AindaAssimTestaOSmtp()
    {
        // Host errado nos dois é comum; mostrar os dois erros de uma vez poupa uma rodada
        // inteira de tentativa e erro.
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Failure("IMAP inacessível."));
        _smtp.TestConnectionAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Failure("SMTP inacessível."));

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "imap.errado",
            SmtpHost = "smtp.errado",
            Password = FakeSecret.For("cadastro"),
        });

        result.Succeeded.Should().BeFalse();
        result.Imap.ErrorMessage.Should().Contain("IMAP");
        result.Smtp.ErrorMessage.Should().Contain("SMTP");
    }

    [Fact]
    public async Task TestarConexao_SemSenha_RecusaAntesDeTocarNaRede()
    {
        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
        });

        result.Succeeded.Should().BeFalse();
        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sem isto o assistente não cadastra conta OAuth nenhuma.
    /// </summary>
    /// <remarks>
    /// O teste de conexão é obrigatório para avançar, e ele autentica com o token guardado no
    /// cofre. Numa conta que ainda não existe não há token, e o IMAP falhava com "o acesso
    /// autorizado expirou" — que descreve o oposto do que houve, porque nunca houve
    /// autorização para expirar. O consentimento só acontecia no cadastro, que fica depois do
    /// teste e nunca era alcançado: um laço fechado, e o mesmo nos dois provedores. Foi a
    /// validação manual em Windows que encontrou.
    /// </remarks>
    [Fact]
    public async Task TestarConexao_ContaOAuthSemTokenGuardado_PedeConsentimentoAntesDeConectar()
    {
        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp.office365.com",
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
        });

        result.Succeeded.Should().BeTrue();

        await _oauthProvider.Received(1)
            .AuthenticateInteractivelyAsync("contato@sintek.com.br", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Com token válido em cache, nada de navegador.
    /// </summary>
    /// <remarks>
    /// O <c>AcquireTokenInteractive</c> do MSAL <b>sempre</b> mostra a janela, mesmo com token
    /// válido. Sem a tentativa silenciosa, testar duas vezes — que é o que se faz enquanto se
    /// corrige host e porta — pediria consentimento a cada vez.
    /// </remarks>
    [Fact]
    public async Task TestarConexao_ContaOAuthComTokenValido_NaoAbreOConsentimento()
    {
        _oauthProvider
            .GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthAccessToken("token", Now.AddHours(1)));

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp.office365.com",
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
        });

        result.Succeeded.Should().BeTrue();

        await _oauthProvider.DidNotReceive()
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Fechar a janela de consentimento é decisão do usuário, e a mensagem precisa dizer
    /// isso — não "acesso expirado".
    /// </summary>
    [Fact]
    public async Task TestarConexao_ConsentimentoCancelado_ExplicaOCancelamento()
    {
        _oauthProvider
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp.office365.com",
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
        });

        result.Succeeded.Should().BeFalse();
        result.FirstError.Should().Contain("cancelada");
        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Servidor que aceita a conexão e não responde precisa ser abandonado.
    /// </summary>
    /// <remarks>
    /// Porta filtrada engole o SYN sem recusar, e o sistema fica retransmitindo por minutos —
    /// diferente de host inexistente, que falha rápido no DNS. É o que se encontra ao errar a
    /// porta ou o modo de proteção, 465 esperando STARTTLS ou 587 esperando SSL direto. Com o
    /// diálogo segurando um <c>Deferral</c>, isso apagava Cancelar e Voltar junto.
    /// </remarks>
    [Fact]
    public async Task TestarConexao_ServidorQueNuncaResponde_DesisteEExplica()
    {
        _clock.TimerDelayOverride = TimeSpan.FromMilliseconds(50);

        _imap
            .ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return ConnectionTestResult.Success();
            });

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@eversis.com.br",
            ImapHost = "imap.eversis.com.br",
            SmtpHost = "smtp.eversis.com.br",
            Password = FakeSecret.For("hostinger"),
        });

        result.Succeeded.Should().BeFalse();
        result.FirstError.Should().Contain("não respondeu");

        _credentials.Keys.Should().BeEmpty(
            "a chave temporária do teste é apagada mesmo quando a espera estoura — senão o " +
            "Gerenciador de Credenciais acumularia a senha de cada tentativa abandonada");
    }

    /// <summary>
    /// Nenhuma resposta de servidor pode derrubar a aplicação.
    /// </summary>
    /// <remarks>
    /// O <c>TimeoutException</c> do MailKit — teto próprio de 120 segundos — não é
    /// <c>IOException</c> nem <c>SocketException</c>, escapava da lista de capturas do
    /// autenticador e subia até o manipulador <c>async void</c> do diálogo, que encerra o
    /// processo. Uma porta errada não pode fechar o programa.
    /// </remarks>
    [Fact]
    public async Task TestarConexao_ExcecaoInesperadaDoCliente_ViraFalhaEmVezDeDerrubar()
    {
        _imap
            .ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Operation timed out after 120000 milliseconds"));

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@eversis.com.br",
            ImapHost = "imap.eversis.com.br",
            SmtpHost = "smtp.eversis.com.br",
            Password = FakeSecret.For("hostinger"),
        });

        result.Succeeded.Should().BeFalse();
        result.FirstError.Should().NotBeNullOrWhiteSpace();
        _credentials.Keys.Should().BeEmpty("a chave temporária sai mesmo em falha inesperada");
    }

    /// <summary>
    /// Consentimento que nunca volta precisa desistir sozinho.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A biblioteca da Google abre um ouvinte local e aguarda o redirecionamento para
    /// <c>http://localhost</c>. Quando o provedor recusa — conta de fora da organização num
    /// aplicativo interno, que devolve <c>403 org_internal</c> — ele exibe a página de erro e
    /// <b>nunca redireciona</b>. A espera não termina sozinha.
    /// </para>
    /// <para>
    /// E o custo não fica na espera: o diálogo do assistente segura um <c>Deferral</c>
    /// enquanto o teste roda, o que deixa Cancelar e Voltar inertes junto. Sem este teto, a
    /// única saída era encerrar o processo — foi assim que apareceu, na validação manual.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TestarConexao_ConsentimentoQueNuncaResponde_DesisteEExplica()
    {
        _clock.TimerDelayOverride = TimeSpan.FromMilliseconds(50);

        _oauthProvider
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // O provedor pendura, como pendura de verdade quando a página de erro não
                // redireciona: só o cancelamento tira daqui.
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new OAuthAccessToken("nunca chega", Now);
            });

        var result = await ConnectionTestHandler().HandleAsync(new TestAccountConnectionCommand
        {
            EmailAddress = "contato@sintek.com.br",
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp.office365.com",
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
        });

        result.Succeeded.Should().BeFalse();

        result.FirstError.Should()
            .Contain("não foi concluída no navegador").And
            .NotContain("cancelada",
                "quem não concluiu é o provedor, e mandar o usuário procurar o que ele cancelou " +
                "desperdiça a única pista que a mensagem tem para dar");

        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// O cadastro reaproveita o consentimento dado no teste — abrir o navegador de novo, logo
    /// depois de a pessoa ter autorizado, seria lido como defeito.
    /// </summary>
    [Fact]
    public async Task CadastrarConta_ComTokenJaObtidoNoTeste_NaoAbreOConsentimentoDeNovo()
    {
        _oauthProvider
            .GetAccessTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthAccessToken("token", Now.AddHours(1)));

        var directory = ArrangeDirectory();

        var result = await AddHandler().HandleAsync(new AddAccountCommand
        {
            DomainDirectoryId = directory.Id,
            EmailAddress = "contato@sintek.com.br",
            DisplayName = "Contato",
            ImapHost = "outlook.office365.com",
            SmtpHost = "smtp.office365.com",
            AuthenticationType = AuthenticationType.OAuth2,
            OAuthProvider = OAuthProviderKind.Microsoft,
        });

        result.Succeeded.Should().BeTrue();

        await _oauthProvider.DidNotReceive()
            .AuthenticateInteractivelyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----- Alteração -----------------------------------------------------------------

    [Fact]
    public async Task AlterarConta_TesteRecusado_NaoAlteraEntidadeNemSenha()
    {
        var account = ArrangeStoredAccount();
        await _credentials.SetSecretAsync(account.CredentialKey, FakeSecret.For("anterior"));

        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.AuthenticationFailure("Senha incorreta."));

        var result = await UpdateHandler().HandleAsync(new UpdateAccountCommand
        {
            AccountId = account.Id,
            DisplayName = "Nome Novo",
            ImapHost = "imap.novo.com.br",
            SmtpHost = "smtp.novo.com.br",
            NewPassword = FakeSecret.For("nova"),
        });

        result.Succeeded.Should().BeFalse();
        result.IsAuthenticationFailure.Should().BeTrue();

        account.DisplayName.Should().Be("Contato");
        account.ImapHost.Should().Be("imap.sintek.com.br");
        (await _credentials.GetSecretAsync(account.CredentialKey)).Should().Be(FakeSecret.For("anterior"));
    }

    [Fact]
    public async Task AlterarConta_TesteAceito_AplicaAlteracoesEGravaNovaSenha()
    {
        var account = ArrangeStoredAccount();
        await _credentials.SetSecretAsync(account.CredentialKey, FakeSecret.For("anterior"));

        var result = await UpdateHandler().HandleAsync(new UpdateAccountCommand
        {
            AccountId = account.Id,
            DisplayName = "Nome Novo",
            ImapHost = "imap.novo.com.br",
            SmtpHost = "smtp.novo.com.br",
            NewPassword = FakeSecret.For("nova"),
        });

        result.Succeeded.Should().BeTrue();
        account.DisplayName.Should().Be("Nome Novo");
        account.ImapHost.Should().Be("imap.novo.com.br");
        (await _credentials.GetSecretAsync(account.CredentialKey)).Should().Be(FakeSecret.For("nova"));
    }

    [Fact]
    public async Task AlterarConta_SemNovaSenha_PreservaAGuardada()
    {
        // Campo de senha vazio na tela de edição significa "não mexer", não "apagar".
        var account = ArrangeStoredAccount();
        await _credentials.SetSecretAsync(account.CredentialKey, FakeSecret.For("anterior"));

        await UpdateHandler().HandleAsync(new UpdateAccountCommand
        {
            AccountId = account.Id,
            DisplayName = "Contato",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
        });

        (await _credentials.GetSecretAsync(account.CredentialKey)).Should().Be(FakeSecret.For("anterior"));
    }

    [Fact]
    public async Task AlterarConta_DesativandoConta_DispensaOTesteDeConexao()
    {
        // Desativar uma conta cujo servidor saiu do ar precisa funcionar: exigir conexão
        // deixaria o usuário preso a uma conta que ele quer justamente parar de usar.
        var account = ArrangeStoredAccount();

        var result = await UpdateHandler().HandleAsync(new UpdateAccountCommand
        {
            AccountId = account.Id,
            DisplayName = "Contato",
            ImapHost = "imap.sintek.com.br",
            SmtpHost = "smtp.sintek.com.br",
            IsActive = false,
        });

        result.Succeeded.Should().BeTrue();
        account.IsActive.Should().BeFalse();
        account.SyncStatus.Should().Be(AccountSyncStatus.Disabled);
        await _imap.DidNotReceive().ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    // ----- Remoção -------------------------------------------------------------------

    [Fact]
    public async Task RemoverConta_SemConfirmacao_RecusaEExplicaOQueSeriaPerdido()
    {
        var account = ArrangeStoredAccount(folderCount: 2, messageCount: 17);

        var result = await RemoveHandler().HandleAsync(account.Id, confirmed: false);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("17");
        _accounts.DidNotReceive().Remove(Arg.Any<Account>());
    }

    [Fact]
    public async Task RemoverConta_ComOperacoesPendentes_AvisaQueSeraoDescartadas()
    {
        var account = ArrangeStoredAccount();

        _outbox.ListPendingAsync(account.Id, Arg.Any<CancellationToken>()).Returns(new[]
        {
            OutboxOperation.Enqueue(
                account.Id, OutboxOperationType.MoveMessage, Guid.CreateVersion7(), "{}", 1, Now),
        });

        var result = await RemoveHandler().HandleAsync(account.Id, confirmed: false);

        result.ErrorMessage.Should().Contain("aguardando sincronização");
        result.Impact!.PendingOperationCount.Should().Be(1);
    }

    [Fact]
    public async Task RemoverConta_Confirmada_ApagaPastasCredenciaisEAudita()
    {
        var account = ArrangeStoredAccount(folderCount: 2, messageCount: 3);
        await _credentials.SetSecretAsync(account.CredentialKey, FakeSecret.For("conta"));

        var result = await RemoveHandler().HandleAsync(account.Id, confirmed: true);

        result.Succeeded.Should().BeTrue();
        _accounts.Received(1).Remove(account);
        _folders.Received(2).Remove(Arg.Any<Folder>());
        _credentials.Keys.Should().BeEmpty();

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.AccountRemoved),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoverConta_ComOAuth_RevogaOConsentimentoNoProvedor()
    {
        var account = ArrangeStoredAccount();
        account.UseOAuthAuthentication(OAuthProviderKind.Microsoft, Now);

        await RemoveHandler().HandleAsync(account.Id, confirmed: true);

        await _oauthProvider.Received(1)
            .SignOutAsync("contato@sintek.com.br", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoverConta_ProvedorInacessivel_NaoImpedeARemocaoLocal()
    {
        var account = ArrangeStoredAccount();
        account.UseOAuthAuthentication(OAuthProviderKind.Microsoft, Now);

        _oauthProvider.SignOutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("sem rede"));

        var result = await RemoveHandler().HandleAsync(account.Id, confirmed: true);

        result.Succeeded.Should().BeTrue();
        _accounts.Received(1).Remove(account);
    }

    [Fact]
    public void OrdenacaoDePastas_ArvoreAninhada_RemoveDaFolhaParaARaiz()
    {
        // A relação de pasta-mãe é Restrict: remover na ordem errada esbarraria na
        // restrição de chave estrangeira.
        var accountId = Guid.CreateVersion7();
        var raiz = Folder.Create(accountId, "Projetos", FolderType.Custom, Now);
        var filha = Folder.Create(accountId, "2026", FolderType.Custom, Now, parentFolderId: raiz.Id);
        var neta = Folder.Create(accountId, "Contratos", FolderType.Custom, Now, parentFolderId: filha.Id);

        var ordered = AccountRemover.OrderByDepthDescending([raiz, filha, neta]).ToList();

        ordered.Should().ContainInOrder(neta, filha, raiz);
    }

    private Account ArrangeStoredAccount(int folderCount = 1, int messageCount = 0)
    {
        var directory = ArrangeDirectory();
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        account.ConfigureServers(
            "imap.sintek.com.br", 993, SecureSocketMode.SslOnConnect,
            "smtp.sintek.com.br", 587, SecureSocketMode.StartTls, Now);

        var folders = Enumerable.Range(0, folderCount)
            .Select(i => Folder.Create(account.Id, $"Pasta {i}", FolderType.Custom, Now))
            .ToArray();

        _accounts.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _folders.ListByAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(folders);

        foreach (var folder in folders)
        {
            _folders.CountMessagesAsync(folder.Id, Arg.Any<CancellationToken>())
                .Returns(messageCount / Math.Max(folderCount, 1));
        }

        if (folders.Length > 0)
        {
            _folders.CountMessagesAsync(folders[0].Id, Arg.Any<CancellationToken>())
                .Returns(messageCount - (messageCount / Math.Max(folderCount, 1)) * (folderCount - 1));
        }

        return account;
    }
}
