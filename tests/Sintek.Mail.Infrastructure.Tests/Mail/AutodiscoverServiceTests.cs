using Microsoft.Extensions.Logging.Abstractions;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Infrastructure.Mail;
using Sintek.Mail.Infrastructure.Mail.Autodiscover;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>
/// Cobre a ordem das estratégias de descoberta. A ordem é o comportamento: cada fonte só
/// é consultada quando a anterior — mais confiável — não respondeu.
/// </summary>
public class AutodiscoverServiceTests
{
    private const string AutoconfigUrl =
        "https://autoconfig.sintek.com.br/mail/config-v1.1.xml?emailaddress=contato%40sintek.com.br";

    private const string WellKnownUrl =
        "https://sintek.com.br/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress=contato%40sintek.com.br";

    private const string IspdbUrl = "https://autoconfig.thunderbird.net/v1.1/sintek.com.br";

    private static AutodiscoverService Create(
        Dictionary<string, string>? documents,
        Dictionary<string, IReadOnlyList<DnsServiceRecord>>? srvRecords,
        out StubHttpMessageHandler handler,
        out FakeDnsResolver resolver)
    {
        handler = new StubHttpMessageHandler(documents);
        resolver = new FakeDnsResolver(srvRecords);

        var fetcher = new AutoconfigFetcher(
            new HttpClient(handler), NullLogger<AutoconfigFetcher>.Instance);

        var locator = new DnsSrvLocator(resolver, NullLogger<DnsSrvLocator>.Instance);

        return new AutodiscoverService(fetcher, locator, NullLogger<AutodiscoverService>.Instance);
    }

    [Fact]
    public async Task DiscoverAsync_ProvedorConhecido_NaoConsultaARede()
    {
        // Gmail exige OAuth. Descobrir o host por outro caminho acertaria o servidor e
        // erraria a autenticação, levando a um erro de senha que nenhuma senha resolve.
        var service = Create(null, null, out var handler, out var resolver);

        var result = await service.DiscoverAsync("usuario@gmail.com");

        result.Should().NotBeNull();
        result!.Value.Source.Should().Be(DiscoverySource.KnownProvider);
        result.Value.RecommendedAuthentication.Should().Be(AuthenticationType.OAuth2);
        result.Value.OAuthProvider.Should().Be(OAuthProviderKind.Google);

        handler.RequestedUris.Should().BeEmpty();
        resolver.QueriedServices.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_AutoconfigDoDominio_TemPrecedenciaSobreOResto()
    {
        var service = Create(
            new Dictionary<string, string>
            {
                [AutoconfigUrl] = ClientConfigSamples.PasswordConfig(
                    "sintek.com.br", "imap.sintek.com.br", "smtp.sintek.com.br"),
            },
            new Dictionary<string, IReadOnlyList<DnsServiceRecord>>
            {
                ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("srv.sintek.com.br", 993, 0, 1)],
                ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("srv.sintek.com.br", 465, 0, 1)],
            },
            out _,
            out var resolver);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result!.Value.Source.Should().Be(DiscoverySource.DomainAutoconfig);
        result.Value.ImapHost.Should().Be("imap.sintek.com.br");
        resolver.QueriedServices.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_SemAutoconfigNoSubdominio_TentaOWellKnown()
    {
        var service = Create(
            new Dictionary<string, string>
            {
                [WellKnownUrl] = ClientConfigSamples.PasswordConfig(
                    "sintek.com.br", "imap.sintek.com.br", "smtp.sintek.com.br"),
            },
            null,
            out var handler,
            out _);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result!.Value.Source.Should().Be(DiscoverySource.DomainAutoconfig);
        handler.RequestedUris.Select(u => u.AbsoluteUri).Should().ContainInOrder(AutoconfigUrl, WellKnownUrl);
    }

    [Fact]
    public async Task DiscoverAsync_SemAutoconfig_UsaOsRegistrosSrv()
    {
        var service = Create(
            null,
            new Dictionary<string, IReadOnlyList<DnsServiceRecord>>
            {
                ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 993, 0, 1)],
                ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
            },
            out var handler,
            out _);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result!.Value.Source.Should().Be(DiscoverySource.DnsSrv);
        result.Value.SmtpPort.Should().Be(465);
        handler.RequestedUris.Select(u => u.AbsoluteUri).Should().NotContain(IspdbUrl);
    }

    [Fact]
    public async Task DiscoverAsync_SemAutoconfigNemSrv_ConsultaOIspdb()
    {
        var service = Create(
            new Dictionary<string, string>
            {
                [IspdbUrl] = ClientConfigSamples.PasswordConfig(
                    "sintek.com.br", "imap.provedor.net", "smtp.provedor.net"),
            },
            null,
            out _,
            out _);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result!.Value.Source.Should().Be(DiscoverySource.Ispdb);
        result.Value.ImapHost.Should().Be("imap.provedor.net");
        result.Value.RequiresUserConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverAsync_ConsultaAoIspdb_NaoEnviaOEnderecoCompleto()
    {
        // O ISPDB é um terceiro sem relação com o usuário. O domínio basta para localizar o
        // registro; mandar o endereço inteiro entregaria a identidade dele de graça.
        var service = Create(
            new Dictionary<string, string>
            {
                [IspdbUrl] = ClientConfigSamples.PasswordConfig(
                    "sintek.com.br", "imap.provedor.net", "smtp.provedor.net"),
            },
            null,
            out var handler,
            out _);

        await service.DiscoverAsync("contato@sintek.com.br");

        var ispdbRequests = handler.RequestedUris
            .Where(u => u.Host == "autoconfig.thunderbird.net")
            .ToList();

        ispdbRequests.Should().ContainSingle();
        ispdbRequests[0].AbsoluteUri.Should().NotContain("contato");
    }

    [Fact]
    public async Task DiscoverAsync_NenhumaFonteResponde_CaiNaConvencao()
    {
        var service = Create(null, null, out _, out _);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result.Should().NotBeNull();
        result!.Value.Source.Should().Be(DiscoverySource.Convention);
        result.Value.ImapHost.Should().Be("imap.sintek.com.br");
        result.Value.SmtpHost.Should().Be("smtp.sintek.com.br");
        result.Value.ImapSecurity.Should().Be(SecureSocketMode.SslOnConnect);
        result.Value.SmtpSecurity.Should().Be(SecureSocketMode.StartTls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("@sintek.com.br")]
    public async Task DiscoverAsync_EnderecoInvalido_DevolveNulo(string address)
    {
        var service = Create(null, null, out _, out _);

        (await service.DiscoverAsync(address)).Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAsync_DocumentoInvalidoNoDominio_SegueParaAsFontesSeguintes()
    {
        // Um autoconfig quebrado não pode interromper a cadeia: o usuário fica sem
        // configuração alguma por causa de um arquivo mal formado que não é dele.
        var service = Create(
            new Dictionary<string, string>
            {
                [AutoconfigUrl] = "<clientConfig>documento truncado",
                [IspdbUrl] = ClientConfigSamples.PasswordConfig(
                    "sintek.com.br", "imap.provedor.net", "smtp.provedor.net"),
            },
            null,
            out _,
            out _);

        var result = await service.DiscoverAsync("contato@sintek.com.br");

        result!.Value.Source.Should().Be(DiscoverySource.Ispdb);
    }
}
