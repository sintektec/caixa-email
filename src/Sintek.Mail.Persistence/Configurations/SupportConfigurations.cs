using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

/// <summary>Mapeamento das regras automáticas.</summary>
public sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Rules");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(256).IsRequired();
        builder.Property(r => r.MatchType).HasConversion<int>();

        // As regras são avaliadas em ordem de prioridade a cada mensagem que chega.
        builder.HasIndex(r => new { r.AccountId, r.IsEnabled, r.Priority });

        builder.HasMany(r => r.Conditions)
            .WithOne(c => c.Rule)
            .HasForeignKey(c => c.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Actions)
            .WithOne(a => a.Rule)
            .HasForeignKey(a => a.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Rule.Conditions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Rule.Actions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento das condições de regra.</summary>
public sealed class RuleConditionConfiguration : IEntityTypeConfiguration<RuleCondition>
{
    public void Configure(EntityTypeBuilder<RuleCondition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RuleConditions");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Field).HasConversion<int>();
        builder.Property(c => c.Operator).HasConversion<int>();
        builder.Property(c => c.Value).HasMaxLength(1024);

        builder.HasIndex(c => c.RuleId);
    }
}

/// <summary>Mapeamento das ações de regra.</summary>
public sealed class RuleActionConfiguration : IEntityTypeConfiguration<RuleAction>
{
    public void Configure(EntityTypeBuilder<RuleAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RuleActions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActionType).HasConversion<int>();
        builder.Property(a => a.Value).HasMaxLength(1024);

        builder.HasIndex(a => a.RuleId);
    }
}

/// <summary>Mapeamento da fila de saída.</summary>
public sealed class OutboxOperationConfiguration : IEntityTypeConfiguration<OutboxOperation>
{
    public void Configure(EntityTypeBuilder<OutboxOperation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OutboxOperations");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OperationType).HasConversion<int>();
        builder.Property(o => o.Status).HasConversion<int>();
        builder.Property(o => o.PayloadJson).IsRequired();
        builder.Property(o => o.LastError).HasMaxLength(2048);

        // A consulta que o motor de sincronização faz a cada ciclo: o que está pronto
        // para executar nesta conta, em ordem de sequência.
        builder.HasIndex(o => new { o.AccountId, o.Status, o.NextAttemptAt });

        // A sequência precisa ser única por conta: é ela que define a ordem em que as
        // operações são aplicadas no servidor.
        builder.HasIndex(o => new { o.AccountId, o.Sequence }).IsUnique();

        builder.HasIndex(o => o.EntityId);

        builder.HasOne(o => o.Account)
            .WithMany()
            .HasForeignKey(o => o.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Mapeamento das listas de remetentes bloqueados e confiáveis.</summary>
public sealed class SenderReputationConfiguration : IEntityTypeConfiguration<SenderReputation>
{
    public void Configure(EntityTypeBuilder<SenderReputation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SenderReputations");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Kind).HasConversion<int>();

        builder.Property(s => s.Address)
            .HasConversion(
                Converters.ValueObjectConverters.NullableEmailAddressConverter,
                Converters.ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320);

        builder.Property(s => s.Domain)
            .HasConversion(
                Converters.ValueObjectConverters.NullableEmailDomainConverter,
                Converters.ValueObjectConverters.EmailDomainComparer)
            .HasMaxLength(253);

        // A avaliação na chegada busca por tipo; os alvos são poucos e filtram em memória.
        builder.HasIndex(s => s.Kind);
        builder.HasIndex(s => s.AccountId);
    }
}

/// <summary>Mapeamento das pesquisas salvas.</summary>
public sealed class SavedSearchConfiguration : IEntityTypeConfiguration<SavedSearch>
{
    public void Configure(EntityTypeBuilder<SavedSearch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SavedSearches");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(256).IsRequired();
        builder.Property(s => s.QueryJson).IsRequired();
        builder.HasIndex(s => s.Name).IsUnique();
    }
}

/// <summary>Mapeamento das assinaturas.</summary>
public sealed class SignatureConfiguration : IEntityTypeConfiguration<Signature>
{
    public void Configure(EntityTypeBuilder<Signature> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Signatures");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(256).IsRequired();
        builder.HasIndex(s => s.AccountId);

        builder.HasOne(s => s.Account)
            .WithMany()
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Mapeamento dos modelos de mensagem.</summary>
public sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MessageTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).HasMaxLength(256).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(2048);
        builder.HasIndex(t => t.Name);
    }
}

/// <summary>Mapeamento do registro de auditoria.</summary>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditLog");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasConversion<int>();
        builder.Property(e => e.Severity).HasConversion<int>();
        builder.Property(e => e.EntityType).HasMaxLength(128);
        builder.Property(e => e.Description).HasMaxLength(2048).IsRequired();

        builder.HasIndex(e => e.OccurredAt).IsDescending();
        builder.HasIndex(e => e.EventType);
        builder.HasIndex(e => e.DomainDirectoryId);
    }
}

/// <summary>Mapeamento das preferências da aplicação.</summary>
public sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AppSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Key).HasMaxLength(256).IsRequired();
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
