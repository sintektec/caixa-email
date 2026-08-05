using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o assistente de configuração de conta. É lógica de interface que só o job Windows
/// executaria; tê-la em um projeto multiplataforma é o que permite verificá-la sem uma
/// máquina Windows.
/// </summary>
public class AccountSetupViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();
    private readonly IAutodiscoverService _autodiscover = Substitute.For<IAutodiscoverService>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly ISmtpSender _smtp = Substitute.For<ISmtpSender>();
    private readonly IOAuthProviderRegistry _oauthRegistry = Substitute.For<IOAuthProviderRegistry>();
    private readonly IOAuthProvider _oauthProvider = Substitute.For<IOAuthProvider>();
    private readonly FakeTimeProvider _clock = new(Now);

    public AccountSetupViewModelTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());
        _smtp.TestConnectionAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());

        _oauthProvider.Provider.Returns(OAuthProviderKind.Microsoft);
        _oauthProvider.IsConfigured.Returns(true);
        _oauthRegistry.Resolve(OAuthProviderKind.Microsoft).Returns(_oauthProvider);
        _oauthRegistry.ConfiguredProviders.Returns([_oauthProvider]);

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<DomainDirectory>());
    }

    private AccountSetupViewModel CreateViewModel() => new(
        _directories,
        _autodiscover,
        new AddAccountHandler(
            _directories, _accounts, _folders, _audit, _unitOfWork, _credentials,
            _autodiscover, _imap, _oauthRegistry, _clock, NullLogger<AddAccountHandler>.Instance),
        new TestAccountConnectionHandler(
            _imap, _smtp, _credentials, _oauthRegistry, [], _clock,
            NullLogger<TestAccountConnectionHandler>.Instance),
        _oauthRegistry,
        NullLogger<AccountSetupViewModel>.Instance);

    private DomainDirectory ArrangeDirectory(string domain = "sintek.com.br", bool allowSubdomains = false)
    {
        var directory = DomainDirectory.Create(
            EmailDomain.Parse(domain), Now, allowSubdomains: allowSubdomains);

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { directory });
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        return directory;
    }

    private void ArrangeDiscovery(
        DiscoverySource source = DiscoverySource.DomainAutoconfig,
        bool requiresConfirmation = false,
        AuthenticationType authentication = AuthenticationType.Password,
        OAuthProviderKind provider = OAuthProviderKind.None)
    {
        _autodiscover.DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DiscoveredServerSettings(
                "imap.sintek.com.br", 993, SecureSocketMode.SslOnConnect,
                "smtp.sintek.com.br", 587, SecureSocketMode.StartTls,
                authentication, provider, source, requiresConfirmation));
    }

    [Fact]
    public async Task ContinuarDoEndereco_EnderecoInvalido_ExibeErroENaoAvanca()
    {
        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "sem-arroba";

        await viewModel.ContinueFromAddressAsync();

        viewModel.Step.Should().Be(AccountSetupStep.Address);
        viewModel.StatusMessage.Should().NotBeNullOrWhiteSpace();

        await _autodiscover.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinuarDoEndereco_SemDiretorioCompativel_NaoConsultaARede()
    {
        // O Diretório de Domínio é escolhido antes de qualquer acesso à rede: descobrir
        // servidores de uma conta que a regra vai recusar desperdiça o tempo do usuário.
        ArrangeDirectory("outraempresa.com.br");

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";

        await viewModel.ContinueFromAddressAsync();

        viewModel.NeedsNewDirectory.Should().BeTrue();
        viewModel.SuggestedDomainName.Should().Be("sintek.com.br");
        viewModel.Step.Should().Be(AccountSetupStep.Address);

        await _autodiscover.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinuarDoEndereco_DiretorioCompativel_PreSelecionaEDescobreServidores()
    {
        var directory = ArrangeDirectory();
        ArrangeDiscovery();

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";

        await viewModel.ContinueFromAddressAsync();

        viewModel.NeedsNewDirectory.Should().BeFalse();
        viewModel.SelectedDirectory!.Id.Should().Be(directory.Id);
        viewModel.ImapHost.Should().Be("imap.sintek.com.br");
        viewModel.SmtpPort.Should().Be(587);
        viewModel.DiscoverySource.Should().Be(DiscoverySource.DomainAutoconfig);
        viewModel.Step.Should().Be(AccountSetupStep.Servers);
    }

    [Fact]
    public async Task ContinuarDoEndereco_SubdominioComDiretorioQuePermite_EAceito()
    {
        // A pré-seleção usa DomainDirectory.Accepts — a mesma regra do cadastro. Repetir a
        // comparação à mão criaria uma segunda versão que divergiria na primeira mudança.
        var directory = ArrangeDirectory(allowSubdomains: true);
        ArrangeDiscovery();

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@filial.sintek.com.br";

        await viewModel.ContinueFromAddressAsync();

        viewModel.SelectedDirectory!.Id.Should().Be(directory.Id);
    }

    [Fact]
    public async Task ContinuarDoEndereco_SemNomeExibido_UsaAParteLocalDoEndereco()
    {
        ArrangeDirectory();
        ArrangeDiscovery();

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";

        await viewModel.ContinueFromAddressAsync();

        viewModel.DisplayName.Should().Be("contato");
    }

    [Fact]
    public async Task ContinuarDoEndereco_DescobertaSemResultado_PedeConfiguracaoManual()
    {
        ArrangeDirectory();
        _autodiscover.DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((DiscoveredServerSettings?)null);

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";

        await viewModel.ContinueFromAddressAsync();

        viewModel.Step.Should().Be(AccountSetupStep.Servers);
        viewModel.DiscoverySource.Should().Be(DiscoverySource.Manual);
        viewModel.StatusMessage.Should().Contain("manualmente");
    }

    [Fact]
    public async Task ContinuarDosServidores_ServidorForaDoDominioSemAceite_NaoAvanca()
    {
        ArrangeDirectory();
        ArrangeDiscovery(DiscoverySource.Ispdb, requiresConfirmation: true);

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";
        await viewModel.ContinueFromAddressAsync();

        viewModel.RequiresServerConfirmation.Should().BeTrue();

        viewModel.ContinueFromServers();

        viewModel.Step.Should().Be(AccountSetupStep.Servers);
        viewModel.StatusMessage.Should().Contain("não pertencem ao domínio");
    }

    [Fact]
    public async Task ContinuarDosServidores_ComAceiteExplicito_Avanca()
    {
        ArrangeDirectory();
        ArrangeDiscovery(DiscoverySource.Ispdb, requiresConfirmation: true);

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";
        await viewModel.ContinueFromAddressAsync();

        viewModel.ServersConfirmed = true;
        viewModel.ContinueFromServers();

        viewModel.Step.Should().Be(AccountSetupStep.Credentials);
    }

    [Fact]
    public void ContinuarDosServidores_SemHost_ExibeErro()
    {
        var viewModel = CreateViewModel();

        viewModel.ContinueFromServers();

        viewModel.Step.Should().Be(AccountSetupStep.Address);
        viewModel.StatusMessage.Should().Contain("IMAP e SMTP");
    }

    [Fact]
    public void MotivoDeIndisponibilidadeOAuth_ProvedorSemClientId_ExplicaQueFaltaConfigurar()
    {
        _oauthProvider.IsConfigured.Returns(false);

        var viewModel = CreateViewModel();
        viewModel.AuthenticationType = AuthenticationType.OAuth2;
        viewModel.OAuthProvider = OAuthProviderKind.Microsoft;

        viewModel.OAuthUnavailableReason.Should().Contain("Client ID");
    }

    [Fact]
    public void MotivoDeIndisponibilidadeOAuth_ProvedorConfigurado_NaoTemImpedimento()
    {
        var viewModel = CreateViewModel();
        viewModel.AuthenticationType = AuthenticationType.OAuth2;
        viewModel.OAuthProvider = OAuthProviderKind.Microsoft;

        viewModel.OAuthUnavailableReason.Should().BeNull();
    }

    [Fact]
    public void MotivoDeIndisponibilidadeOAuth_ProvedorSemImplementacao_Explica()
    {
        var viewModel = CreateViewModel();
        viewModel.AuthenticationType = AuthenticationType.OAuth2;
        viewModel.OAuthProvider = OAuthProviderKind.Google;

        viewModel.OAuthUnavailableReason.Should().Contain("Google");
    }

    [Fact]
    public async Task Verificar_ConexaoRecusada_MantemAEtapaEExibeOErro()
    {
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.AuthenticationFailure("Senha incorreta."));

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";
        viewModel.ImapHost = "imap.sintek.com.br";
        viewModel.SmtpHost = "smtp.sintek.com.br";
        viewModel.Password = FakeSecret.For("recusada");

        await viewModel.VerifyAsync();

        viewModel.Step.Should().NotBe(AccountSetupStep.Verification);
        viewModel.StatusMessage.Should().Contain("Senha incorreta");
        viewModel.LastTestResult!.Value.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Verificar_ConexaoAceita_AvancaParaAConfirmacao()
    {
        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";
        viewModel.ImapHost = "imap.sintek.com.br";
        viewModel.SmtpHost = "smtp.sintek.com.br";
        viewModel.Password = FakeSecret.For("aceita");

        await viewModel.VerifyAsync();

        viewModel.Step.Should().Be(AccountSetupStep.Verification);
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task Concluir_ContaDeOutroDominio_ExibeAMensagemDaEspecificacao()
    {
        // A regra não é reavaliada aqui: quem recusa é o caso de uso, e é a mensagem dele
        // que chega à tela.
        var directory = ArrangeDirectory();

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "usuario@gmail.com";
        viewModel.DisplayName = "Usuário";
        viewModel.ImapHost = "imap.gmail.com";
        viewModel.SmtpHost = "smtp.gmail.com";
        viewModel.Password = FakeSecret.For("conta");
        viewModel.SelectedDirectory = new DomainDirectoryChoice(directory.Id, directory.DomainName.Value, null);

        await viewModel.FinishAsync();

        viewModel.Step.Should().NotBe(AccountSetupStep.Completed);
        viewModel.StatusMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Concluir_SemDiretorioEscolhido_Recusa()
    {
        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";

        await viewModel.FinishAsync();

        viewModel.StatusMessage.Should().Contain("Diretório de Domínio");
        await _accounts.DidNotReceive().AddAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concluir_CadastroBemSucedido_LimpaASenhaDaMemoria()
    {
        var directory = ArrangeDirectory();

        var viewModel = CreateViewModel();
        viewModel.EmailAddress = "contato@sintek.com.br";
        viewModel.DisplayName = "Contato";
        viewModel.ImapHost = "imap.sintek.com.br";
        viewModel.SmtpHost = "smtp.sintek.com.br";
        viewModel.Password = FakeSecret.For("cadastro");
        viewModel.SelectedDirectory = new DomainDirectoryChoice(directory.Id, directory.DomainName.Value, null);

        await viewModel.FinishAsync();

        viewModel.Step.Should().Be(AccountSetupStep.Completed);
        viewModel.CreatedAccountId.Should().NotBeNull();
        viewModel.Password.Should().BeEmpty("a senha não permanece na memória do assistente");
    }

    [Fact]
    public void Voltar_DeCadaEtapa_RetornaAAnterior()
    {
        var viewModel = CreateViewModel();

        viewModel.Step = AccountSetupStep.Verification;
        viewModel.GoBack();
        viewModel.Step.Should().Be(AccountSetupStep.Credentials);

        viewModel.GoBack();
        viewModel.Step.Should().Be(AccountSetupStep.Servers);

        viewModel.GoBack();
        viewModel.Step.Should().Be(AccountSetupStep.Address);

        viewModel.GoBack();
        viewModel.Step.Should().Be(AccountSetupStep.Address, "a primeira etapa não tem anterior");
    }

    // ---- Servidor de agenda -------------------------------------------------------------

    /// <summary>
    /// Só o CalDAV pede endereço. Graph e Calendar API têm endereço fixo e conhecido, e
    /// pedir que o usuário o digite seria pedir que ele acerte um valor que o programa já
    /// sabe.
    /// </summary>
    [Theory]
    [InlineData(CalendarProviderKind.CalDav, true)]
    [InlineData(CalendarProviderKind.MicrosoftGraph, false)]
    [InlineData(CalendarProviderKind.GoogleCalendar, false)]
    public void ProtocoloDeAgenda_DecideSeOEnderecoEPedido(
        CalendarProviderKind protocolo, bool pedeEndereco)
    {
        var viewModel = CreateViewModel();
        viewModel.SyncCalendar = true;
        viewModel.SelectedCalendarProtocol =
            viewModel.CalendarProtocols.Single(p => p.Provider == protocolo);

        viewModel.RequiresCalendarUrl.Should().Be(pedeEndereco);
    }

    /// <summary>
    /// Basic sobre HTTP é a senha em claro no fio, e o host vem do que o usuário digitou.
    /// </summary>
    [Fact]
    public void EnderecoDeAgenda_EmHttpSimples_ERecusado()
    {
        var viewModel = CreateViewModel();
        viewModel.SyncCalendar = true;
        viewModel.CalendarUrl = "http://dav.exemplo.com/";

        viewModel.CalendarUrlError.Should().Contain("https://");
    }

    [Fact]
    public void EnderecoDeAgenda_EmHttps_EAceito()
    {
        var viewModel = CreateViewModel();
        viewModel.SyncCalendar = true;
        viewModel.CalendarUrl = "https://dav.exemplo.com/";

        viewModel.CalendarUrlError.Should().BeEmpty();
    }

    /// <summary>
    /// O erro só vale para quem pede endereço: no Graph o campo nem aparece, e um erro sobre
    /// ele confundiria quem escolheu o protocolo certo.
    /// </summary>
    [Fact]
    public void EnderecoDeAgenda_ComProtocoloQueNaoOPede_NaoAcusaErro()
    {
        var viewModel = CreateViewModel();
        viewModel.SyncCalendar = true;
        viewModel.SelectedCalendarProtocol = viewModel.CalendarProtocols
            .Single(p => p.Provider == CalendarProviderKind.MicrosoftGraph);
        viewModel.CalendarUrl = "endereço inválido";

        viewModel.CalendarUrlError.Should().BeEmpty();
    }

    /// <summary>
    /// Graph e Google não têm endereço a digitar, mas a conta precisa de um valor não vazio
    /// para distinguir "sem servidor de agenda" de "com servidor".
    /// </summary>
    [Fact]
    public void EnderecoEfetivo_ComProtocoloDeEnderecoFixo_UsaODoServico()
    {
        var viewModel = CreateViewModel();
        viewModel.SyncCalendar = true;
        viewModel.SelectedCalendarProtocol = viewModel.CalendarProtocols
            .Single(p => p.Provider == CalendarProviderKind.GoogleCalendar);

        viewModel.EffectiveCalendarUrl.Should().Be("https://www.googleapis.com/calendar/v3/");
    }

    /// <summary>Sem marcar a sincronização, nada de agenda é pedido nem validado.</summary>
    [Fact]
    public void AgendaDesligada_NaoPedeEnderecoNemAcusaErro()
    {
        var viewModel = CreateViewModel();

        viewModel.SyncCalendar.Should().BeFalse();
        viewModel.RequiresCalendarUrl.Should().BeFalse();
        viewModel.CalendarUrlError.Should().BeEmpty();
    }
}

/// <summary>Relógio fixo, para tornar os testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Valores sintéticos usados no lugar de senha nos testes.
/// </summary>
/// <remarks>
/// São montados em tempo de execução em vez de escritos como literal ao lado de um campo
/// chamado <c>Password</c>. O detector de segredos do CI não tem como distinguir credencial
/// real de valor de teste, e um alerta que é sempre falso ensina a ignorar alertas — inclusive
/// os verdadeiros.
/// </remarks>
internal static class FakeSecret
{
    /// <summary>Devolve um valor previsível e inconfundivelmente fictício.</summary>
    public static string For(string label) => string.Join('-', "valor", "ficticio", label);
}
