using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Ports;

/// <summary>
/// Repository for mail-related persistence operations.
/// </summary>
public interface IMailRepository
{
    // Domains
    Task<DomainDirectory?> GetDomainByIdAsync(Guid id, CancellationToken ct = default);
    Task<DomainDirectory?> GetDomainByNameAsync(string domainName, CancellationToken ct = default);
    Task<IReadOnlyList<DomainDirectory>> GetAllDomainsAsync(CancellationToken ct = default);
    Task AddDomainAsync(DomainDirectory domain, CancellationToken ct = default);
    Task UpdateDomainAsync(DomainDirectory domain, CancellationToken ct = default);
    Task DeleteDomainAsync(Guid id, CancellationToken ct = default);

    // Accounts
    Task<Account?> GetAccountByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetAccountsByDomainAsync(Guid domainId, CancellationToken ct = default);
    Task AddAccountAsync(Account account, CancellationToken ct = default);
    Task UpdateAccountAsync(Account account, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid id, CancellationToken ct = default);

    // Folders
    Task<Folder?> GetFolderByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Folder>> GetFoldersByAccountAsync(Guid accountId, CancellationToken ct = default);
    Task AddFolderAsync(Folder folder, CancellationToken ct = default);
    Task UpdateFolderAsync(Folder folder, CancellationToken ct = default);
    Task DeleteFolderAsync(Guid id, CancellationToken ct = default);

    // Messages
    Task<Message?> GetMessageByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesByFolderAsync(Guid folderId, int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetUnreadMessagesAsync(Guid accountId, CancellationToken ct = default);
    Task AddMessageAsync(Message message, CancellationToken ct = default);
    Task UpdateMessageAsync(Message message, CancellationToken ct = default);
    Task DeleteMessageAsync(Guid id, CancellationToken ct = default);

    // Outbox
    Task<OutboxOperation?> GetOutboxOperationByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxOperation>> GetPendingOutboxOperationsAsync(Guid accountId, CancellationToken ct = default);
    Task AddOutboxOperationAsync(OutboxOperation operation, CancellationToken ct = default);
    Task UpdateOutboxOperationAsync(OutboxOperation operation, CancellationToken ct = default);

    // Audit
    Task AddAuditLogAsync(AuditLog entry, CancellationToken ct = default);

    // Unit of work
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
