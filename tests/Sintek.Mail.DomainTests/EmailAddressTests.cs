using Sintek.Mail.Domain.ValueObjects;
using Xunit;

namespace Sintek.Mail.DomainTests;

public class EmailAddressTests
{
    [Theory]
    [InlineData("user@example.com", "user", "example.com")]
    [InlineData("user.name+tag@sub.example.com", "user.name+tag", "sub.example.com")]
    [InlineData("USER@EXAMPLE.COM", "user", "example.com")]
    public void Parse_ValidEmail_ReturnsCorrectParts(string input, string expectedLocal, string expectedDomain)
    {
        var email = EmailAddress.Parse(input);
        Assert.Equal(expectedLocal, email.LocalPart);
        Assert.Equal(expectedDomain, email.Domain.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@.com")]
    public void Parse_InvalidEmail_ThrowsException(string input)
    {
        Assert.Throws<ArgumentException>(() => EmailAddress.Parse(input));
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
