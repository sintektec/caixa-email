using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.DTOs;

public sealed record DomainDirectoryDto(
    Guid Id,
    string DomainName,
    string? Description,
    ValidationMode ValidationMode,
    InvalidEmailAction InvalidEmailAction,
    bool AllowSubdomains,
    bool IsActive,
    int SortOrder,
    bool IsFavorite,
    int AccountCount
);
