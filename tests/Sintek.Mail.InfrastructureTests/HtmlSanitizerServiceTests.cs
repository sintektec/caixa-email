using Sintek.Mail.Infrastructure.Services;
using Xunit;

namespace Sintek.Mail.InfrastructureTests;

public class HtmlSanitizerServiceTests
{
    private readonly HtmlSanitizerService _sanitizer = new();

    [Fact]
    public void Sanitize_RemovesScriptTags()
    {
        var html = "<p>Hello</p><script>alert('xss')</script>";
        var result = _sanitizer.Sanitize(html);
        Assert.DoesNotContain("script", result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void Sanitize_RemovesOnClickAttribute()
    {
        var html = "<a href='#' onclick='alert(1)'>Click</a>";
        var result = _sanitizer.Sanitize(html);
        Assert.DoesNotContain("onclick", result);
    }

    [Fact]
    public void Sanitize_RemovesJavascriptUrls()
    {
        var html = "<a href='javascript:alert(1)'>Click</a>";
        var result = _sanitizer.Sanitize(html);
        Assert.DoesNotContain("javascript:", result);
    }

    [Fact]
    public void HasRemoteContent_WithRemoteImage_ReturnsTrue()
    {
        var html = "<img src='https://example.com/image.png'>";
        Assert.True(_sanitizer.HasRemoteContent(html));
    }

    [Fact]
    public void HasRemoteContent_WithoutRemoteContent_ReturnsFalse()
    {
        var html = "<p>Hello</p>";
        Assert.False(_sanitizer.HasRemoteContent(html));
    }

    [Fact]
    public void ExtractText_RemovesHtmlTags()
    {
        var html = "<p>Hello <b>World</b></p>";
        var result = _sanitizer.ExtractText(html);
        Assert.Equal("Hello World", result);
    }
}
