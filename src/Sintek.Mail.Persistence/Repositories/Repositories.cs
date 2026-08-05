using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Persistence.Repositories;

/// <inheritdoc cref="IDomainDirectoryRepository" />
public sealed class DomainDirectoryRepository : IDomainDirectoryRepository
{
    private readonly MailDbContext _context;

    public DomainDirectoryRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<DomainDirectory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        // Os aliases vêm junto porque a regra de pertencimento os consulta em toda
        // avaliação: carregá-los depois geraria uma consulta extra por mensagem.
        => _context.DomainDirectories
            .Include(d => d.Aliases)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A busca cobre o domínio principal <b>e</b> os adicionais. Um domínio registrado como
    /// adicional de um diretório não pode virar o principal de outro: as duas contas do
    /// mesmo domínio acabariam em diretórios diferentes, e qual delas responde pela regra
    /// passaria a depender da ordem da consulta.
    /// </remarks>
    public Task<DomainDirectory?> GetByDomainAsync(EmailDomain domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);

        return _context.DomainDirectories
            .Include(d => d.Aliases)
            .FirstOrDefaultAsync(
                d => d.DomainName == domain || d.Aliases.Any(a => a.DomainName == domain),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DomainDirectory>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.DomainDirectories
            .Include(d => d.Aliases)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DomainName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(DomainDirectory directory, CancellationToken cancellationToken = default)
        => await _context.DomainDirectories.AddAsync(directory, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(DomainDirectory directory) => _context.DomainDirectories.Remove(directory);
}

/// <inheritdoc cref="IAccountRepository" />
public sealed class AccountRepository : IAccountRepository
{
    private readonly MailDbContext _context;

    public AccountRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Accounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Account?> GetByAddressAsync(EmailAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _context.Accounts.FirstOrDefaultAsync(a => a.EmailAddress == address, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListByDomainAsync(
        Guid domainDirectoryId, CancellationToken cancellationToken = default)
        => await _context.Accounts
            .Where(a => a.DomainDirectoryId == domainDirectoryId)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.EmailAddress)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await _context.Accounts
            .Where(a => a.IsActive)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
        => await _context.Accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Account account) => _context.Accounts.Remove(account);
}

/// <inheritdoc cref="IFolderRepository" />
public sealed class FolderRepository : IFolderRepository
{
    private readonly MailDbContext _context;

    public FolderRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Folders.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Folder>> ListByAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => await _context.Folders
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Folder?> GetByTypeAsync(
        Guid accountId, FolderType folderType, CancellationToken cancellationToken = default)
        => _context.Folders
            .FirstOrDefaultAsync(f => f.AccountId == accountId && f.FolderType == folderType, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(Folder folder, CancellationToken cancellationToken = default)
        => await _context.Folders.AddAsync(folder, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Folder folder) => _context.Folders.Remove(folder);

    /// <inheritdoc />
    public Task<int> CountMessagesAsync(Guid folderId, CancellationToken cancellationToken = default)
        => _context.Messages.CountAsync(m => m.FolderId == folderId && !m.IsDeleted, cancellationToken);
}

/// <inheritdoc cref="IMessageRepository" />
public sealed class MessageRepository : IMessageRepository
{
    private readonly MailDbContext _context;

    public MessageRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Messages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Message?> GetWithParticipantsAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Messages
            .Include(m => m.Addresses)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageParticipant>> GetParticipantsAsync(
        Guid messageId, CancellationToken cancellationToken = default)
        // Projeção direta: a avaliação da regra de domínio precisa só do campo e do
        // domínio, e materializar a mensagem inteira a cada arrastar e soltar seria
        // desperdício.
        => await _context.MessageAddresses
            .Where(a => a.MessageId == messageId)
            .Select(a => new MessageParticipant(a.Kind, a.Domain))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageParticipant>>> GetParticipantsAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<MessageParticipant>>();
        }

        var rows = await _context.MessageAddresses
            .Where(a => messageIds.Contains(a.MessageId))
            .Select(a => new { a.MessageId, a.Kind, a.Domain })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MessageParticipant>)g
                    .Select(r => new MessageParticipant(r.Kind, r.Domain))
                    .ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListIdsByFolderAsync(
        Guid folderId, CancellationToken cancellationToken = default)
        => await _context.Messages
            .Where(m => m.FolderId == folderId && !m.IsDeleted)
            .OrderByDescending(m => m.ReceivedAt)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Message?> GetByUidAsync(Guid folderId, long uid, CancellationToken cancellationToken = default)
        => _context.Messages
            .FirstOrDefaultAsync(m => m.FolderId == folderId && m.Uid == uid, cancellationToken);

    /// <inheritdoc />
    public Task<Message?> GetByMessageIdAsync(
        Guid accountId, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        return _context.Messages
            .FirstOrDefaultAsync(m => m.AccountId == accountId && m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> ListUidsByFolderAsync(
        Guid folderId, CancellationToken cancellationToken = default)
        => await _context.Messages
            .Where(m => m.FolderId == folderId && m.Uid != null && m.Uid > 0)
            .OrderBy(m => m.Uid)
            .Select(m => m.Uid!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<int> CountUnreadAsync(Guid folderId, CancellationToken cancellationToken = default)
        => _context.Messages
            .CountAsync(m => m.FolderId == folderId && !m.IsDeleted && !m.IsRead, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Message>> ListInRestrictedFoldersAsync(
        Guid domainDirectoryId, CancellationToken cancellationToken = default)
        => await _context.Messages
            .Where(m => !m.IsDeleted
                && _context.Folders.Any(f =>
                    f.Id == m.FolderId && f.EffectiveRestrictionDomainDirectoryId == domainDirectoryId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(Message message, CancellationToken cancellationToken = default)
        => await _context.Messages.AddAsync(message, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Message message) => _context.Messages.Remove(message);
}

/// <inheritdoc cref="IOutboxRepository" />
public sealed class OutboxRepository : IOutboxRepository
{
    private readonly MailDbContext _context;

    public OutboxRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task AddAsync(OutboxOperation operation, CancellationToken cancellationToken = default)
        => await _context.OutboxOperations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxOperation>> ListReadyAsync(
        Guid accountId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
        => await _context.OutboxOperations
            .Where(o => o.AccountId == accountId
                && (o.Status == OutboxOperationStatus.Pending || o.Status == OutboxOperationStatus.Failed)
                && (o.NextAttemptAt == null || o.NextAttemptAt <= now))
            // A ordem de sequência é o que garante que "mover" seja aplicado antes de
            // "marcar como lida", e não o contrário.
            .OrderBy(o => o.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxOperation>> ListPendingAsync(
        Guid? accountId, CancellationToken cancellationToken = default)
        => await _context.OutboxOperations
            .Where(o => (accountId == null || o.AccountId == accountId)
                && o.Status != OutboxOperationStatus.Completed
                && o.Status != OutboxOperationStatus.Cancelled)
            .OrderBy(o => o.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<OutboxOperation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.OutboxOperations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<long> NextSequenceAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        // MAX + 1 dentro da transação do caso de uso. O índice único
        // (AccountId, Sequence) é a garantia final: se duas escritas concorrentes
        // calcularem o mesmo número, a segunda falha em vez de embaralhar a ordem.
        var current = await _context.OutboxOperations
            .Where(o => o.AccountId == accountId)
            .MaxAsync(o => (long?)o.Sequence, cancellationToken)
            .ConfigureAwait(false);

        return (current ?? 0) + 1;
    }
}

/// <inheritdoc cref="IAuditLogRepository" />
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly MailDbContext _context;

    public AuditLogRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        => await _context.AuditLog.AddAsync(entry, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(
        int limit, CancellationToken cancellationToken = default)
        => await _context.AuditLog
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
