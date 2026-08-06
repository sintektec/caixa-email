using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

// A propriedade EmailAddress do assistente esconderia o tipo de mesmo nome dentro desta
// classe. O apelido devolve o acesso ao tipo sem obrigar a qualificar o namespace inteiro
// em cada uso.
using AccountAddress = Sintek.Mail.Domain.ValueObjects.EmailAddress;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Etapa corrente do assistente de configuração de conta.</summary>
public enum AccountSetupStep
{
    /// <summary>Endereço, nome exibido e Diretório de Domínio de destino.</summary>
    Address,

    /// <summary>Servidores descobertos, abertos para correção manual.</summary>
    Servers,

    /// <summary>Senha ou consentimento OAuth.</summary>
    Credentials,

    /// <summary>Teste de conexão antes de concluir.</summary>
    Verification,

    /// <summary>Conta cadastrada.</summary>
    Completed,
}

/// <summary>Um Diretório de Domínio oferecido como destino da conta.</summary>
/// <param name="Id">Identificador do diretório.</param>
/// <param name="DomainName">Domínio representado.</param>
/// <param name="Description">Descrição, quando houver.</param>
public sealed record DomainDirectoryChoice(Guid Id, string DomainName, string? Description)
{
    /// <summary>Texto exibido na lista.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? DomainName
        : $"{DomainName} — {Description}";
}

