using System.Text.RegularExpressions;
using Sintek.Mail.Application.Ports;

namespace Sintek.Mail.Infrastructure.Services;

public sealed class HtmlSanitizerService : IHtmlSanitizer
{
    private static readonly string[] DangerousTags = { "script", "iframe", "object", "embed", "form", "input", "button", "textarea", "select", "link", "meta", "base" };
    private static readonly string[] DangerousAttributes = { "onload", "onerror", "onclick", "onmouseover", "onfocus", "onblur", "onsubmit", "onreset", "onchange", "onkeydown", "onkeyup", "onkeypress", "formaction", "xlink:href", "data" };

    public string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        var sanitized = html;

        // Remove dangerous tags
        foreach (var tag in DangerousTags)
        {
            sanitized = Regex.Replace(sanitized, $@"<{tag}[^>]*>.*?</{tag}>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            sanitized = Regex.Replace(sanitized, $@"<{tag}[^>]*/>", string.Empty, RegexOptions.IgnoreCase);
        }

        // Remove dangerous attributes
        foreach (var attr in DangerousAttributes)
        {
            sanitized = Regex.Replace(sanitized, $@"\s+{attr}\s*=\s*[""'][^""']*[""']", string.Empty, RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, $@"\s+{attr}\s*=\s*[^\s>]+", string.Empty, RegexOptions.IgnoreCase);
        }

        // Remove javascript: and data: URLs
        sanitized = Regex.Replace(sanitized, @"javascript\s*:", string.Empty, RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"data\s*:", string.Empty, RegexOptions.IgnoreCase);

        // Remove CSS expressions
        sanitized = Regex.Replace(sanitized, @"expression\s*\(", string.Empty, RegexOptions.IgnoreCase);

        return sanitized;
    }

    public bool HasRemoteContent(string html)
    {
        if (string.IsNullOrEmpty(html))
            return false;

        // Check for remote images, stylesheets, etc.
        return Regex.IsMatch(html, @"<img[^>]+src\s*=\s*[""']https?://", RegexOptions.IgnoreCase)
            || Regex.IsMatch(html, @"<link[^>]+href\s*=\s*[""']https?://", RegexOptions.IgnoreCase)
            || Regex.IsMatch(html, @"url\s*\(\s*[""']?https?://", RegexOptions.IgnoreCase)
            || Regex.IsMatch(html, @"@import\s+[""']https?://", RegexOptions.IgnoreCase);
    }

    public string ExtractText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Remove HTML tags
        var text = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);
        // Normalize whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
