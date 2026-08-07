using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.ValueObjects;

public class EmailAddressTests
{
    [Theory]
    [InlineData("contato@sintek.com.br", "contato", "sintek.com.br")]
    [InlineData("  contato@SINTEK.com.br ", "contato", "sintek.com.br")]
    [InlineData("Financeiro@Sintek.Com.Br", "Financeiro", "sintek.com.br")]
    public void Parse_ExtraiDominio_ENormalizaApenasODominio(
        string input, string expectedLocal, string expectedDomain)
    {
        var address = EmailAddress.Parse(input);

        // O domínio é normalizado; a parte local é preservada como digitada, porque a
        // RFC 5321 a define como sensível à caixa.
        address.LocalPart.Should().Be(expectedLocal);
        address.Domain.Value.Should().Be(expectedDomain);
    }

    [Fact]
    public void Parse_UsaOUltimoArroba_QuandoAParteLocalOContem()
    {
        // Parte local entre aspas pode conter '@' (RFC 5321). Separar pelo primeiro
        // produziria um domínio absurdo.
        var address = EmailAddress.Parse("\"estranho@interno\"@sintek.com.br");

        address.Domain.Value.Should().Be("sintek.com.br");
        address.LocalPart.Should().Be("\"estranho@interno\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("semarroba.com")]
    [InlineData("@sintek.com.br")]
    [InlineData("contato@")]
    [InlineData("con tato@sintek.com.br")]
    [InlineData("contato@sintek..com.br")]
    public void TryParse_Recusa_EnderecosInvalidos(string? input)
    {
        EmailAddress.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Igualdade_IgnoraCaixaNaParteLocal()
    {
        // Nenhum provedor real trata Contato@ e contato@ como caixas distintas; tratá-las
        // como distintas aqui permitiria cadastrar a mesma conta duas vezes.
        EmailAddress.Parse("Contato@sintek.com.br")
            .Should().Be(EmailAddress.Parse("contato@sintek.com.br"));
    }

    [Fact]
    public void Value_ReconstroiOEnderecoComDominioNormalizado()
    {
        EmailAddress.Parse("Contato@SINTEK.COM.BR").Value.Should().Be("Contato@sintek.com.br");
    }
}
