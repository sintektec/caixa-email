using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.ValueObjects;

public class EmailDomainTests
{
    [Theory]
    [InlineData("sintek.com.br", "sintek.com.br")]
    [InlineData("SINTEK.COM.BR", "sintek.com.br")]
    [InlineData("  sintek.com.br  ", "sintek.com.br")]
    [InlineData("Sintek.Com.Br", "sintek.com.br")]
    [InlineData("sintek.com.br.", "sintek.com.br")]
    public void Parse_Normaliza_CaixaEEspacos(string input, string expected)
    {
        // A especificação exige converter para minúsculas e remover espaços indevidos
        // antes de comparar — é o que impede que "SINTEK.COM.BR" digitado pelo usuário
        // deixe de bater com o diretório.
        EmailDomain.Parse(input).Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("contato@sintek.com.br")]
    [InlineData("sintek..com.br")]
    [InlineData("-sintek.com.br")]
    [InlineData("sintek-.com.br")]
    [InlineData("sintek.com.br/path")]
    [InlineData("sintek com br")]
    public void TryParse_Recusa_ValoresInvalidos(string? input)
    {
        EmailDomain.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_Recusa_DominioAcimaDoLimite()
    {
        var tooLong = string.Join('.', Enumerable.Repeat("abcdefghij", 30));

        var act = () => EmailDomain.Parse(tooLong);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Recusa_RotuloAcimaDoLimite()
    {
        var longLabel = new string('a', 64);

        var act = () => EmailDomain.Parse($"{longLabel}.com");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Igualdade_EhOrdinalEIgnoraCaixaNaEntrada()
    {
        EmailDomain.Parse("Sintek.Com.BR").Should().Be(EmailDomain.Parse("sintek.com.br"));
        EmailDomain.Parse("sintek.com.br").Should().NotBe(EmailDomain.Parse("cliente.com.br"));
    }

    [Theory]
    [InlineData("empresa.com", "empresa.com", false, true)]
    [InlineData("vendas.empresa.com", "empresa.com", false, false)]
    [InlineData("vendas.empresa.com", "empresa.com", true, true)]
    [InlineData("a.b.empresa.com", "empresa.com", true, true)]
    [InlineData("empresa.com", "vendas.empresa.com", true, false)]
    public void IsSameOrSubdomainOf_RespeitaPermissaoDeSubdominios(
        string candidate, string parent, bool allowSubdomains, bool expected)
    {
        EmailDomain.Parse(candidate)
            .IsSameOrSubdomainOf(EmailDomain.Parse(parent), allowSubdomains)
            .Should().Be(expected);
    }

    [Fact]
    public void IsSameOrSubdomainOf_NaoConfunde_SufixoSemPonto()
    {
        // 'malempresa.com' termina com 'empresa.com' como texto. Se a comparação de
        // subdomínio não exigisse o ponto separador, um domínio hostil registrado de
        // propósito entraria em um diretório corporativo.
        EmailDomain.Parse("malempresa.com")
            .IsSameOrSubdomainOf(EmailDomain.Parse("empresa.com"), allowSubdomains: true)
            .Should().BeFalse();
    }
}
