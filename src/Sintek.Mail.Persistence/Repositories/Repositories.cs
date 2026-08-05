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
            // julianday() e desempate por Id: ver SqliteFunctions.
            .OrderByDescending(m => SqliteFunctions.JulianDay(m.ReceivedAt))
            .ThenByDescending(m => m.Id)
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
    public async Task<IReadOnlyList<KnownCorrespondent>> ListKnownCorrespondentsAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        // O domínio do endereço é derivado do value object, que o SQL não enxerga; a
        // projeção traz os pares e a decomposição acontece em memória. O teto de linhas
        // protege contra caixas com décadas de histórico.
        var rows = await _context.Messages
            .Where(m => m.AccountId == accountId
                && m.IsRead
                && !m.IsFlaggedAsSpamByServer
                && m.FromDisplayName != null
                && m.FromAddress != null)
            .Select(m => new { m.FromDisplayName, m.FromAddress })
            .Distinct()
            .Take(2000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => new KnownCorrespondent(r.FromDisplayName!, r.FromAddress!.Domain))
            .DistinctBy(k => (k.DisplayName, k.Domain.Value))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Message>> ListCachedContentAsync(
        DateTimeOffset downloadedBefore, CancellationToken cancellationToken = default)
        => await _context.Messages
            .Include(m => m.Body)
            .Include(m => m.Attachments)
            // Uid > 0 é o que prova que o servidor ainda tem a mensagem: sem ele o
            // download não teria de onde recomeçar.
            //
            // A idade do cache é a do corpo: o anexo não guarda instante próprio, e na
            // prática desce depois do corpo — usar a data do corpo para a mensagem inteira
            // erra no máximo para o lado de preservar por mais tempo.
            .Where(m => m.Uid != null && m.Uid > 0
                && m.SyncState == MessageSyncState.Synced
                && m.Body != null
                && m.Body.DownloadedAt != null
                && SqliteFunctions.JulianDay(m.Body.DownloadedAt.Value)
                    < SqliteFunctions.JulianDay(downloadedBefore))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
                // A comparação passa pelo julianday() pelo mesmo motivo da ordenação:
                // o provedor não traduz comparação de DateTimeOffset. Ver SqliteFunctions.
                && (o.NextAttemptAt == null
                    || SqliteFunctions.JulianDay(o.NextAttemptAt.Value) <= SqliteFunctions.JulianDay(now)))
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

/// <inheritdoc cref="IRuleRepository" />
public sealed class RuleRepository : IRuleRepository
{
    private readonly MailDbContext _context;

    public RuleRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Rule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Rules
            .Include(r => r.Conditions)
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Rule>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.Rules
            .Include(r => r.Conditions)
            .Include(r => r.Actions)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Rule>> ListEnabledForAccountAsync(
        Guid accountId, Guid domainDirectoryId, CancellationToken cancellationToken = default)
        => await _context.Rules
            .Include(r => r.Conditions)
            .Include(r => r.Actions)
            .Where(r => r.IsEnabled
                && (r.AccountId == null || r.AccountId == accountId)
                && (r.DomainDirectoryId == null || r.DomainDirectoryId == domainDirectoryId))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(Rule rule, CancellationToken cancellationToken = default)
        => await _context.Rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Rule rule) => _context.Rules.Remove(rule);
}

/// <inheritdoc cref="ICategoryRepository" />
public sealed class CategoryRepository : ICategoryRepository
{
    private readonly MailDbContext _context;

    public CategoryRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default)
        => await _context.Categories
            .Where(c => c.AccountId == null || accountId == null || c.AccountId == accountId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
        => await _context.Categories.AddAsync(category, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Category category) => _context.Categories.Remove(category);

    /// <inheritdoc />
    public Task<bool> IsAssignedAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default)
        => _context.MessageCategories
            .AnyAsync(mc => mc.MessageId == messageId && mc.CategoryId == categoryId, cancellationToken);

    /// <inheritdoc />
    public async Task AssignAsync(MessageCategory link, CancellationToken cancellationToken = default)
        => await _context.MessageCategories.AddAsync(link, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> UnassignAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        var link = await _context.MessageCategories
            .FirstOrDefaultAsync(
                mc => mc.MessageId == messageId && mc.CategoryId == categoryId, cancellationToken)
            .ConfigureAwait(false);

        if (link is null)
        {
            return false;
        }

        _context.MessageCategories.Remove(link);
        return true;
    }
}

/// <inheritdoc cref="IMessageTemplateRepository" />
public sealed class MessageTemplateRepository : IMessageTemplateRepository
{
    private readonly MailDbContext _context;

    public MessageTemplateRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<MessageTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplate>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default)
        => await _context.MessageTemplates
            .Where(t => t.AccountId == null || accountId == null || t.AccountId == accountId)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(MessageTemplate template, CancellationToken cancellationToken = default)
        => await _context.MessageTemplates.AddAsync(template, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(MessageTemplate template) => _context.MessageTemplates.Remove(template);
}

/// <inheritdoc cref="ISenderReputationRepository" />
public sealed class SenderReputationRepository : ISenderReputationRepository
{
    private readonly MailDbContext _context;

