using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Mail.Autodiscover;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>
/// Descobre a configuração de servidores a partir do endereço de e-mail.
/// </summary>
/// <remarks>
/// <para>
/// A ordem das estratégias vai da mais confiável para a menos, e cada uma só é tentada
/// quando a anterior não respondeu:
/// </para>
/// <list type="number">
/// <item>
/// <b>Provedores conhecidos</b>, por tabela. Gmail e Microsoft 365 exigem OAuth 2.0 e têm
/// endereços fixos; descobrir o host por outro caminho acertaria o servidor e erraria a
/// autenticação, levando o usuário a um erro de senha que nenhuma senha resolveria.
/// </item>
/// <item>
/// <b>Autoconfig do próprio domínio</b>, em <c>autoconfig.dominio</c> e em
/// <c>.well-known</c>. É a declaração de quem manda no domínio.
/// </item>
/// <item>
/// <b>Registros SRV do DNS</b> (RFC 6186), também publicados pelo dono do domínio, mas sem
/// campo para descrever autenticação.
/// </item>
/// <item>
/// <b>ISPDB da Mozilla</b>, banco de terceiros que cobre a maioria dos provedores
/// comerciais.
/// </item>
/// <item>
/// <b>Convenções usuais</b> (<c>imap.dominio</c>, <c>smtp.dominio</c>), que atendem boa
/// parte dos servidores corporativos.
/// </item>
/// </list>
/// <para>
/// Nada aqui interrompe o cadastro: a descoberta é uma sugestão, e a tela de configuração
/// manual continua sendo o caminho definitivo quando o resultado não serve.
/// </para>
/// </remarks>
public sealed class AutodiscoverService : IAutodiscoverService
{
    /// <summary>
    /// Teto de tempo da descoberta inteira, somadas todas as estratégias de rede.
    /// </summary>
    /// <remarks>
    /// O usuário está parado numa tela esperando. Passado esse limite, é melhor apresentar
    /// o palpite por convenção e deixá-lo corrigir do que continuar consultando a rede.
    /// </remarks>
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(20);

    /// <summary>Endereço do banco ISPDB da Mozilla.</summary>
    private const string IspdbBaseAddress = "https://autoconfig.thunderbird.net/v1.1/";

    private readonly AutoconfigFetcher _fetcher;
    private readonly DnsSrvLocator _srvLocator;
    private readonly ILogger<AutodiscoverService> _logger;

    public AutodiscoverService(
        AutoconfigFetcher fetcher,
        DnsSrvLocator srvLocator,
        ILogger<AutodiscoverService> logger)
    {
        _fetcher = fetcher;
        _srvLocator = srvLocator;
        _logger = logger;
    }

    /// <summary>Provedores cuja configuração é conhecida e não deve ser descoberta.</summary>
    private static readonly Dictionary<string, DiscoveredServerSettings> KnownProviders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail.com"] = Google(),
            ["googlemail.com"] = Google(),
            ["outlook.com"] = Microsoft(),
            ["hotmail.com"] = Microsoft(),
            ["live.com"] = Microsoft(),
            ["msn.com"] = Microsoft(),
            ["office365.com"] = Microsoft(),
        };

    private static DiscoveredServerSettings Google() => new(
        "imap.gmail.com", 993, SecureSocketMode.SslOnConnect,
        "smtp.gmail.com", 587, SecureSocketMode.StartTls,
        AuthenticationType.OAuth2, OAuthProviderKind.Google,
        DiscoverySource.KnownProvider);

    private static DiscoveredServerSettings Microsoft() => new(
        "outlook.office365.com", 993, SecureSocketMode.SslOnConnect,
        "smtp.office365.com", 587, SecureSocketMode.StartTls,
        AuthenticationType.OAuth2, OAuthProviderKind.Microsoft,
        DiscoverySource.KnownProvider);

    /// <inheritdoc />
    public async Task<DiscoveredServerSettings?> DiscoverAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.TryParse(emailAddress, out var address))
        {
            return null;
        }

        var domain = address.Domain;

        if (KnownProviders.TryGetValue(domain.Value, out var known))
        {
            _logger.LogInformation("Configuração conhecida encontrada para o domínio {Domain}.", domain.Value);
            return known;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DiscoveryBudget);

        try
        {
            return await DiscoverRemotelyAsync(address, domain, budget.Token).ConfigureAwait(false)
                ?? GuessByConvention(domain);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "A descoberta para {Domain} esgotou o tempo; usando as convenções usuais.", domain.Value);

            return GuessByConvention(domain);
        }
    }

    private async Task<DiscoveredServerSettings?> DiscoverRemotelyAsync(
        EmailAddress address, EmailDomain domain, CancellationToken cancellationToken)
    {
        var fromDomain = await TryDomainAutoconfigAsync(address, domain, cancellationToken).ConfigureAwait(false);
        if (fromDomain is not null)
        {
            _logger.LogInformation("Autoconfiguração publicada pelo domínio {Domain}.", domain.Value);
            return fromDomain;
        }

        var fromSrv = await _srvLocator.LocateAsync(domain, cancellationToken).ConfigureAwait(false);
        if (fromSrv is not null)
        {
            return fromSrv;
        }

        var fromIspdb = await TryIspdbAsync(domain, cancellationToken).ConfigureAwait(false);
        if (fromIspdb is not null)
        {
            _logger.LogInformation("Configuração encontrada no ISPDB para o domínio {Domain}.", domain.Value);
            return fromIspdb;
        }

        _logger.LogInformation(
            "Nenhuma fonte respondeu para {Domain}; usando as convenções usuais de nomeação.", domain.Value);

        return null;
    }

    /// <summary>
    /// Consulta o arquivo de autoconfiguração publicado pelo próprio domínio.
    /// </summary>
    /// <remarks>
    /// O endereço completo vai na consulta porque o formato o prevê e porque provedores com
    /// mais de uma configuração o usam para escolher a certa. Aqui isso é aceitável: quem
    /// responde é o servidor do próprio domínio do usuário, que já conhece o endereço dele.
    /// </remarks>
    private async Task<DiscoveredServerSettings?> TryDomainAutoconfigAsync(
        EmailAddress address, EmailDomain domain, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(address.Value);

        var candidates = new[]
        {
            new Uri($"https://autoconfig.{domain.Value}/mail/config-v1.1.xml?emailaddress={encoded}"),
            new Uri($"https://{domain.Value}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={encoded}"),
        };

        foreach (var uri in candidates)
        {
            var xml = await _fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
            var parsed = xml is null
                ? null
                : ClientConfigParser.Parse(xml, DiscoverySource.DomainAutoconfig, domain);

            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Consulta o banco ISPDB da Mozilla.
    /// </summary>
    /// <remarks>
    /// <b>Só o domínio é enviado, nunca o endereço completo.</b> Quem responde aqui é um
    /// terceiro sem relação com o usuário, e o domínio basta para localizar o registro. É a
    /// mesma escolha que o Thunderbird faz, e pela mesma razão.
    /// </remarks>
    private async Task<DiscoveredServerSettings?> TryIspdbAsync(
        EmailDomain domain, CancellationToken cancellationToken)
    {
        var uri = new Uri(IspdbBaseAddress + Uri.EscapeDataString(domain.Value));
        var xml = await _fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false);

        return xml is null ? null : ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, domain);
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
            AuthenticationType.Password, OAuthProviderKind.None,
            DiscoverySource.Convention);

    /// <summary>Indica se o domínio tem configuração conhecida, sem consultar a rede.</summary>
    public static bool IsKnownProvider(string domain) => KnownProviders.ContainsKey(domain);
}
