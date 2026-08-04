namespace Sintek.Mail.Application.Ports;

/// <summary>
/// Sanitizes HTML content for safe rendering.
/// </summary>
public interface IHtmlSanitizer
{
    /// <summary>Sanitizes HTML, removing dangerous elements and attributes.</summary>
    string Sanitize(string html);

    /// <summary>Checks if HTML contains remote content (images, etc.).</summary>
    bool HasRemoteContent(string html);

    /// <summary>Extracts plain text from HTML.</summary>
    string ExtractText(string html);
}
