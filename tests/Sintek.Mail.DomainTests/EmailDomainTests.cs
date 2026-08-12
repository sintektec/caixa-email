using Sintek.Mail.Domain.ValueObjects;
using Xunit;

namespace Sintek.Mail.DomainTests;

public class EmailDomainTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("sub.example.com", "sub.example.com")]
    public void Parse_ValidDomain_ReturnsNormalized(string input, string expected)
    {
        var domain = EmailDomain.Parse(input);
        Assert.Equal(expected, domain.Value);
    }

    // ThrowsAny, nao Throws: o xunit exige tipo EXATO em Assert.Throws<T>, e
    // InvalidEmailDomainException deriva de ArgumentException (D-007). O que
    // este teste afirma e o contrato -- "e um ArgumentException" -- nao a
    // classe concreta.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user@example.com")]
    public void Parse_InvalidDomain_ThrowsException(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => EmailDomain.Parse(input));
    }

    // D-008: o Diretorio de Dominio aceita qualquer rotulo. A regra que o
    // produto realmente impoe nao e o formato do nome do Diretorio, e sim a
    // igualdade entre o dominio da conta e o do Diretorio -- ver
    // DomainDirectory.ValidateAccount. Validar formato aqui restringiria nomes
    // legitimos (intranets, TLDs novos, hosts sem ponto) sem ganho.
    [Theory]
    [InlineData(".com")]
    [InlineData("example.")]
    [InlineData("intranet")]
    [InlineData("localhost")]
    public void Parse_QualquerRotulo_EAceito(string input)
    {
        var domain = EmailDomain.Parse(input);
        Assert.Equal(input.ToLowerInvariant(), domain.Value);
    }

    [Fact]
    public void IsSubdomainOf_Subdomain_ReturnsTrue()
    {
        var parent = EmailDomain.Parse("example.com");
        var child = EmailDomain.Parse("sub.example.com");
        Assert.True(child.IsSubdomainOf(parent));
    }

    [Fact]
    public void IsSubdomainOf_SameDomain_ReturnsFalse()
    {
        var a = EmailDomain.Parse("example.com");
        var b = EmailDomain.Parse("example.com");
        Assert.False(a.IsSubdomainOf(b));
    }

    [Fact]
    public void IsSubdomainOf_Unrelated_ReturnsFalse()
    {
        var a = EmailDomain.Parse("example.com");
        var b = EmailDomain.Parse("other.com");
        Assert.False(a.IsSubdomainOf(b));
    }
}