    public SenderReputationRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<SenderReputation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.SenderReputations.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SenderReputation>> ListAsync(
        SenderReputationKind? kind = null, CancellationToken cancellationToken = default)
        => await _context.SenderReputations
            .Where(s => kind == null || s.Kind == kind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(SenderReputation entry, CancellationToken cancellationToken = default)
        => await _context.SenderReputations.AddAsync(entry, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(SenderReputation entry) => _context.SenderReputations.Remove(entry);
}

/// <inheritdoc cref="ISavedSearchRepository" />
public sealed class SavedSearchRepository : ISavedSearchRepository
{
    private readonly MailDbContext _context;

    public SavedSearchRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<SavedSearch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.SavedSearches.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<SavedSearch?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _context.SavedSearches.FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedSearch>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.SavedSearches
            .OrderByDescending(s => s.IsPinned)
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(SavedSearch search, CancellationToken cancellationToken = default)
        => await _context.SavedSearches.AddAsync(search, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(SavedSearch search) => _context.SavedSearches.Remove(search);
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
            .OrderByDescending(e => SqliteFunctions.JulianDay(e.OccurredAt))
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <inheritdoc cref="IRecipientHistoryRepository" />
public sealed class RecipientHistoryRepository : IRecipientHistoryRepository
{
    private readonly MailDbContext _context;

    public RecipientHistoryRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<RecipientHistory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.RecipientHistory.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<RecipientHistory?> GetByAddressAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        return _context.RecipientHistory.FirstOrDefaultAsync(
            h => h.AccountId == accountId && h.Address == address, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipientHistory>> ListForSuggestionAsync(
        Guid accountId, int limit, CancellationToken cancellationToken = default)
        => await _context.RecipientHistory
            .Where(h => h.AccountId == accountId)
            .OrderByDescending(h => SqliteFunctions.JulianDay(h.LastUsedAt))
            .ThenByDescending(h => h.UseCount)
            .Take(Math.Max(limit, 1))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipientHistory>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => await _context.RecipientHistory
            .Where(h => h.AccountId == accountId)
            .OrderByDescending(h => SqliteFunctions.JulianDay(h.LastUsedAt))
            .ThenByDescending(h => h.UseCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(RecipientHistory entry, CancellationToken cancellationToken = default)
        => await _context.RecipientHistory.AddAsync(entry, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(RecipientHistory entry) => _context.RecipientHistory.Remove(entry);
}

/// <inheritdoc cref="IContactRepository" />
public sealed class ContactRepository : IContactRepository
{
    private readonly MailDbContext _context;

    public ContactRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<Contact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Contacts
            .Include(c => c.Emails)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Contact?> GetByExternalIdAsync(
        Guid accountId, string externalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        return _context.Contacts
            .Include(c => c.Emails)
            .FirstOrDefaultAsync(
                c => c.AccountId == accountId && c.ExternalId == externalId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Contact?> GetByEmailAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        return _context.Contacts
            .Include(c => c.Emails)
            .FirstOrDefaultAsync(
                c => c.AccountId == accountId && c.Emails.Any(e => e.Address == address),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => await _context.Contacts
            .Include(c => c.Emails)
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(Contact contact, CancellationToken cancellationToken = default)
        => await _context.Contacts.AddAsync(contact, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(Contact contact) => _context.Contacts.Remove(contact);
}

/// <inheritdoc cref="ICalendarRepository" />
public sealed class CalendarRepository : ICalendarRepository
{
    private readonly MailDbContext _context;

    public CalendarRepository(MailDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.CalendarEvents
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<CalendarEvent?> GetByUidAsync(
        Guid accountId, string uid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        return _context.CalendarEvents
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.AccountId == accountId && e.Uid == uid, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CalendarEvent?> GetBySourceMessageAsync(
        Guid messageId, CancellationToken cancellationToken = default)
        => _context.CalendarEvents
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.SourceMessageId == messageId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> ListInRangeAsync(
        Guid? accountId, DateTimeOffset from, DateTimeOffset until,
        CancellationToken cancellationToken = default)
        => await _context.CalendarEvents
            .Include(e => e.Attendees)
            .Where(e => (accountId == null || e.AccountId == accountId)
                // Comparação de data pelo julianday(): ver SqliteFunctions. Evento
                // recorrente entra sempre que começou antes do fim da janela, porque suas
                // ocorrências podem cair dentro dela com o primeiro encontro muito no
                // passado; quem expande a recorrência é o ICalendarSerializer.
                && SqliteFunctions.JulianDay(e.StartsAt) < SqliteFunctions.JulianDay(until)
                && (e.RecurrenceRule != null
                    || SqliteFunctions.JulianDay(e.EndsAt) > SqliteFunctions.JulianDay(from)))
            .OrderBy(e => SqliteFunctions.JulianDay(e.StartsAt))
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
        => await _context.CalendarEvents.AddAsync(calendarEvent, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Remove(CalendarEvent calendarEvent) => _context.CalendarEvents.Remove(calendarEvent);
}
