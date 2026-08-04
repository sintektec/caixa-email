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

    [Theory]
    [InlineData("")]
    [InlineData(".com")]
    [InlineData("example.")]
    public void Parse_InvalidDomain_ThrowsException(string input)
    {
        Assert.Throws<ArgumentException>(() => EmailDomain.Parse(input));
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
