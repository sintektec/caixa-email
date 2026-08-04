namespace Sintek.Mail.Domain.Enums;

/// <summary>Como a conta se autentica nos servidores IMAP e SMTP.</summary>
public enum AuthenticationType
{
    /// <summary>Usuário e senha sobre conexão cifrada (SASL PLAIN/LOGIN).</summary>
    Password = 0,

    /// <summary>OAuth 2.0 com SASL XOAUTH2. Obrigatório em Gmail e Microsoft 365.</summary>
    OAuth2 = 1,
}

/// <summary>Provedor de identidade usado quando <see cref="AuthenticationType.OAuth2"/>.</summary>
public enum OAuthProviderKind
{
    /// <summary>Nenhum: a conta usa senha.</summary>
    None = 0,

    /// <summary>Microsoft Entra ID (Outlook.com, Microsoft 365, Exchange Online).</summary>
    Microsoft = 1,

    /// <summary>Google (Gmail e Google Workspace).</summary>
    Google = 2,
}

/// <summary>Como a conexão TCP é protegida.</summary>
public enum SecureSocketMode
{
    /// <summary>
    /// Sem TLS. Só existe para servidores internos legados; a interface deve alertar.
    /// </summary>
    None = 0,

    /// <summary>TLS negociado antes de qualquer dado (portas 993/465). Padrão.</summary>
    SslOnConnect = 1,

    /// <summary>Conexão em claro promovida a TLS via STARTTLS (portas 143/587).</summary>
    StartTls = 2,

    /// <summary>Usa STARTTLS quando o servidor anuncia suporte; caso contrário segue em claro.</summary>
    StartTlsWhenAvailable = 3,

    /// <summary>Detecta automaticamente a partir da porta configurada.</summary>
    Auto = 4,
}

/// <summary>Estado de sincronização de uma conta, exibido na barra superior e na árvore.</summary>
public enum AccountSyncStatus
{
    /// <summary>Nunca sincronizou desde que foi configurada.</summary>
    NeverSynced = 0,

    /// <summary>Sem conexão. A aplicação segue plenamente utilizável com os dados locais.</summary>
    Offline = 1,

    /// <summary>Conectada e em dia.</summary>
    Online = 2,

    /// <summary>Sincronização em andamento.</summary>
    Syncing = 3,

    /// <summary>Falhou. <c>LastSyncError</c> traz a causa para exibição ao usuário.</summary>
    Error = 4,

    /// <summary>
    /// Credenciais recusadas pelo servidor. Distinto de <see cref="Error"/> porque a
    /// ação do usuário é outra: reautenticar, não tentar de novo.
    /// </summary>
    AuthenticationFailed = 5,

    /// <summary>Desativada pelo usuário: não sincroniza, mas os dados locais permanecem.</summary>
    Disabled = 6,
}

/// <summary>Quanto de cada mensagem baixar durante a sincronização.</summary>
/// <remarks>
/// Cabeçalhos são sempre baixados — é o que permite listar e pesquisar offline. O que
/// varia é o corpo, que domina o volume de dados.
/// </remarks>
public enum BodyDownloadPolicy
{
    /// <summary>Baixa o corpo de todas as mensagens sincronizadas. Melhor experiência offline.</summary>
    Always = 0,

    /// <summary>Baixa o corpo apenas quando o usuário abre a mensagem.</summary>
    OnDemand = 1,

    /// <summary>Baixa automaticamente o corpo das mensagens recentes; o restante sob demanda.</summary>
    RecentOnly = 2,
}
