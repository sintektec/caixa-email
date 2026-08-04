using System.Xml;
using System.Xml.Linq;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Infrastructure.Mail.Autodiscover;

/// <summary>
/// Lê o formato de autoconfiguração do Thunderbird (<c>clientConfig</c> v1.1).
/// </summary>
/// <remarks>
/// <para>
/// O mesmo formato serve às duas fontes remotas: o arquivo que o próprio domínio publica e
/// o banco ISPDB da Mozilla. Um analisador só, portanto — a diferença entre as fontes está
/// em quem responde, não no que responde.
/// </para>
/// <para>
/// <b>O documento vem de fora e é tratado como hostil.</b> A leitura desliga DTD e
/// resolvedor externo: sem isso, um <c>&lt;!ENTITY&gt;</c> apontando para um arquivo local
/// transformaria a descoberta de servidores em leitura arbitrária de disco, e um apontando
/// para a rede interna, em varredura de portas a partir da máquina do usuário.
/// </para>
/// </remarks>
public static class ClientConfigParser
{
    /// <summary>
    /// Converte o XML em configuração, ou devolve <see langword="null"/> quando o
    /// documento não traz um par IMAP/SMTP utilizável.
    /// </summary>
    /// <param name="xml">Documento recebido.</param>
    /// <param name="source">Origem, para registro na configuração devolvida.</param>
    /// <param name="emailDomain">
    /// Domínio do endereço, usado para decidir se a configuração aponta para fora do
    /// domínio e portanto precisa de confirmação do usuário.
    /// </param>
    public static DiscoveredServerSettings? Parse(string xml, DiscoverySource source, EmailDomain emailDomain)
    {
        ArgumentNullException.ThrowIfNull(emailDomain);

        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument document;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var provider = document.Root?.Element("emailProvider");
        if (provider is null)
        {
            return null;
        }

        var incoming = provider
            .Elements("incomingServer")
            .Where(e => string.Equals((string?)e.Attribute("type"), "imap", StringComparison.OrdinalIgnoreCase))
            .Select(ReadServer)
            .FirstOrDefault(s => s is not null);

        var outgoing = provider
            .Elements("outgoingServer")
            .Where(e => string.Equals((string?)e.Attribute("type"), "smtp", StringComparison.OrdinalIgnoreCase))
            .Select(ReadServer)
            .FirstOrDefault(s => s is not null);

        // POP3 sozinho não serve: o produto é organizado por pastas espelhadas do servidor,
        // conceito que o POP3 não tem. Melhor cair para a próxima estratégia do que
        // configurar uma conta que nunca vai funcionar como o usuário espera.
        if (incoming is null || outgoing is null)
        {
            return null;
        }

        var authentication = CombineAuthentication(incoming.Value.Authentication, outgoing.Value.Authentication);

        return new DiscoveredServerSettings(
            incoming.Value.Host,
            incoming.Value.Port,
            incoming.Value.Security,
            outgoing.Value.Host,
            outgoing.Value.Port,
            outgoing.Value.Security,
            authentication,
            authentication == AuthenticationType.OAuth2
                ? InferOAuthProvider(incoming.Value.Host)
                : OAuthProviderKind.None,
            source,
            RequiresUserConfirmation:
                !HostBelongsToDomain(incoming.Value.Host, emailDomain)
                || !HostBelongsToDomain(outgoing.Value.Host, emailDomain));
    }

    private readonly record struct ServerConfig(
        string Host, int Port, SecureSocketMode Security, AuthenticationType? Authentication);

    private static ServerConfig? ReadServer(XElement element)
    {
        var host = (string?)element.Element("hostname");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (!int.TryParse((string?)element.Element("port"), out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        var security = ParseSocketType((string?)element.Element("socketType"));

        // Conexão em claro é recusada de propósito. O formato permite declará-la, e alguns
        // registros antigos do ISPDB ainda o fazem; aceitar isso significaria entregar a
        // senha do usuário em texto puro porque um banco de terceiros mandou.
        if (security == SecureSocketMode.None)
        {
            return null;
        }

        var authentication = element
            .Elements("authentication")
            .Select(e => ParseAuthentication(e.Value))
            .FirstOrDefault(a => a is not null);

        return new ServerConfig(host.Trim(), port, security, authentication);
    }

    private static SecureSocketMode ParseSocketType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SSL" => SecureSocketMode.SslOnConnect,
        "STARTTLS" => SecureSocketMode.StartTls,
        _ => SecureSocketMode.None,
    };

    private static AuthenticationType? ParseAuthentication(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "OAUTH2" => AuthenticationType.OAuth2,
        "PASSWORD-CLEARTEXT" or "PLAIN" or "PASSWORD-ENCRYPTED" or "SECURE" => AuthenticationType.Password,
        _ => null,
    };

    /// <summary>
    /// Escolhe a autenticação da conta a partir do que cada servidor declara.
    /// </summary>
    /// <remarks>
    /// OAuth em qualquer um dos dois vence: um provedor que aceita OAuth no IMAP e ainda
    /// anuncia senha no SMTP está descrevendo o caminho legado, e usá-lo levaria o usuário
    /// a uma senha de aplicativo que ele não precisa criar.
    /// </remarks>
    private static AuthenticationType CombineAuthentication(AuthenticationType? incoming, AuthenticationType? outgoing)
        => incoming == AuthenticationType.OAuth2 || outgoing == AuthenticationType.OAuth2
            ? AuthenticationType.OAuth2
            : AuthenticationType.Password;

    private static OAuthProviderKind InferOAuthProvider(string host)
    {
        if (host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("googlemail.com", StringComparison.OrdinalIgnoreCase))
        {
            return OAuthProviderKind.Google;
        }

        if (host.EndsWith("office365.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("outlook.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return OAuthProviderKind.Microsoft;
        }

        return OAuthProviderKind.None;
    }

    /// <summary>Indica se o host está dentro do domínio do endereço.</summary>
    internal static bool HostBelongsToDomain(string host, EmailDomain domain)
    {
        var normalized = host.Trim().TrimEnd('.');

        return normalized.Equals(domain.Value, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith('.' + domain.Value, StringComparison.OrdinalIgnoreCase);
    }
}
