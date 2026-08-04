using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Application.Handlers;

public sealed record ChangeDomainNameCommand(
    Guid DomainId,
    string NewDomainName,
    bool ConfirmChanges = false
);

public sealed record ChangeDomainNameResult(
    bool RequiresConfirmation,
    IReadOnlyList<string> IncompatibleAccounts,
    IReadOnlyList<Guid> IncompatibleMessageIds
);

public sealed class ChangeDomainNameHandler
{
    private readonly IMailRepository _repository;

    public ChangeDomainNameHandler(IMailRepository repository)
    {
        _repository = repository;
    }

    public async Task<ChangeDomainNameResult> HandleAsync(ChangeDomainNameCommand command, CancellationToken ct = default)
    {
        var domain = await _repository.GetDomainByIdAsync(command.DomainId, ct)
            ?? throw new InvalidOperationException($"Domain '{command.DomainId}' not found.");

        // Dry-run: check for incompatible accounts
        var incompatibleAccounts = domain.GetIncompatibleAccounts(command.NewDomainName);

        if (incompatibleAccounts.Count > 0 && !command.ConfirmChanges)
        {
            return new ChangeDomainNameResult(
                RequiresConfirmation: true,
                IncompatibleAccounts: incompatibleAccounts.Select(a => a.EmailAddress).ToList(),
                IncompatibleMessageIds: new List<Guid>()
            );
        }

        // Execute change
        domain.ChangeDomainName(command.NewDomainName);
        await _repository.UpdateDomainAsync(domain, ct);

        // Log the change
        await _repository.AddAuditLogAsync(new AuditLog
        {
            EventType = "DomainNameChanged",
            EntityType = "DomainDirectory",
            EntityId = domain.Id,
            Description = $"Domain name changed from '{domain.DomainName}' to '{command.NewDomainName}'.",
            Severity = "Info"
        }, ct);

        await _repository.SaveChangesAsync(ct);

        return new ChangeDomainNameResult(
            RequiresConfirmation: false,
            IncompatibleAccounts: incompatibleAccounts.Select(a => a.EmailAddress).ToList(),
            IncompatibleMessageIds: new List<Guid>()
        );
    }
}
