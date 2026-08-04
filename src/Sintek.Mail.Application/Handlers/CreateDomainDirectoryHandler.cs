using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Handlers;

public sealed record CreateDomainDirectoryCommand(
    string DomainName,
    string? Description = null,
    bool AllowSubdomains = false
);

public sealed class CreateDomainDirectoryHandler
{
    private readonly IMailRepository _repository;

    public CreateDomainDirectoryHandler(IMailRepository repository)
    {
        _repository = repository;
    }

    public async Task<DomainDirectoryDto> HandleAsync(CreateDomainDirectoryCommand command, CancellationToken ct = default)
    {
        // Validate domain format
        var domain = EmailDomain.Parse(command.DomainName);

        // Check for duplicates
        var existing = await _repository.GetDomainByNameAsync(domain.Value, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Domain directory '{domain.Value}' already exists.");

        var entity = new DomainDirectory(domain.Value, command.Description)
        {
            AllowSubdomains = command.AllowSubdomains
        };

        await _repository.AddDomainAsync(entity, ct);
        await _repository.SaveChangesAsync(ct);

        return new DomainDirectoryDto(
            entity.Id,
            entity.DomainName,
            entity.Description,
            entity.ValidationMode,
            entity.InvalidEmailAction,
            entity.AllowSubdomains,
            entity.IsActive,
            entity.SortOrder,
            entity.IsFavorite,
            0
        );
    }
}
