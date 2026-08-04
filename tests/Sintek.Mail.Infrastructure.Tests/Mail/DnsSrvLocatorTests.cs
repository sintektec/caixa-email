using Microsoft.Extensions.Logging.Abstractions;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Mail.Autodiscover;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>Cobre a descoberta por registros SRV, conforme a RFC 6186.</summary>
public class DnsSrvLocatorTests
{
    private static readonly EmailDomain Domain = EmailDomain.Parse("sintek.com.br");

    private static DnsSrvLocator Locator(Dictionary<string, IReadOnlyList<DnsServiceRecord>> records, out FakeDnsResolver resolver)
    {
        resolver = new FakeDnsResolver(records);
        return new DnsSrvLocator(resolver, NullLogger<DnsSrvLocator>.Instance);
    }

    [Fact]
    public async Task LocateAsync_ServicosCifrados_UsamTlsDesdeAConexao()
    {
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 993, 0, 1)],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result.Should().NotBeNull();
        result!.Value.ImapHost.Should().Be("imap.sintek.com.br");
        result.Value.ImapPort.Should().Be(993);
        result.Value.ImapSecurity.Should().Be(SecureSocketMode.SslOnConnect);
        result.Value.SmtpPort.Should().Be(465);
        result.Value.SmtpSecurity.Should().Be(SecureSocketMode.SslOnConnect);
        result.Value.Source.Should().Be(DiscoverySource.DnsSrv);
    }

    [Fact]
    public async Task LocateAsync_SomenteServicosSemTls_UsamStartTls()
    {
        var locator = Locator(new()
        {
            ["_imap._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 143, 0, 1)],
            ["_submission._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 587, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result.Should().NotBeNull();
        result!.Value.ImapSecurity.Should().Be(SecureSocketMode.StartTls);
        result.Value.SmtpSecurity.Should().Be(SecureSocketMode.StartTls);
    }

    [Fact]
    public async Task LocateAsync_ComAsDuasVariantes_PrefereACifrada()
    {
        // O domínio anuncia os dois; a variante cifrada ganha mesmo com prioridade maior.
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("segura.sintek.com.br", 993, 10, 1)],
            ["_imap._tcp.sintek.com.br"] = [new DnsServiceRecord("legado.sintek.com.br", 143, 0, 1)],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
        }, out var resolver);

        var result = await locator.LocateAsync(Domain);

        result!.Value.ImapHost.Should().Be("segura.sintek.com.br");
        resolver.QueriedServices.Should().NotContain("_imap._tcp.sintek.com.br");
    }

    [Fact]
    public async Task LocateAsync_AlvoPonto_TrataComoServicoIndisponivel()
    {
        // A RFC 2782 define o alvo "." como negação explícita do serviço.
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord(".", 993, 0, 1)],
            ["_imap._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 143, 0, 1)],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result!.Value.ImapHost.Should().Be("imap.sintek.com.br");
        result.Value.ImapSecurity.Should().Be(SecureSocketMode.StartTls);
    }

    [Fact]
    public async Task LocateAsync_VariosRegistros_EscolheMenorPrioridadeEMaiorPeso()
    {
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] =
            [
                new DnsServiceRecord("terciario.sintek.com.br", 993, 20, 100),
                new DnsServiceRecord("secundario.sintek.com.br", 993, 10, 1),
                new DnsServiceRecord("primario.sintek.com.br", 993, 10, 50),
            ],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result!.Value.ImapHost.Should().Be("primario.sintek.com.br");
    }

    [Fact]
    public async Task LocateAsync_SemRegistroDeEnvio_DevolveNulo()
    {
        // Meia configuração não serve: sem SMTP o usuário não envia, e completar o par com
        // um palpite esconderia o problema até a primeira tentativa de envio.
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 993, 0, 1)],
        }, out _);

        (await locator.LocateAsync(Domain)).Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_SemNenhumRegistro_DevolveNulo()
    {
        var locator = Locator([], out _);

        (await locator.LocateAsync(Domain)).Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_ServidorForaDoDominio_ExigeConfirmacaoDoUsuario()
    {
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.provedor-externo.net", 993, 0, 1)],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.provedor-externo.net", 465, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result!.Value.RequiresUserConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task LocateAsync_SrvNaoDescreveAutenticacao_AssumeSenha()
    {
        // A RFC 6186 não tem campo de autenticação. Um domínio que exigisse OAuth estaria
        // na tabela de conhecidos ou publicaria autoconfig — ambos consultados antes.
        var locator = Locator(new()
        {
            ["_imaps._tcp.sintek.com.br"] = [new DnsServiceRecord("imap.sintek.com.br", 993, 0, 1)],
            ["_submissions._tcp.sintek.com.br"] = [new DnsServiceRecord("smtp.sintek.com.br", 465, 0, 1)],
        }, out _);

        var result = await locator.LocateAsync(Domain);

        result!.Value.RecommendedAuthentication.Should().Be(AuthenticationType.Password);
        result.Value.OAuthProvider.Should().Be(OAuthProviderKind.None);
    }
}
