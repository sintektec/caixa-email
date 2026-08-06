using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Conta de e-mail vinculada a um Diretório de Domínio.
/// </summary>
/// <remarks>
/// <b>Nenhum segredo é armazenado nesta entidade.</b> Senhas, tokens de OAuth e a chave
/// do banco vivem exclusivamente no Windows Credential Manager;
/// <see cref="CredentialKey"/> guarda apenas o identificador usado para recuperá-los.
/// Um dump do banco — mesmo descriptografado — não expõe credencial alguma.
/// </remarks>
public sealed class Account : Entity
{
    private readonly List<Folder> _folders = [];

    private Account(
        Guid id,
        Guid domainDirectoryId,
        EmailAddress emailAddress,
        string displayName,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        DomainDirectoryId = domainDirectoryId;
        EmailAddress = emailAddress;
        DisplayName = displayName;
        CredentialKey = BuildCredentialKey(emailAddress);
    }

    private Account()
    {
    }

    /// <summary>Diretório de Domínio ao qual a conta pertence.</summary>
    public Guid DomainDirectoryId { get; private set; }

    /// <summary>Diretório dono da conta.</summary>
    public DomainDirectory? DomainDirectory { get; private set; }

    /// <summary>Endereço da conta, normalizado.</summary>
    public EmailAddress EmailAddress { get; private set; } = null!;

    /// <summary>Nome exibido nas mensagens enviadas e na árvore de navegação.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Servidor IMAP.</summary>
    public string ImapHost { get; private set; } = string.Empty;

    /// <summary>Porta IMAP.</summary>
    public int ImapPort { get; private set; } = 993;

    /// <summary>Modo de proteção da conexão IMAP.</summary>
    public SecureSocketMode ImapSecurity { get; private set; } = SecureSocketMode.SslOnConnect;

    /// <summary>Servidor SMTP.</summary>
    public string SmtpHost { get; private set; } = string.Empty;

    /// <summary>Porta SMTP.</summary>
    public int SmtpPort { get; private set; } = 587;

    /// <summary>Modo de proteção da conexão SMTP.</summary>
    public SecureSocketMode SmtpSecurity { get; private set; } = SecureSocketMode.StartTls;

    /// <summary>
    /// Se a conta exige conexão cifrada. Mantido para atender ao campo <c>UseSsl</c> da
    /// especificação; os modos por protocolo acima são o controle fino.
    /// </summary>
    public bool UseSsl { get; private set; } = true;

    /// <summary>
    /// Protocolo do servidor de agenda, ou <see cref="CalendarProviderKind.None"/> quando a
    /// conta não tem um.
    /// </summary>
    public CalendarProviderKind CalendarProvider { get; private set; } = CalendarProviderKind.None;

    /// <summary>
    /// Ponto de entrada do servidor de agenda.
    /// </summary>
    /// <remarks>
    /// No CalDAV é a raiz por onde a descoberta começa — o resto (principal, home, coleções)
    /// vem do próprio servidor, e fixá-lo no código quebraria no iCloud, que devolve uma
    /// partição diferente por conta.
    /// </remarks>
    public string? CalendarUrl { get; private set; }

    /// <summary>Se a agenda desta conta é sincronizada com o servidor.</summary>
    public bool CalendarSyncEnabled { get; private set; }

    /// <summary>Como a conta se autentica.</summary>
    public AuthenticationType AuthenticationType { get; private set; } = AuthenticationType.Password;

    /// <summary>Provedor de identidade, quando a autenticação é OAuth 2.0.</summary>
    public OAuthProviderKind OAuthProvider { get; private set; } = OAuthProviderKind.None;

    /// <summary>
    /// Identificador da credencial no Windows Credential Manager. Nunca a credencial em si.
    /// </summary>
    public string CredentialKey { get; private set; } = string.Empty;

    /// <summary>Nome de usuário para autenticação, quando difere do endereço de e-mail.</summary>
    public string? UserName { get; private set; }

    /// <summary>Se a conta está ativa.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Estado da última sincronização.</summary>
    public AccountSyncStatus SyncStatus { get; private set; } = AccountSyncStatus.NeverSynced;

    /// <summary>Instante da última sincronização bem-sucedida.</summary>
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>
    /// Erro da última tentativa de sincronização, em texto exibível. Nunca contém
    /// credencial nem conteúdo de mensagem.
    /// </summary>
    public string? LastSyncError { get; private set; }