/// <summary>
/// Conduz o cadastro de uma conta, do endereço até a primeira conexão bem-sucedida.
/// </summary>
/// <remarks>
/// <para>
/// A ordem das etapas espelha a da camada de Aplicação, e por um motivo concreto: <b>o
/// Diretório de Domínio é escolhido antes de qualquer acesso à rede</b>. Descobrir
/// servidores e pedir senha de uma conta que a regra de domínio vai recusar desperdiça o
/// tempo do usuário e pode disparar bloqueio por tentativa malsucedida no provedor.
/// </para>
/// <para>
/// A regra em si <b>não</b> é reimplementada aqui. O assistente pré-seleciona o diretório
/// compatível para poupar cliques, mas quem valida é <see cref="AddAccountHandler"/> — e é
/// a mensagem dele que aparece na tela quando a validação recusa.
/// </para>
/// </remarks>
public sealed partial class AccountSetupViewModel : ObservableObject
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAutodiscoverService _autodiscover;
    private readonly AddAccountHandler _addAccount;
    private readonly TestAccountConnectionHandler _connectionTest;
    private readonly IOAuthProviderRegistry _oauthProviders;
    private readonly ILogger<AccountSetupViewModel> _logger;

    public AccountSetupViewModel(
        IDomainDirectoryRepository directories,
        IAutodiscoverService autodiscover,
        AddAccountHandler addAccount,
        TestAccountConnectionHandler connectionTest,
        IOAuthProviderRegistry oauthProviders,
        ILogger<AccountSetupViewModel> logger)
    {
        _directories = directories;
        _autodiscover = autodiscover;
        _addAccount = addAccount;
        _connectionTest = connectionTest;
        _oauthProviders = oauthProviders;
        _logger = logger;
    }

    /// <summary>Diretórios disponíveis como destino.</summary>
    public ObservableCollection<DomainDirectoryChoice> AvailableDirectories { get; } = [];

    /// <summary>Modos de proteção oferecidos nas listas de servidor.</summary>
    public IReadOnlyList<SecurityModeOption> SecurityModes => SelectionOptions.SecurityModes;

    /// <summary>Formas de autenticação oferecidas.</summary>
    public IReadOnlyList<AuthenticationOption> AuthenticationOptions => SelectionOptions.AuthenticationOptions;

    /// <summary>
    /// Forma de autenticação escolhida na lista.
    /// </summary>
    /// <remarks>
    /// Existe porque tipo de autenticação e provedor são dois campos que precisam mudar
    /// juntos: escolher "Conta Google" e deixar o provedor em Microsoft produziria um erro
    /// de autenticação sem explicação possível. A lista os move como um par só.
    /// </remarks>
    public AuthenticationOption? SelectedAuthentication
    {
        get => SelectionOptions.AuthenticationOptions
            .FirstOrDefault(o => o.Value == AuthenticationType && o.Provider == OAuthProvider);
        set
        {
            if (value is null)
            {
                return;
            }

            AuthenticationType = value.Value;
            OAuthProvider = value.Provider;
        }
    }

    /// <summary>Modo de proteção do IMAP selecionado na lista.</summary>
    public SecurityModeOption? SelectedImapSecurity
    {
        get => SelectionOptions.SecurityModes.FirstOrDefault(o => o.Value == ImapSecurity);
        set
        {
            if (value is not null)
            {
                ImapSecurity = value.Value;
            }
        }
    }

    /// <summary>Modo de proteção do SMTP selecionado na lista.</summary>
    public SecurityModeOption? SelectedSmtpSecurity
    {
        get => SelectionOptions.SecurityModes.FirstOrDefault(o => o.Value == SmtpSecurity);
        set
        {
            if (value is not null)
            {
                SmtpSecurity = value.Value;
            }
        }
    }

    /// <summary>
    /// Porta IMAP no tipo que o campo numérico da interface usa.
    /// </summary>
    /// <remarks>
    /// O NumberBox do WinUI trabalha com <c>double</c>. A conversão fica aqui, e não numa
    /// ligação de duas vias entre tipos diferentes, que o compilador de XAML recusaria.
    /// </remarks>
    public double ImapPortValue
    {
        get => ImapPort;
        set => ImapPort = (int)value;
    }

    /// <inheritdoc cref="ImapPortValue" />
    public double SmtpPortValue
    {
        get => SmtpPort;
        set => SmtpPort = (int)value;
    }

    /// <summary>Se a etapa corrente é a de endereço.</summary>
    public bool IsAddressStep => Step == AccountSetupStep.Address;

    /// <summary>Se a etapa corrente é a de servidores.</summary>
    public bool IsServersStep => Step == AccountSetupStep.Servers;

    /// <summary>Se a etapa corrente é a de credenciais.</summary>
    public bool IsCredentialsStep => Step == AccountSetupStep.Credentials;

    /// <summary>Se a etapa corrente é a de verificação concluída.</summary>
    public bool IsVerificationStep => Step == AccountSetupStep.Verification;

    /// <summary>Se o cadastro terminou.</summary>
    public bool IsCompleted => Step == AccountSetupStep.Completed;

    /// <summary>Se a senha deve ser pedida — apenas na autenticação por senha.</summary>
    public bool RequiresPassword => AuthenticationType == AuthenticationType.Password;

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Se o aviso de provedor OAuth indisponível deve aparecer.</summary>
    public bool ShowOAuthWarning => OAuthUnavailableReason is not null;

    /// <summary>Descrição de onde vieram os servidores propostos, para exibição.</summary>
    public string DiscoverySourceDescription => DiscoverySource switch
    {
        DiscoverySource.KnownProvider => "Configuração conhecida deste provedor.",
        DiscoverySource.DomainAutoconfig => "Configuração publicada pelo próprio domínio.",
        DiscoverySource.DnsSrv => "Registros SRV do DNS do domínio.",
        DiscoverySource.Ispdb => "Banco de dados público de provedores (ISPDB).",
        DiscoverySource.Convention => "Estimativa pelas convenções usuais. Confira antes de continuar.",
        _ => "Informado manualmente.",
    };

    /// <summary>Etapa corrente.</summary>
    [ObservableProperty]
    private AccountSetupStep _step = AccountSetupStep.Address;

    /// <summary>Endereço da conta.</summary>
    [ObservableProperty]
    private string _emailAddress = string.Empty;

    /// <summary>Nome exibido nas mensagens enviadas.</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Diretório escolhido.</summary>
    [ObservableProperty]
    private DomainDirectoryChoice? _selectedDirectory;

    /// <summary>Servidor IMAP.</summary>
    [ObservableProperty]
    private string _imapHost = string.Empty;

    /// <summary>Porta IMAP.</summary>
    [ObservableProperty]
    private int _imapPort = 993;

    /// <summary>Modo de proteção do IMAP.</summary>
    [ObservableProperty]
    private SecureSocketMode _imapSecurity = SecureSocketMode.SslOnConnect;

    /// <summary>Servidor SMTP.</summary>
    [ObservableProperty]
    private string _smtpHost = string.Empty;

    /// <summary>Porta SMTP.</summary>
    [ObservableProperty]
    private int _smtpPort = 587;

    /// <summary>Modo de proteção do SMTP.</summary>
    [ObservableProperty]
    private SecureSocketMode _smtpSecurity = SecureSocketMode.StartTls;

    /// <summary>Como a conta se autentica.</summary>
    [ObservableProperty]
    private AuthenticationType _authenticationType = AuthenticationType.Password;

    /// <summary>Provedor de identidade, quando OAuth.</summary>
    [ObservableProperty]
    private OAuthProviderKind _oAuthProvider = OAuthProviderKind.None;

    /// <summary>
    /// Nome de usuário, quando difere do endereço.
    /// </summary>
    /// <remarks>
    /// Vazio, não nulo: as caixas de texto do WinUI recusam <see langword="null"/> em tempo
    /// de execução, e os casos de uso já tratam vazio como ausente.
    /// </remarks>
    [ObservableProperty]
    private string _userName = string.Empty;

    /// <inheritdoc cref="UserName" />
    /// <summary>Senha digitada. Nunca sai desta instância a não ser rumo ao cofre.</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>Se a conta também sincroniza a agenda com um servidor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarUrlError))]
    [NotifyPropertyChangedFor(nameof(RequiresCalendarUrl))]
    private bool _syncCalendar;

    /// <summary>Protocolo escolhido para a agenda.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarUrlError))]
    [NotifyPropertyChangedFor(nameof(RequiresCalendarUrl))]
    [NotifyPropertyChangedFor(nameof(CalendarProtocolHint))]
    private CalendarProtocolOption _selectedCalendarProtocol = CalendarProtocolOption.Options[0];

    /// <inheritdoc cref="UserName" />
    /// <summary>
    /// Endereço do servidor de agenda.
    /// </summary>
    /// <remarks>
    /// No CalDAV basta a raiz — o principal, a coleção-raiz e os calendários vêm do próprio
    /// servidor. Fixar o caminho quebraria no iCloud, que devolve uma partição por conta.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarUrlError))]
    private string _calendarUrl = string.Empty;

    /// <summary>De onde vieram os servidores propostos.</summary>
    [ObservableProperty]
    private DiscoverySource _discoverySource = DiscoverySource.Manual;

    /// <summary>
    /// Se os servidores descobertos apontam para fora do domínio do endereço.
    /// </summary>
    /// <remarks>
    /// Hospedagem terceirizada é legítima e comum — e tem exatamente o mesmo formato de um
    /// desvio malicioso. Quando isto é verdadeiro, o assistente exige um aceite explícito
    /// em vez de seguir adiante em silêncio.
    /// </remarks>
    [ObservableProperty]
    private bool _requiresServerConfirmation;

    /// <summary>Se o usuário confirmou os servidores que apontam para fora do domínio.</summary>
    [ObservableProperty]
    private bool _serversConfirmed;

    /// <summary>Mensagem de erro ou aviso exibida na etapa corrente.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Cancela a operação em andamento; nulo quando não há nenhuma.</summary>
    private CancellationTokenSource? _running;

    /// <summary>
    /// Interrompe a operação em andamento.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Existe porque os botões do diálogo não servem enquanto ela roda.</b> O
    /// <c>ContentDialog</c> do WinUI desliga Cancelar e Voltar enquanto há um
    /// <c>Deferral</c> pendente — e o assistente segura um durante todo o teste de conexão,
    /// para o diálogo não fechar no meio e perder o que já foi preenchido. O resultado é que
    /// um servidor lento deixava a tela sem saída nenhuma, e a única alternativa era encerrar
    /// o processo. Aconteceu três vezes na validação manual.
    /// </para>
    /// <para>
    /// Os tetos de espera do caso de uso continuam valendo, e são a rede de segurança para
    /// quando ninguém está olhando. Este comando é o contrário: devolve a decisão a quem está
    /// olhando e não quer esperar.
    /// </para>
    /// </remarks>
    [RelayCommand]
    public void CancelRunning()
    {
        // O `Cancel` dispara a continuação de quem espera, que devolve a mensagem e limpa o
        // `IsBusy` no próprio `finally`. Mexer nele aqui competiria com essa limpeza.
        _running?.Cancel();
    }

    /// <summary>
    /// Prepara o cancelamento da operação que vai começar.
    /// </summary>
    private CancellationTokenSource BeginRunning(CancellationToken cancellationToken)
    {
        _running?.Dispose();
        _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return _running;
    }

    private void EndRunning()
    {
        _running?.Dispose();
        _running = null;
        IsBusy = false;
    }

    /// <summary>Resultado do último teste de conexão.</summary>
    [ObservableProperty]
    private TestAccountConnectionResult? _lastTestResult;

    /// <summary>Conta criada ao final.</summary>
    [ObservableProperty]
    private Guid? _createdAccountId;

    /// <summary>
    /// Domínio do endereço digitado, quando ele é válido — o que o assistente sugere criar
    /// caso nenhum diretório aceite a conta.
    /// </summary>
    public string? SuggestedDomainName
        => AccountAddress.TryParse(EmailAddress, out var address) ? address.Domain.Value : null;

    /// <summary>Se nenhum diretório existente aceita o endereço digitado.</summary>
    [ObservableProperty]
    private bool _needsNewDirectory;

    /// <summary>Protocolos de agenda oferecidos.</summary>
    public IReadOnlyList<CalendarProtocolOption> CalendarProtocols => CalendarProtocolOption.Options;

    /// <summary>
    /// Se o protocolo escolhido precisa de um endereço digitado.
    /// </summary>
    /// <remarks>
    /// Só o CalDAV precisa: o Graph e a Calendar API têm endereço fixo e conhecido, e pedir
    /// que o usuário o digite seria pedir que ele acerte um valor que o programa já sabe.
    /// </remarks>
    public bool RequiresCalendarUrl
        => SyncCalendar && SelectedCalendarProtocol.Provider == CalendarProviderKind.CalDav;

    /// <summary>Explicação do que o protocolo escolhido exige.</summary>
    public string CalendarProtocolHint => SelectedCalendarProtocol.Hint;

    /// <summary>
    /// O endereço que vai para o cadastro.
    /// </summary>
    /// <remarks>
    /// Graph e Google não têm endereço a digitar, mas a conta precisa de um valor não vazio
    /// para que <c>ConfigureCalendar</c> ligue a sincronização — é ele que distingue "sem
    /// servidor de agenda" de "com servidor". O endereço fixo do serviço cumpre esse papel.
    /// </remarks>
    public string? EffectiveCalendarUrl => SelectedCalendarProtocol.Provider switch
    {
        CalendarProviderKind.CalDav => CalendarUrl,
        CalendarProviderKind.MicrosoftGraph => "https://graph.microsoft.com/v1.0/",
        CalendarProviderKind.GoogleCalendar => "https://www.googleapis.com/calendar/v3/",
        _ => null,
    };

    /// <summary>
    /// Erro de validação do endereço de agenda, exibido enquanto se digita.
    /// </summary>
    /// <remarks>
    /// Vazio quando não há erro — e não nulo — porque o destino é um <c>TextBlock</c>, que
    /// recusa <see langword="null"/> em tempo de execução.
    /// </remarks>
    public string CalendarUrlError
    {
        get
        {
            if (!SyncCalendar || !RequiresCalendarUrl || string.IsNullOrWhiteSpace(CalendarUrl))
            {
                return string.Empty;
            }

            return Uri.TryCreate(CalendarUrl.Trim(), UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                    ? string.Empty
                    // Basic sobre HTTP é a senha em claro no fio, e o host vem do que o
                    // usuário digitou.
                    : "O endereço do servidor de agenda precisa começar com https://.";
        }
    }

    /// <summary>Provedores OAuth efetivamente configurados nesta instalação.</summary>
    public IReadOnlyList<OAuthProviderKind> ConfiguredOAuthProviders
        => _oauthProviders.ConfiguredProviders.Select(p => p.Provider).ToList();

    /// <summary>
    /// Motivo pelo qual a autenticação OAuth escolhida não está disponível, quando for o
    /// caso.
    /// </summary>
    /// <remarks>
    /// Explicar que falta configuração é diferente de falhar na autenticação: a ação
    /// necessária é do administrador, não do usuário, e mandá-lo procurar uma senha não
    /// resolveria nada.
    /// </remarks>
    public string? OAuthUnavailableReason
    {
        get
        {
            if (AuthenticationType != AuthenticationType.OAuth2)
            {
                return null;
            }

            var provider = _oauthProviders.Resolve(OAuthProvider);

            if (provider is null)
            {
                return $"Não há suporte a autenticação {OAuthProvider} nesta versão.";
            }

            return provider.IsConfigured
                ? null
                : $"A autenticação {OAuthProvider} ainda não foi configurada nesta instalação. " +
                  "É preciso registrar o aplicativo no provedor e informar o Client ID.";
        }
    }

    /// <summary>Carrega os diretórios disponíveis.</summary>
    [RelayCommand]
    public async Task LoadDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        AvailableDirectories.Clear();

        foreach (var directory in await _directories.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            AvailableDirectories.Add(
                new DomainDirectoryChoice(directory.Id, directory.DomainName.Value, directory.Description));
        }
    }

    /// <summary>
    /// Valida o endereço, escolhe o diretório compatível e descobre os servidores.
    /// </summary>
    /// <remarks>
    /// A pré-seleção do diretório usa <see cref="DomainDirectory.Accepts"/> — a mesma regra
    /// que o cadastro aplicará. Repetir a comparação de domínio aqui, à mão, criaria uma
    /// segunda versão da regra, e as duas divergiriam na primeira mudança.
    /// </remarks>
    [RelayCommand]
    public async Task ContinueFromAddressAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = null;

        if (!AccountAddress.TryParse(EmailAddress, out var address, out var parseError))
        {
            StatusMessage = parseError;
            return;
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = address.LocalPart;
        }

        IsBusy = true;

        try
        {
            await ResolveDirectoryAsync(address, cancellationToken).ConfigureAwait(true);

            if (NeedsNewDirectory)
            {
                StatusMessage =
                    $"Nenhum Diretório de Domínio representa '{address.Domain.Value}'. " +
                    "Crie o diretório antes de vincular esta conta.";
                return;
            }

            await DiscoverServersAsync(address, cancellationToken).ConfigureAwait(true);

            Step = AccountSetupStep.Servers;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Aceita os servidores e segue para as credenciais.</summary>
    [RelayCommand]
    public void ContinueFromServers()
    {
        StatusMessage = null;

        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost))
        {
            StatusMessage = "Informe os servidores IMAP e SMTP.";
            return;
        }

        if (RequiresServerConfirmation && !ServersConfirmed)
        {
            StatusMessage =
                "Os servidores propostos não pertencem ao domínio do endereço. " +
                "Confirme que eles estão corretos antes de continuar.";
            return;
        }

        Step = AccountSetupStep.Credentials;
    }

    /// <summary>Testa a configuração antes de concluir.</summary>
    [RelayCommand]
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = null;
        IsBusy = true;

        using var running = BeginRunning(cancellationToken);

        try
        {
            var result = await _connectionTest.HandleAsync(
                new TestAccountConnectionCommand
                {
                    EmailAddress = EmailAddress,
                    ImapHost = ImapHost,
                    ImapPort = ImapPort,
                    ImapSecurity = ImapSecurity,
                    SmtpHost = SmtpHost,
                    SmtpPort = SmtpPort,
                    SmtpSecurity = SmtpSecurity,
                    AuthenticationType = AuthenticationType,
                    OAuthProvider = OAuthProvider,
                    UserName = UserName,
                    Password = Password,
                    CalendarProvider = SyncCalendar
                        ? SelectedCalendarProtocol.Provider
                        : CalendarProviderKind.None,
                    CalendarUrl = SyncCalendar ? EffectiveCalendarUrl : null,
                },
                running.Token).ConfigureAwait(true);

            LastTestResult = result;

            if (!result.Succeeded)
            {
                StatusMessage = result.FirstError;
                return;
            }

            Step = AccountSetupStep.Verification;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Interrompido por quem estava esperando, e não pelo fechamento da janela.
            StatusMessage = "O teste foi interrompido. Confira os dados e tente de novo.";
        }
        finally
        {
            EndRunning();
        }
    }

    /// <summary>Conclui o cadastro.</summary>
    [RelayCommand]
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = null;

        if (SelectedDirectory is not { } directory)
        {
            StatusMessage = "Escolha o Diretório de Domínio ao qual a conta pertence.";
            return;
        }

        if (CalendarUrlError.Length > 0)
        {
            StatusMessage = CalendarUrlError;
            return;
        }

        if (SyncCalendar && RequiresCalendarUrl && string.IsNullOrWhiteSpace(CalendarUrl))
        {
            StatusMessage = "Informe o endereço do servidor de agenda.";
            return;
        }

        IsBusy = true;

        using var running = BeginRunning(cancellationToken);

        try
        {
            var result = await _addAccount.HandleAsync(
                new AddAccountCommand
                {
                    DomainDirectoryId = directory.Id,
                    EmailAddress = EmailAddress,
                    DisplayName = DisplayName,
                    ImapHost = ImapHost,
                    ImapPort = ImapPort,
                    ImapSecurity = ImapSecurity,
                    SmtpHost = SmtpHost,
                    SmtpPort = SmtpPort,
                    SmtpSecurity = SmtpSecurity,
                    AuthenticationType = AuthenticationType,
                    OAuthProvider = OAuthProvider,
                    UserName = UserName,
                    Password = Password,
                    CalendarProvider = SyncCalendar
                        ? SelectedCalendarProtocol.Provider
                        : CalendarProviderKind.None,
                    CalendarUrl = SyncCalendar ? EffectiveCalendarUrl : null,
                },
                running.Token).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage;
                return;
            }

            // A senha some da memória do assistente assim que deixa de ser necessária.
            Password = string.Empty;
            CreatedAccountId = result.AccountId;
            Step = AccountSetupStep.Completed;
        }
        catch (DomainMismatchException ex)
        {
            // Mensagem literal da especificação, redigida para leitura do usuário.
            StatusMessage = ex.UserMessage;
            _logger.LogInformation("Cadastro recusado pela regra de Diretório de Domínio.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "O cadastro foi interrompido. Nada foi gravado.";
        }
        finally
        {
            EndRunning();
        }
    }

    /// <summary>Volta uma etapa.</summary>
    [RelayCommand]
    public void GoBack()
    {
        StatusMessage = null;

        Step = Step switch
        {
            AccountSetupStep.Servers => AccountSetupStep.Address,
            AccountSetupStep.Credentials => AccountSetupStep.Servers,
            AccountSetupStep.Verification => AccountSetupStep.Credentials,
            _ => Step,
        };
    }

    private async Task ResolveDirectoryAsync(AccountAddress address, CancellationToken cancellationToken)
    {
        var directories = await _directories.ListAsync(cancellationToken).ConfigureAwait(true);

        AvailableDirectories.Clear();

        foreach (var directory in directories)
        {
            AvailableDirectories.Add(
                new DomainDirectoryChoice(directory.Id, directory.DomainName.Value, directory.Description));
        }

        var match = directories.FirstOrDefault(d => d.Accepts(address));

        if (match is null)
        {
            NeedsNewDirectory = true;
            SelectedDirectory = null;
            return;
        }

        NeedsNewDirectory = false;
        SelectedDirectory = new DomainDirectoryChoice(match.Id, match.DomainName.Value, match.Description);
    }

    private async Task DiscoverServersAsync(AccountAddress address, CancellationToken cancellationToken)
    {
        var discovered = await _autodiscover.DiscoverAsync(address.Value, cancellationToken).ConfigureAwait(true);

        if (discovered is not { } settings)
        {
            DiscoverySource = DiscoverySource.Manual;
            RequiresServerConfirmation = false;
            StatusMessage = "Não foi possível descobrir os servidores. Informe IMAP e SMTP manualmente.";
            return;
        }

        ImapHost = settings.ImapHost;
        ImapPort = settings.ImapPort;
        ImapSecurity = settings.ImapSecurity;
        SmtpHost = settings.SmtpHost;
        SmtpPort = settings.SmtpPort;
        SmtpSecurity = settings.SmtpSecurity;
        AuthenticationType = settings.RecommendedAuthentication;
        OAuthProvider = settings.OAuthProvider;
        DiscoverySource = settings.Source;
        RequiresServerConfirmation = settings.RequiresUserConfirmation;
        ServersConfirmed = false;
    }

    partial void OnEmailAddressChanged(string value)
        => OnPropertyChanged(nameof(SuggestedDomainName));

    partial void OnAuthenticationTypeChanged(AuthenticationType value)
    {
        OnPropertyChanged(nameof(OAuthUnavailableReason));
        OnPropertyChanged(nameof(ShowOAuthWarning));
        OnPropertyChanged(nameof(RequiresPassword));
        OnPropertyChanged(nameof(SelectedAuthentication));
    }

    partial void OnOAuthProviderChanged(OAuthProviderKind value)
    {
        OnPropertyChanged(nameof(OAuthUnavailableReason));
        OnPropertyChanged(nameof(ShowOAuthWarning));
        OnPropertyChanged(nameof(SelectedAuthentication));
    }

    partial void OnStepChanged(AccountSetupStep value)
    {
        OnPropertyChanged(nameof(IsAddressStep));
        OnPropertyChanged(nameof(IsServersStep));
        OnPropertyChanged(nameof(IsCredentialsStep));
        OnPropertyChanged(nameof(IsVerificationStep));
        OnPropertyChanged(nameof(IsCompleted));
    }

    partial void OnStatusMessageChanged(string? value)
        => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnDiscoverySourceChanged(DiscoverySource value)
        => OnPropertyChanged(nameof(DiscoverySourceDescription));

    partial void OnImapSecurityChanged(SecureSocketMode value)
        => OnPropertyChanged(nameof(SelectedImapSecurity));

    partial void OnSmtpSecurityChanged(SecureSocketMode value)
        => OnPropertyChanged(nameof(SelectedSmtpSecurity));

    partial void OnImapPortChanged(int value) => OnPropertyChanged(nameof(ImapPortValue));

    partial void OnSmtpPortChanged(int value) => OnPropertyChanged(nameof(SmtpPortValue));
}
