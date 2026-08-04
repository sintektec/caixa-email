using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Infrastructure.Mail.Autodiscover;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>
/// Cobre a leitura do formato de autoconfiguração do Thunderbird, incluindo o que o
/// analisador precisa <b>recusar</b> — é onde estão as consequências de segurança.
/// </summary>
public class ClientConfigParserTests
{
    private static readonly EmailDomain Domain = EmailDomain.Parse("sintek.com.br");

    [Fact]
    public void Parse_ConfiguracaoValida_ExtraiImapESmtp()
    {
        var xml = ClientConfigSamples.PasswordConfig("sintek.com.br", "imap.sintek.com.br", "smtp.sintek.com.br");

        var result = ClientConfigParser.Parse(xml, DiscoverySource.DomainAutoconfig, Domain);

        result.Should().NotBeNull();
        result!.Value.ImapHost.Should().Be("imap.sintek.com.br");
        result.Value.ImapPort.Should().Be(993);
        result.Value.ImapSecurity.Should().Be(SecureSocketMode.SslOnConnect);
        result.Value.SmtpHost.Should().Be("smtp.sintek.com.br");
        result.Value.SmtpPort.Should().Be(587);
        result.Value.SmtpSecurity.Should().Be(SecureSocketMode.StartTls);
        result.Value.RecommendedAuthentication.Should().Be(AuthenticationType.Password);
        result.Value.Source.Should().Be(DiscoverySource.DomainAutoconfig);
        result.Value.RequiresUserConfirmation.Should().BeFalse();
    }

    [Fact]
    public void Parse_ConexaoEmClaro_DevolveNulo()
    {
        // socketType "plain" significa senha trafegando em texto puro. Aceitar seria
        // entregar a credencial do usuário porque um arquivo remoto mandou.
        var xml = """
            <clientConfig version="1.1">
              <emailProvider id="sintek.com.br">
                <incomingServer type="imap">
                  <hostname>imap.sintek.com.br</hostname>
                  <port>143</port>
                  <socketType>plain</socketType>
                  <authentication>password-cleartext</authentication>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.sintek.com.br</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                  <authentication>password-cleartext</authentication>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain).Should().BeNull();
    }

    [Fact]
    public void Parse_EntidadeExternaNoDocumento_DevolveNuloSemLerArquivo()
    {
        // XXE: um documento que declara entidade externa apontando para o disco. Com DTD
        // desligado o analisador rejeita o documento inteiro — e é isso que se verifica.
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE clientConfig [ <!ENTITY segredo SYSTEM "file:///etc/passwd"> ]>
            <clientConfig version="1.1">
              <emailProvider id="sintek.com.br">
                <incomingServer type="imap">
                  <hostname>&segredo;</hostname>
                  <port>993</port>
                  <socketType>SSL</socketType>
                  <authentication>password-cleartext</authentication>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.sintek.com.br</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                  <authentication>password-cleartext</authentication>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain).Should().BeNull();
    }

    [Fact]
    public void Parse_ApenasPop3_DevolveNulo()
    {
        // O produto organiza pastas espelhadas do servidor; POP3 não tem esse conceito.
        var xml = """
            <clientConfig version="1.1">
              <emailProvider id="sintek.com.br">
                <incomingServer type="pop3">
                  <hostname>pop.sintek.com.br</hostname>
                  <port>995</port>
                  <socketType>SSL</socketType>
                  <authentication>password-cleartext</authentication>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.sintek.com.br</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                  <authentication>password-cleartext</authentication>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain).Should().BeNull();
    }

    [Fact]
    public void Parse_AutenticacaoOAuth2_InfereProvedorPeloHost()
    {
        var xml = """
            <clientConfig version="1.1">
              <emailProvider id="empresa.com.br">
                <incomingServer type="imap">
                  <hostname>outlook.office365.com</hostname>
                  <port>993</port>
                  <socketType>SSL</socketType>
                  <authentication>OAuth2</authentication>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.office365.com</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                  <authentication>OAuth2</authentication>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        var result = ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, EmailDomain.Parse("empresa.com.br"));

        result.Should().NotBeNull();
        result!.Value.RecommendedAuthentication.Should().Be(AuthenticationType.OAuth2);
        result.Value.OAuthProvider.Should().Be(OAuthProviderKind.Microsoft);
    }

    [Fact]
    public void Parse_ServidorForaDoDominio_ExigeConfirmacaoDoUsuario()
    {
        // Hospedagem terceirizada é legítima e comum — e tem exatamente o mesmo formato de
        // um desvio malicioso. Quem decide é o usuário, então a descoberta pede confirmação.
        var xml = ClientConfigSamples.PasswordConfig(
            "sintek.com.br", "imap.provedor-externo.net", "smtp.provedor-externo.net");

        var result = ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain);

        result.Should().NotBeNull();
        result!.Value.RequiresUserConfirmation.Should().BeTrue();
    }

    [Fact]
    public void Parse_SubdominioDoProprioDominio_NaoExigeConfirmacao()
    {
        var xml = ClientConfigSamples.PasswordConfig(
            "sintek.com.br", "imap.mail.sintek.com.br", "smtp.mail.sintek.com.br");

        var result = ClientConfigParser.Parse(xml, DiscoverySource.DomainAutoconfig, Domain);

        result.Should().NotBeNull();
        result!.Value.RequiresUserConfirmation.Should().BeFalse();
    }

    [Fact]
    public void Parse_DominioParecidoMasDistinto_ExigeConfirmacao()
    {
        // "malsintek.com.br" termina com "sintek.com.br" sem ser subdomínio dele. A
        // comparação precisa do ponto separador para não confundir os dois.
        var xml = ClientConfigSamples.PasswordConfig(
            "sintek.com.br", "imap.malsintek.com.br", "smtp.malsintek.com.br");

        var result = ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain);

        result.Should().NotBeNull();
        result!.Value.RequiresUserConfirmation.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao é xml")]
    [InlineData("<clientConfig version=\"1.1\"></clientConfig>")]
    public void Parse_DocumentoInutilizavel_DevolveNulo(string xml)
        => ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain).Should().BeNull();

    [Fact]
    public void Parse_PortaForaDaFaixa_DevolveNulo()
    {
        var xml = """
            <clientConfig version="1.1">
              <emailProvider id="sintek.com.br">
                <incomingServer type="imap">
                  <hostname>imap.sintek.com.br</hostname>
                  <port>99999</port>
                  <socketType>SSL</socketType>
                  <authentication>password-cleartext</authentication>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.sintek.com.br</hostname>
                  <port>587</port>
                  <socketType>STARTTLS</socketType>
                  <authentication>password-cleartext</authentication>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        ClientConfigParser.Parse(xml, DiscoverySource.Ispdb, Domain).Should().BeNull();
    }
}