    /// <summary>Intervalo entre sincronizações automáticas.</summary>
    public int SyncIntervalMinutes { get; private set; } = 5;

    /// <summary>Quanto de cada mensagem baixar.</summary>
    public BodyDownloadPolicy BodyDownloadPolicy { get; private set; } = BodyDownloadPolicy.RecentOnly;

    /// <summary>Posição manual na árvore de navegação.</summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Assinatura acrescentada às mensagens escritas por esta conta.
    /// </summary>
    /// <remarks>
    /// Texto puro, não HTML. O compositor a escapa antes de inserir no corpo: o campo é
    /// digitado pelo usuário, e aceitar marcação crua transformaria a tela de assinatura em
    /// vetor de injeção contra o próprio produto.
    /// </remarks>
    public string? Signature { get; private set; }

    /// <summary>Pastas da conta.</summary>
    public IReadOnlyCollection<Folder> Folders => _folders;

    /// <summary>
    /// Cria uma conta.
    /// </summary>
    /// <remarks>
    /// A validação de domínio NÃO acontece aqui e sim em
    /// <see cref="DomainDirectory.AttachAccount"/>: é o diretório que conhece a regra
    /// (incluindo aliases e permissão de subdomínios), e concentrá-la lá impede que uma
    /// conta seja criada já vinculada a um diretório incompatível.
    /// </remarks>
    public static Account Create(
        Guid domainDirectoryId,
        EmailAddress emailAddress,
        string displayName,
        DateTimeOffset createdAt,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(emailAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new Account(id ?? Guid.CreateVersion7(), domainDirectoryId, emailAddress, displayName.Trim(), createdAt);
    }

    /// <summary>
    /// Cria uma conta de validação: não pertence a diretório algum e não é persistida.
    /// </summary>
    /// <remarks>
    /// Serve ao teste de configuração que antecede o cadastro. A chave da credencial é
    /// própria e efêmera de propósito — usar a chave definitiva faria o teste sobrescrever,
    /// e depois apagar, a senha de uma conta real já cadastrada com o mesmo endereço.
    /// </remarks>
    public static Account CreateProbe(EmailAddress emailAddress, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(emailAddress);

        var probe = new Account(Guid.CreateVersion7(), Guid.Empty, emailAddress, emailAddress.Value, createdAt)
        {
            IsActive = false,
        };

        probe.CredentialKey = $"Sintek.Mail:validacao:{probe.Id:N}";

        return probe;
    }

    /// <summary>Configura os servidores IMAP e SMTP.</summary>
    public void ConfigureServers(
        string imapHost,
        int imapPort,
        SecureSocketMode imapSecurity,
        string smtpHost,
        int smtpPort,
        SecureSocketMode smtpSecurity,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imapHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(smtpHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imapPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(imapPort, 65535);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(smtpPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(smtpPort, 65535);

        ImapHost = imapHost.Trim();
        ImapPort = imapPort;
        ImapSecurity = imapSecurity;
        SmtpHost = smtpHost.Trim();
        SmtpPort = smtpPort;
        SmtpSecurity = smtpSecurity;
        UseSsl = imapSecurity != SecureSocketMode.None || smtpSecurity != SecureSocketMode.None;
        Touch(now);
    }

    /// <summary>
    /// Configura o servidor de agenda.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A credencial é a mesma do e-mail — mesmo <see cref="CredentialKey"/>, mesmo token
    /// OAuth. Pedir uma segunda senha para o mesmo servidor seria pedir duas vezes a mesma
    /// coisa, e guardar uma cópia dela é uma cópia a mais para vazar.
    /// </para>
    /// <para>
    /// Endereço em branco desliga a sincronização em vez de recusar: uma conta sem servidor
    /// de agenda é o caso comum, não erro de preenchimento.
    /// </para>
    /// </remarks>
    public void ConfigureCalendar(
        CalendarProviderKind provider, string? calendarUrl, bool syncEnabled, DateTimeOffset now)
    {
        var url = string.IsNullOrWhiteSpace(calendarUrl) ? null : calendarUrl.Trim();

        if (url is not null
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme != Uri.UriSchemeHttps))
        {
            // Basic sobre HTTP é a senha em claro no fio, e o host vem do que o usuário
            // digitou. É a mesma exigência do AutoconfigFetcher, pelo mesmo motivo.
            throw new ArgumentException(
                "O endereço do servidor de agenda precisa ser HTTPS.", nameof(calendarUrl));
        }

        CalendarProvider = url is null ? CalendarProviderKind.None : provider;
        CalendarUrl = url;
        CalendarSyncEnabled = url is not null && provider != CalendarProviderKind.None && syncEnabled;
        Touch(now);
    }

    /// <summary>Configura autenticação por senha.</summary>
    public void UsePasswordAuthentication(string? userName, DateTimeOffset now)
    {
        AuthenticationType = AuthenticationType.Password;
        OAuthProvider = OAuthProviderKind.None;
        UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        Touch(now);
    }

    /// <summary>Configura autenticação OAuth 2.0 com o provedor indicado.</summary>
    public void UseOAuthAuthentication(OAuthProviderKind provider, DateTimeOffset now)
    {
        if (provider == OAuthProviderKind.None)
        {
            throw new ArgumentException(
                "Autenticação OAuth exige um provedor (Microsoft ou Google).", nameof(provider));
        }

        AuthenticationType = AuthenticationType.OAuth2;
        OAuthProvider = provider;
        Touch(now);
    }

    /// <summary>Registra sincronização bem-sucedida.</summary>
    public void MarkSynced(DateTimeOffset now)
    {
        SyncStatus = AccountSyncStatus.Online;
        LastSyncAt = now;
        LastSyncError = null;
        Touch(now);
    }

    /// <summary>Registra falha de sincronização.</summary>
    /// <param name="error">
    /// Texto exibível. Quem chama é responsável por não incluir credencial nem conteúdo
    /// de mensagem.
    /// </param>
    public void MarkSyncFailed(string error, bool isAuthenticationFailure, DateTimeOffset now)
    {
        SyncStatus = isAuthenticationFailure
            ? AccountSyncStatus.AuthenticationFailed
            : AccountSyncStatus.Error;
        LastSyncError = error;
        Touch(now);
    }

    /// <summary>Atualiza o estado de sincronização sem alterar o histórico de erro.</summary>
    public void SetSyncStatus(AccountSyncStatus status, DateTimeOffset now)
    {
        SyncStatus = status;
        Touch(now);
    }

    /// <summary>Define a posição manual dentro do Diretório de Domínio.</summary>
    /// <remarks>
    /// A ordem é <b>relativa ao diretório</b>, não global: duas contas de diretórios
    /// diferentes podem ter a mesma posição sem ambiguidade, porque nunca aparecem na mesma
    /// lista. Mudar de diretório é outra operação — passa pela regra de pertinência, e não
    /// por aqui.
    /// </remarks>
    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sortOrder);

        SortOrder = sortOrder;
        Touch(now);
    }

