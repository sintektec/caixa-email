using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Infrastructure.Mail;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>
/// Cobre a leitura do veredito do servidor. O produto não reverifica SPF, DKIM nem DMARC —
/// eles dependem do DNS no instante em que a mensagem chegou, e refazê-los dias depois daria
/// resultado diferente e errado.
/// </summary>
public class AuthenticationResultsParserTests
{
    [Fact]
    public void Ler_CabecalhoCompleto_ExtraiOsTresMetodos()
    {
        const string header =
            "mx.google.com; spf=pass smtp.mailfrom=sintek.com.br; " +
            "dkim=pass header.i=@sintek.com.br; dmarc=pass (p=REJECT sp=REJECT dis=NONE)";

        var verdict = AuthenticationResultsParser.Parse(header);

        verdict.Spf.Should().Be(AuthenticationResult.Pass);
        verdict.Dkim.Should().Be(AuthenticationResult.Pass);
        verdict.Dmarc.Should().Be(AuthenticationResult.Pass);
    }

    [Theory]
    [InlineData("spf=fail", AuthenticationResult.Fail)]
    [InlineData("spf=softfail", AuthenticationResult.SoftFail)]
    [InlineData("spf=neutral", AuthenticationResult.Neutral)]
    [InlineData("spf=none", AuthenticationResult.None)]
    [InlineData("spf=temperror", AuthenticationResult.TemporaryError)]
    [InlineData("spf=permerror", AuthenticationResult.PermanentError)]
    public void Ler_CadaResultadoDaRfc_ETraduzido(string header, AuthenticationResult expected)
        => AuthenticationResultsParser.Parse(header).Spf.Should().Be(expected);

    [Fact]
    public void Ler_MetodoAusente_FicaDesconhecido()
    {
        // "Desconhecido" e "o domínio não publica política" são coisas diferentes, e a
        // distinção decide se a interface mostra selo, alerta ou nada.
        var verdict = AuthenticationResultsParser.Parse("mx.exemplo.com; spf=pass");

        verdict.Dkim.Should().Be(AuthenticationResult.Unknown);
        verdict.Dmarc.Should().Be(AuthenticationResult.Unknown);
    }

    [Fact]
    public void Ler_MetodoParecidoNoNome_NaoEConfundido()
    {
        // "dkim-adsp" é outro método, obsoleto, e servidores antigos ainda o emitem.
        var verdict = AuthenticationResultsParser.Parse("mx.exemplo.com; dkim-adsp=fail; dkim=pass");

        verdict.Dkim.Should().Be(AuthenticationResult.Pass);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("texto sem estrutura nenhuma")]
    public void Ler_CabecalhoInutilizavel_NaoInventaVeredito(string? header)
    {
        var verdict = AuthenticationResultsParser.Parse(header);

        verdict.Spf.Should().Be(AuthenticationResult.Unknown);
        verdict.Dkim.Should().Be(AuthenticationResult.Unknown);
        verdict.Dmarc.Should().Be(AuthenticationResult.Unknown);
    }

    [Fact]
    public void Ler_ValorNaoReconhecido_FicaDesconhecidoEmVezDeReprovar()
    {
        // Inventar "falhou" a partir de um valor que não se entende produziria alarme falso
        // em servidores com extensões próprias.
        AuthenticationResultsParser.Parse("spf=policy").Spf.Should().Be(AuthenticationResult.Unknown);
    }

    [Theory]
    [InlineData("YES", null, true)]
    [InlineData("yes", null, true)]
    [InlineData("NO", null, false)]
    [InlineData(null, "Yes, score=8.2 required=5.0", true)]
    [InlineData(null, "No, score=-1.4 required=5.0", false)]
    [InlineData(null, null, false)]
    public void Ler_AsDuasConvencoesDeSpam_SaoAceitas(string? flag, string? status, bool expected)
        => AuthenticationResultsParser.Parse(null, flag, status).IsFlaggedAsSpam.Should().Be(expected);

    [Fact]
    public void Ler_PontuacaoNoCabecalhoProprio_EExtraida()
        => AuthenticationResultsParser.Parse(null, null, null, "6.7").SpamScore.Should().Be(6.7);

    [Fact]
    public void Ler_PontuacaoDentroDoStatus_EExtraida()
        => AuthenticationResultsParser.Parse(null, null, "Yes, score=8.2 required=5.0").SpamScore
            .Should().Be(8.2);

    [Fact]
    public void Ler_PontuacaoNegativa_EPreservada()
        => AuthenticationResultsParser.Parse(null, null, "No, score=-1.4 required=5.0").SpamScore
            .Should().Be(-1.4);

    [Fact]
    public void Ler_SemPontuacao_DevolveNulo()
        => AuthenticationResultsParser.Parse(null, "YES").SpamScore.Should().BeNull();
}
