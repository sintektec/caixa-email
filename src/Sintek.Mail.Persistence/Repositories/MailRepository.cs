using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Repositories;

public sealed class MailRepository : IMailRepository
{
    private readonly MailDbContext _context;

    public MailRepository(MailDbContext context)
    {
        _context = context;
    }

    // Domains
    public async Task<DomainDirectory?> GetDomainByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Domains.Include(d => d.Aliases).FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<DomainDirectory?> GetDomainByNameAsync(string domainName, CancellationToken ct = default)
        => await _context.Domains.Include(d => d.Aliases).FirstOrDefaultAsync(d => d.DomainName == domainName, ct);

    public async Task<IReadOnlyList<DomainDirectory>> GetAllDomainsAsync(CancellationToken ct = default)
        => await _context.Domains.Include(d => d.Aliases).OrderBy(d => d.SortOrder).ToListAsync(ct);

    public async Task AddDomainAsync(DomainDirectory domain, CancellationToken ct = default)
        => await _context.Domains.AddAsync(domain, ct);

    public Task UpdateDomainAsync(DomainDirectory domain, CancellationToken ct = default)
    {
        _context.Domains.Update(domain);
        return Task.CompletedTask;
    }

    public async Task DeleteDomainAsync(Guid id, CancellationToken ct = default)
    {
        var domain = await _context.Domains.FindAsync(new object[] { id }, ct);
        if (domain is not null) _context.Domains.Remove(domain);
    }

    // Accounts
    public async Task<Account?> GetAccountByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Accounts.FindAsync(new object[] { id }, ct);

    public async Task<IReadOnlyList<Account>> GetAccountsByDomainAsync(Guid domainId, CancellationToken ct = default)
        => await _context.Accounts.Where(a => a.DomainId == domainId).ToListAsync(ct);

    public async Task AddAccountAsync(Account account, CancellationToken ct = default)
        => await _context.Accounts.AddAsync(account, ct);

    public Task UpdateAccountAsync(Account account, CancellationToken ct = default)
    {
        _context.Accounts.Update(account);
        return Task.CompletedTask;
    }

    public async Task DeleteAccountAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _context.Accounts.FindAsync(new object[] { id }, ct);
        if (account is not null) _context.Accounts.Remove(account);
    }

    // Folders
    public async Task<Folder?> GetFolderByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Folders.Include(f => f.ParentFolder).FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<Folder>> GetFoldersByAccountAsync(Guid accountId, CancellationToken ct = default)
        => await _context.Folders.Where(f => f.AccountId == accountId).ToListAsync(ct);

    public async Task AddFolderAsync(Folder folder, CancellationToken ct = default)
        => await _context.Folders.AddAsync(folder, ct);

    public Task UpdateFolderAsync(Folder folder, CancellationToken ct = default)
    {
        _context.Folders.Update(folder);
        return Task.CompletedTask;
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        var folder = await _context.Folders.FindAsync(new object[] { id }, ct);
        if (folder is not null) _context.Folders.Remove(folder);
    }

    // Messages
    public async Task<Message?> GetMessageByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Messages
            .Include(m => m.Addresses)
            .Include(m => m.Body)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<Message>> GetMessagesByFolderAsync(Guid folderId, int skip, int take, CancellationToken ct = default)
        => await _context.Messages
            .Where(m => m.FolderId == folderId && !m.IsDeleted)
            .OrderByDescending(m => m.ReceivedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Message>> GetUnreadMessagesAsync(Guid accountId, CancellationToken ct = default)
        => await _context.Messages
            .Where(m => m.AccountId == accountId && !m.IsRead && !m.IsDeleted)
            .ToListAsync(ct);

    public async Task AddMessageAsync(Message message, CancellationToken ct = default)
        => await _context.Messages.AddAsync(message, ct);

    public Task UpdateMessageAsync(Message message, CancellationToken ct = default)
    {
        _context.Messages.Update(message);
        return Task.CompletedTask;
    }

    public async Task DeleteMessageAsync(Guid id, CancellationToken ct = default)
    {
        var message = await _context.Messages.FindAsync(new object[] { id }, ct);
        if (message is not null) _context.Messages.Remove(message);
    }

    // Outbox
    public async Task<OutboxOperation?> GetOutboxOperationByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.OutboxOperations.FindAsync(new object[] { id }, ct);

    public async Task<IReadOnlyList<OutboxOperation>> GetPendingOutboxOperationsAsync(Guid accountId, CancellationToken ct = default)
        => await _context.OutboxOperations
            .Where(o => o.AccountId == accountId && o.Status == Domain.Enums.OutboxOperationStatus.Pending)
            .OrderBy(o => o.Sequence)
            .ToListAsync(ct);

    public async Task AddOutboxOperationAsync(OutboxOperation operation, CancellationToken ct = default)
        => await _context.OutboxOperations.AddAsync(operation, ct);

    public Task UpdateOutboxOperationAsync(OutboxOperation operation, CancellationToken ct = default)
    {
        _context.OutboxOperations.Update(operation);
        return Task.CompletedTask;
    }

    // Audit
    public async Task AddAuditLogAsync(AuditLog entry, CancellationToken ct = default)
        => await _context.AuditLogs.AddAsync(entry, ct);

    // Unit of work
    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);
}
