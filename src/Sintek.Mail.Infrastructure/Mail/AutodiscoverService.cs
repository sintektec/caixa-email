using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>
/// Descobre a configuração de servidores a partir do endereço de e-mail.
/// </summary>
/// <remarks>
/// <para>
/// A ordem das estratégias é deliberada:
/// </para>
/// <list type="number">
/// <item>
/// <b>Provedores conhecidos</b>, por tabela. Gmail e Microsoft 365 exigem OAuth 2.0 e têm
/// endereços fixos; adivinhar por convenção acertaria o host e erraria a autenticação,
/// levando o usuário a um erro de senha que nenhuma senha resolveria.
/// </item>
/// <item>
/// <b>Convenções usuais</b> do domínio (<c>imap.dominio</c>, <c>mail.dominio</c>), que
/// atendem a maioria dos servidores corporativos.
/// </item>
/// </list>
/// <para>
/// A consulta a registros SRV do DNS (RFC 6186) e ao banco de dados ISPDB da Mozilla é o
/// próximo passo natural desta classe, mas ambas dependem de rede e ficam para a fase de
/// configuração de contas — a estrutura aqui já as acomoda sem alteração de contrato.
/// </para>
/// </remarks>
public sealed class AutodiscoverService : IAutodiscoverService
{
    private readonly ILogger<AutodiscoverService> _logger;

    public AutodiscoverService(ILogger<AutodiscoverService> logger) => _logger = logger;

    /// <summary>Provedores cuja configuração é conhecida e não deve ser adivinhada.</summary>
    private static readonly Dictionary<string, DiscoveredServerSettings> KnownProviders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail.com"] = new(
                "imap.gmail.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.gmail.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Google),

            ["googlemail.com"] = new(
                "imap.gmail.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.gmail.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Google),

            ["outlook.com"] = new(
                "outlook.office365.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.office365.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Microsoft),

            ["hotmail.com"] = new(
                "outlook.office365.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.office365.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Microsoft),

            ["live.com"] = new(
                "outlook.office365.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.office365.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Microsoft),

            ["office365.com"] = new(
                "outlook.office365.com", 993, SecureSocketMode.SslOnConnect,
                "smtp.office365.com", 587, SecureSocketMode.StartTls,
                AuthenticationType.OAuth2, OAuthProviderKind.Microsoft),
        };

    /// <inheritdoc />
    public Task<DiscoveredServerSettings?> DiscoverAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.TryParse(emailAddress, out var address))
        {
            return Task.FromResult<DiscoveredServerSettings?>(null);
        }

        var domain = address.Domain.Value;

        if (KnownProviders.TryGetValue(domain, out var known))
        {
            _logger.LogInformation("Configuração conhecida encontrada para o domínio {Domain}.", domain);
            return Task.FromResult<DiscoveredServerSettings?>(known);
        }

        _logger.LogInformation(
            "Nenhum provedor conhecido para {Domain}; usando as convenções usuais de nomeação.", domain);

        return Task.FromResult<DiscoveredServerSettings?>(GuessByConvention(address.Domain));
    }

    /// <summary>
    /// Monta a configuração pelas convenções mais comuns de servidores corporativos.
    /// </summary>
    /// <remarks>
    /// As portas escolhidas são as cifradas (993 e 587 com STARTTLS). As portas em claro
    /// (143 e 25) ficam de fora de propósito: a especificação exige suporte a SSL/TLS, e
    /// oferecer o caminho sem criptografia como padrão automático seria o oposto disso.
    /// Quem realmente precisar delas configura manualmente.
    /// </remarks>
    private static DiscoveredServerSettings GuessByConvention(EmailDomain domain)
        => new(
            $"imap.{domain.Value}", 993, SecureSocketMode.SslOnConnect,
            $"smtp.{domain.Value}", 587, SecureSocketMode.StartTls,
            AuthenticationType.Password, OAuthProviderKind.None);

    /// <summary>Indica se o domínio tem configuração conhecida, sem consultar a rede.</summary>
    public static bool IsKnownProvider(string domain) => KnownProviders.ContainsKey(domain);
}