    /// <summary>Ativa ou desativa a conta. Desativar preserva os dados locais.</summary>
    public void SetActive(bool isActive, DateTimeOffset now)
    {
        IsActive = isActive;
        if (!isActive)
        {
            SyncStatus = AccountSyncStatus.Disabled;
        }

        Touch(now);
    }

    /// <summary>Ajusta preferências de sincronização.</summary>
    public void ConfigureSync(int syncIntervalMinutes, BodyDownloadPolicy bodyDownloadPolicy, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(syncIntervalMinutes);

        SyncIntervalMinutes = syncIntervalMinutes;
        BodyDownloadPolicy = bodyDownloadPolicy;
        Touch(now);
    }

    /// <summary>Define a assinatura da conta.</summary>
    public void SetSignature(string? signature, DateTimeOffset now)
    {
        Signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim();
        Touch(now);
    }

    /// <summary>Atualiza o nome exibido.</summary>
    public void Rename(string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
        Touch(now);
    }

    /// <summary>Adiciona uma pasta à conta.</summary>
    public void AddFolder(Folder folder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (_folders.Any(f => f.Id == folder.Id))
        {
            return;
        }

        _folders.Add(folder);
        Touch(now);
    }

    internal void AssignToDomain(DomainDirectory directory, DateTimeOffset now)
    {
        DomainDirectoryId = directory.Id;
        DomainDirectory = directory;
        Touch(now);
    }

    /// <summary>
    /// Monta o identificador da credencial no Credential Manager.
    /// </summary>
    /// <remarks>
    /// O prefixo evita colisão com credenciais de outros aplicativos e torna óbvio, no
    /// painel do Windows, de onde a entrada veio.
    /// </remarks>
    private static string BuildCredentialKey(EmailAddress address)
        => $"Sintek.Mail:{address.Value}";
}
