using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence;

/// <summary>
/// Contexto do banco local, criptografado com SQLCipher.
/// </summary>
/// <remarks>
/// Todo o estado da aplicação vive aqui: é a leitura deste banco que a interface faz, e
/// não a rede. A sincronização apenas o alimenta — o que é exatamente o que permite à
/// aplicação continuar plenamente utilizável offline.
/// </remarks>
public sealed class MailDbContext : DbContext
{
    public MailDbContext(DbContextOptions<MailDbContext> options) : base(options)
    {
    }

    /// <summary>Diretórios de Domínio.</summary>
    public DbSet<DomainDirectory> DomainDirectories => Set<DomainDirectory>();

    /// <summary>Domínios adicionais aceitos por cada diretório.</summary>
    public DbSet<DomainAlias> DomainAliases => Set<DomainAlias>();

    /// <summary>Contas de e-mail.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>Pastas.</summary>
    public DbSet<Folder> Folders => Set<Folder>();

    /// <summary>Mensagens (sem o corpo).</summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <summary>Participantes das mensagens.</summary>
    public DbSet<MessageAddress> MessageAddresses => Set<MessageAddress>();

    /// <summary>Corpos das mensagens.</summary>
    public DbSet<MessageBody> MessageBodies => Set<MessageBody>();

    /// <summary>Anexos.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    /// <summary>Conversas.</summary>
    public DbSet<MessageThread> MessageThreads => Set<MessageThread>();

    /// <summary>Categorias.</summary>
    public DbSet<Category> Categories => Set<Category>();

    /// <summary>Associação entre mensagens e categorias.</summary>
    public DbSet<MessageCategory> MessageCategories => Set<MessageCategory>();

    /// <summary>Regras automáticas.</summary>
    public DbSet<Rule> Rules => Set<Rule>();

    /// <summary>Condições das regras.</summary>
    public DbSet<RuleCondition> RuleConditions => Set<RuleCondition>();

    /// <summary>Ações das regras.</summary>
    public DbSet<RuleAction> RuleActions => Set<RuleAction>();

    /// <summary>Fila de saída.</summary>
    public DbSet<OutboxOperation> OutboxOperations => Set<OutboxOperation>();

    /// <summary>Pesquisas salvas.</summary>
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();

    /// <summary>Assinaturas.</summary>
    public DbSet<Signature> Signatures => Set<Signature>();

    /// <summary>Modelos de mensagem.</summary>
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    /// <summary>Registro de auditoria.</summary>
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>Preferências da aplicação.</summary>
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MailDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
