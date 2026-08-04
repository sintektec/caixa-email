namespace Sintek.Mail.Application.DTOs;

public sealed record MessageBodyDto(
    Guid MessageId,
    string? HtmlBody,
    string? TextBody,
    string? SanitizedHtml,
    bool HasRemoteContent
);
