using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;
using Xunit;

namespace Sintek.Mail.DomainTests;

public class DomainMembershipEvaluatorTests
{
    [Fact]
    public void IsAddressInDomain_ExactMatch_ReturnsTrue()
    {
        var domain = new DomainDirectory("example.com");
        var evaluator = new DomainMembershipEvaluator(domain);
        Assert.True(evaluator.IsAddressInDomain("user@example.com"));
    }

    [Fact]
    public void IsAddressInDomain_SubdomainNotAllowed_ReturnsFalse()
    {
        var domain = new DomainDirectory("example.com") { AllowSubdomains = false };
        var evaluator = new DomainMembershipEvaluator(domain);
        Assert.False(evaluator.IsAddressInDomain("user@sub.example.com"));
    }

    [Fact]
    public void IsAddressInDomain_SubdomainAllowed_ReturnsTrue()
    {
        var domain = new DomainDirectory("example.com") { AllowSubdomains = true };
        var evaluator = new DomainMembershipEvaluator(domain);
        Assert.True(evaluator.IsAddressInDomain("user@sub.example.com"));
    }

    [Fact]
    public void IsAddressInDomain_AliasMatch_ReturnsTrue()
    {
        var domain = new DomainDirectory("example.com");
        var alias = new DomainAlias { DomainId = domain.Id, DomainName = "alias.com" };
        var evaluator = new DomainMembershipEvaluator(domain, new[] { alias });
        Assert.True(evaluator.IsAddressInDomain("user@alias.com"));
    }

    [Fact]
    public void IsAddressInDomain_NoMatch_ReturnsFalse()
    {
        var domain = new DomainDirectory("example.com");
        var evaluator = new DomainMembershipEvaluator(domain);
        Assert.False(evaluator.IsAddressInDomain("user@other.com"));
    }

    [Fact]
    public void EvaluateMessage_SenderOnly_MatchingSender_ReturnsTrue()
    {
        var domain = new DomainDirectory("example.com") { ValidationMode = ValidationMode.SenderOnly };
        var evaluator = new DomainMembershipEvaluator(domain);
        var message = new Message { FromAddress = "user@example.com" };
        Assert.True(evaluator.EvaluateMessage(message));
    }

    [Fact]
    public void EvaluateMessage_SenderOnly_NonMatchingSender_ReturnsFalse()
    {
        var domain = new DomainDirectory("example.com") { ValidationMode = ValidationMode.SenderOnly };
        var evaluator = new DomainMembershipEvaluator(domain);
        var message = new Message { FromAddress = "user@other.com" };
        Assert.False(evaluator.EvaluateMessage(message));
    }
}
