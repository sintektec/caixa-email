using Sintek.Mail.Domain.ValueObjects;
using Xunit;

namespace Sintek.Mail.DomainTests;

public class EmailAddressTests
{
    // Parte local em MAIUSCULA, dominio em minuscula (D-009). A caixa da
    // entrada nao importa: as tres linhas abaixo produzem o mesmo valor.
    [Theory]
    [InlineData("user@example.com", "USER", "example.com")]
    [InlineData("user.name+tag@sub.example.com", "USER.NAME+TAG", "sub.example.com")]
    [InlineData("USER@EXAMPLE.COM", "USER", "example.com")]
    public void Parse_ValidEmail_ReturnsCorrectParts(string input, string expectedLocal, string expectedDomain)
    {
        var email = EmailAddress.Parse(input);
        Assert.Equal(expectedLocal, email.LocalPart);
        Assert.Equal(expectedDomain, email.Domain.Value);
    }

    // ThrowsAny pelo mesmo motivo de EmailDomainTests: Assert.Throws<T> exige
    // tipo exato e InvalidEmailAddressException deriva de ArgumentException.
    // "user@.com" saiu da lista: por D-008 o dominio aceita qualquer rotulo,
    // entao esse endereco e valido -- ver Parse_DominioSemFormato_EAceito.
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void Parse_InvalidEmail_ThrowsException(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => EmailAddress.Parse(input));
    }

    [Theory]
    [InlineData("user@.com", ".com")]
    [InlineData("user@intranet", "intranet")]
    public void Parse_DominioSemFormato_EAceito(string input, string expectedDomain)
    {
        var email = EmailAddress.Parse(input);
        Assert.Equal(expectedDomain, email.Domain.Value);
    }

    [Fact]
    public void TryParse_EnderecoInvalido_RetornaFalse()
    {
        Assert.False(EmailAddress.TryParse("invalid", out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_EnderecoValido_RetornaNormalizado()
    {
        Assert.True(EmailAddress.TryParse("  User@Example.COM  ", out var result));
        Assert.NotNull(result);
        Assert.Equal("USER@example.com", result!.FullAddress);
    }

    [Fact]
    public void Equals_SameEmail_ReturnsTrue()
    {
        var a = EmailAddress.Parse("user@example.com");
        var b = EmailAddress.Parse("USER@EXAMPLE.COM");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentEmail_ReturnsFalse()
    {
        var a = EmailAddress.Parse("user@example.com");
        var b = EmailAddress.Parse("other@example.com");
        Assert.NotEqual(a, b);
    }
}
