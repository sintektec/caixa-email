using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence;

public sealed class MailDbContext : DbContext
{
    public MailDbContext(DbContextOptions<MailDbContext> options) : base(options) { }

    public DbSet<DomainDirectory> Domains => Set<DomainDirectory>();
    public DbSet<DomainAlias> DomainAliases => Set<DomainAlias>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAddress> MessageAddresses => Set<MessageAddress>();
    public DbSet<MessageBody> MessageBodies => Set<MessageBody>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MessageCategory> MessageCategories => Set<MessageCategory>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleCondition> RuleConditions => Set<RuleCondition>();
    public DbSet<RuleAction> RuleActions => Set<RuleAction>();
    public DbSet<OutboxOperation> OutboxOperations => Set<OutboxOperation>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MailDbContext).Assembly);
    }
}
