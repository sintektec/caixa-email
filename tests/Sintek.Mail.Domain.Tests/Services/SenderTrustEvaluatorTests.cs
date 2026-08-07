using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre o veredito de procedência exibido no painel de leitura. O produto não classifica
/// spam — lê o que o servidor apurou e acrescenta a única verificação que o cliente faz
/// melhor: comparar o nome exibido com os contatos que este usuário de fato tem.
/// </summary>
public class SenderTrustEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static Message Received(
        string from,
        string? displayName = null,
        AuthenticationResult spf = AuthenticationResult.Unknown,
        AuthenticationResult dkim = AuthenticationResult.Unknown,
        AuthenticationResult dmarc = AuthenticationResult.Unknown,
        bool flaggedAsSpam = false)
    {
        var message = Message.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "<1@servidor>", Now, Now, Now);

        message.SetHeaders("Assunto", EmailAddress.Parse(from), displayName, null, null, Now);
        message.SetAuthenticationResults(spf, dkim, dmarc, flaggedAsSpam, null, Now);

        return message;
    }

    private static readonly KnownCorrespondent[] Contatos =
    [
        new("João Silva", EmailDomain.Parse("sintek.com.br")),
        new("Financeiro", EmailDomain.Parse("cliente.com.br")),
    ];

    [Fact]
    public void Avaliar_AutenticacaoCompleta_RecebeSeloDeVerificada()
    {
        var message = Received(
            "contato@sintek.com.br",
            spf: AuthenticationResult.Pass,
            dkim: AuthenticationResult.Pass,
            dmarc: AuthenticationResult.Pass);

        SenderTrustEvaluator.Evaluate(message, []).Level.Should().Be(SenderTrustLevel.Authenticated);
    }

    [Fact]
    public void Avaliar_ServidorSemAvaliacaoDeDmarc_AindaContaComoVerificada()
    {
        // Exigir DMARC apagaria o selo de praticamente toda mensagem legítima recebida por
        // servidores que não o avaliam.
        var message = Received(
            "contato@sintek.com.br",
            spf: AuthenticationResult.Pass,
            dkim: AuthenticationResult.Pass);

        SenderTrustEvaluator.Evaluate(message, []).Level.Should().Be(SenderTrustLevel.Authenticated);
    }

    [Theory]
    [InlineData(AuthenticationResult.Fail, AuthenticationResult.Pass, AuthenticationResult.Unknown)]
    [InlineData(AuthenticationResult.Pass, AuthenticationResult.Fail, AuthenticationResult.Unknown)]
    [InlineData(AuthenticationResult.Pass, AuthenticationResult.Pass, AuthenticationResult.Fail)]
    public void Avaliar_QualquerVerificacaoReprovada_Alerta(
        AuthenticationResult spf, AuthenticationResult dkim, AuthenticationResult dmarc)
    {
        var message = Received("contato@sintek.com.br", spf: spf, dkim: dkim, dmarc: dmarc);

        SenderTrustEvaluator.Evaluate(message, []).Level
            .Should().Be(SenderTrustLevel.AuthenticationFailed);
    }

    [Fact]
    public void Avaliar_ErroTemporarioNaVerificacao_NaoAlerta()
    {
        // Erro temporário diz que a verificação não pôde ser feita, não que reprovou. Aviso
        // que aparece toda vez que um DNS oscila deixa de ser lido.
        var message = Received(
            "contato@sintek.com.br",
            spf: AuthenticationResult.TemporaryError,
            dkim: AuthenticationResult.TemporaryError);

        SenderTrustEvaluator.Evaluate(message, []).Level.Should().Be(SenderTrustLevel.Neutral);
    }

    [Fact]
    public void Avaliar_ServidorClassificouComoSpam_ExibeOVeredito()
    {
        var message = Received("promo@desconhecido.com", flaggedAsSpam: true);

        var verdict = SenderTrustEvaluator.Evaluate(message, []);

        verdict.Level.Should().Be(SenderTrustLevel.FlaggedAsSpam);
        verdict.Reason.Should().Contain("lixo eletrônico");
    }

    [Fact]
    public void Avaliar_NomeDeContatoConhecidoComDominioDiferente_AcusaDisfarce()
    {
        // É o vetor de phishing que mais funciona: o nome bate, e ninguém lê o endereço.
        var message = Received("joao.silva@sintek-com.br", "João Silva");

        var verdict = SenderTrustEvaluator.Evaluate(message, Contatos);

        verdict.Level.Should().Be(SenderTrustLevel.DisplayNameSpoofing);
        verdict.ImpersonatedName.Should().Be("João Silva");
        verdict.Reason.Should().Contain("sintek-com.br");
    }

    [Fact]
    public void Avaliar_DisfarceSemAcentoOuComOutraCaixa_TambemEDetectado()
    {
        // "joao silva" engana tão bem quanto "João Silva".
        var message = Received("suporte@golpe.com", "JOAO SILVA");

        SenderTrustEvaluator.Evaluate(message, Contatos).Level
            .Should().Be(SenderTrustLevel.DisplayNameSpoofing);
    }

    [Fact]
    public void Avaliar_MesmoNomeNoDominioLegitimo_NaoEDisfarce()
    {
        var message = Received("joao.silva@sintek.com.br", "João Silva");

        SenderTrustEvaluator.Evaluate(message, Contatos).Level
            .Should().NotBe(SenderTrustLevel.DisplayNameSpoofing);
    }

    [Fact]
    public void Avaliar_MesmoNomeEmSubdominioLegitimo_NaoEDisfarce()
    {
        var message = Received("joao.silva@filial.sintek.com.br", "João Silva");

        SenderTrustEvaluator.Evaluate(message, Contatos).Level
            .Should().NotBe(SenderTrustLevel.DisplayNameSpoofing);
    }

    [Fact]
    public void Avaliar_SemHistoricoDeContatos_NaoAcusaDisfarce()
    {
        // Caixa recém-configurada não tem como saber quem é quem, e chutar produziria alarme
        // falso em massa.
        var message = Received("qualquer@dominio.com", "João Silva");

        SenderTrustEvaluator.Evaluate(message, []).Level.Should().Be(SenderTrustLevel.Neutral);
    }

    [Fact]
    public void Avaliar_DisfarceEmMensagemJaMarcadaComoSpam_MostraODisfarce()
    {
        // É a informação que muda o comportamento de quem já ia ignorar o aviso de spam.
        var message = Received("joao.silva@golpe.com", "João Silva", flaggedAsSpam: true);

        SenderTrustEvaluator.Evaluate(message, Contatos).Level
            .Should().Be(SenderTrustLevel.DisplayNameSpoofing);
    }

    [Fact]
    public void Avaliar_SemNomeExibido_NaoTentaDetectarDisfarce()
    {
        var message = Received("qualquer@dominio.com");

        SenderTrustEvaluator.Evaluate(message, Contatos).Level.Should().Be(SenderTrustLevel.Neutral);
    }
}
