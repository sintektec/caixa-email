namespace Sintek.Mail.Application.DTOs;

public sealed record AttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    bool IsInline,
    bool IsDownloaded,
    bool IsSuspicious
);
