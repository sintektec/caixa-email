using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Abstractions.Mail;

/// <summary>De onde veio a configuração descoberta.</summary>
/// <remarks>
/// A origem não é curiosidade: ela determina o quanto se pode confiar no resultado. Uma
/// tabela interna e o autoconfig publicado pelo próprio domínio são declarações de quem
/// manda no domínio; o ISPDB é um banco de terceiros; a convenção é palpite. O assistente
/// mostra a origem ao usuário justamente para que a diferença apareça.
/// </remarks>
public enum DiscoverySource
{
    /// <summary>Tabela interna de provedores conhecidos.</summary>
    KnownProvider,

    /// <summary>Arquivo de autoconfiguração publicado pelo próprio domínio.</summary>
    DomainAutoconfig,

    /// <summary>Registros SRV do DNS, conforme a RFC 6186.</summary>
    DnsSrv,

    /// <summary>Banco de dados ISPDB da Mozilla.</summary>
    Ispdb,

    /// <summary>Convenção de nomeação (<c>imap.dominio</c>, <c>smtp.dominio</c>).</summary>
    Convention,

    /// <summary>Informado manualmente pelo usuário.</summary>
    Manual,
}

/// <summary>Configuração de servidores descoberta automaticamente.</summary>
/// <param name="ImapHost">Servidor IMAP.</param>
/// <param name="ImapPort">Porta IMAP.</param>
/// <param name="ImapSecurity">Modo de proteção do IMAP.</param>
/// <param name="SmtpHost">Servidor SMTP.</param>
/// <param name="SmtpPort">Porta SMTP.</param>
/// <param name="SmtpSecurity">Modo de proteção do SMTP.</param>
/// <param name="RecommendedAuthentication">Autenticação recomendada pelo provedor.</param>
/// <param name="OAuthProvider">Provedor de identidade, quando OAuth é recomendado.</param>
/// <param name="Source">Estratégia que produziu esta configuração.</param>
/// <param name="RequiresUserConfirmation">
/// Se o usuário precisa confirmar os servidores antes de prosseguir. Vale para descobertas
/// que apontam para fora do domínio do endereço — hospedagem terceirizada é legítima e
/// comum, mas é também o formato de um desvio malicioso, e quem decide é o usuário.
/// </param>
public readonly record struct DiscoveredServerSettings(
    string ImapHost,
    int ImapPort,
    SecureSocketMode ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    SecureSocketMode SmtpSecurity,
    AuthenticationType RecommendedAuthentication,
    OAuthProviderKind OAuthProvider,
    DiscoverySource Source = DiscoverySource.Convention,
    bool RequiresUserConfirmation = false);

/// <summary>
/// Descobre a configuração de servidores a partir do endereço de e-mail.
/// </summary>
/// <remarks>
/// A especificação exige configuração automática além da manual. A descoberta tenta, em
/// ordem: provedores conhecidos (Gmail, Microsoft 365), o autoconfig publicado pelo próprio
/// domínio, registros SRV do DNS conforme a RFC 6186, o banco ISPDB da Mozilla e, por
/// último, as convenções usuais (<c>imap.dominio</c>, <c>smtp.dominio</c>).
/// </remarks>
public interface IAutodiscoverService
{
    /// <summary>
    /// Descobre a configuração para o endereço informado, ou devolve
    /// <see langword="null"/> quando nada foi encontrado e o usuário precisa configurar
    /// manualmente.
    /// </summary>
    Task<DiscoveredServerSettings?> DiscoverAsync(
        string emailAddress, CancellationToken cancellationToken = default);
}

/// <summary>Um registro SRV do DNS.</summary>
/// <param name="Target">Nome do servidor. Um ponto isolado significa "serviço indisponível".</param>
/// <param name="Port">Porta do serviço.</param>
/// <param name="Priority">Prioridade; menor vence.</param>
/// <param name="Weight">Peso entre registros de mesma prioridade; maior vence.</param>
public readonly record struct DnsServiceRecord(string Target, int Port, int Priority, int Weight)
{
    /// <summary>
    /// Indica que o domínio declarou explicitamente não oferecer o serviço.
    /// </summary>
    /// <remarks>
    /// A RFC 2782 define o alvo "." como negação explícita. Tratá-la como um host chamado
    /// "." levaria a uma tentativa de conexão sem sentido e a uma mensagem de erro que não
    /// explicaria nada ao usuário.
    /// </remarks>
    public bool IsServiceUnavailable => Target is "." or "";
}

/// <summary>Consulta registros SRV do DNS.</summary>
/// <remarks>
/// Abstraída para que a descoberta seja testável sem rede: o .NET não expõe consulta SRV
/// na biblioteca padrão, e depender diretamente do resolvedor tornaria impossível verificar
/// a ordem das estratégias em teste automatizado.
/// </remarks>
public interface IDnsResolver
{
    /// <summary>
    /// Consulta os registros SRV de um serviço (por exemplo
    /// <c>_imaps._tcp.exemplo.com.br</c>), devolvendo lista vazia quando não existem.
    /// </summary>
    Task<IReadOnlyList<DnsServiceRecord>> ResolveServiceAsync(
        string serviceName, CancellationToken cancellationToken = default);
}
