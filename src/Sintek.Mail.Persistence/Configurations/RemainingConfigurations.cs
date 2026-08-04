using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class DomainAliasConfiguration : IEntityTypeConfiguration<DomainAlias>
{
    public void Configure(EntityTypeBuilder<DomainAlias> builder)
    {
        builder.ToTable("DomainAliases");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.DomainName).IsRequired().HasMaxLength(253);
        builder.HasIndex(a => new { a.DomainId, a.DomainName }).IsUnique();
    }
}

public sealed class MessageBodyConfiguration : IEntityTypeConfiguration<MessageBody>
{
    public void Configure(EntityTypeBuilder<MessageBody> builder)
    {
        builder.ToTable("MessageBodies");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.HtmlBody).HasColumnType("TEXT");
        builder.Property(b => b.TextBody).HasColumnType("TEXT");
        builder.Property(b => b.SanitizedHtml).HasColumnType("TEXT");
    }
}

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(500);
        builder.Property(a => a.ContentType).HasMaxLength(200);
        builder.Property(a => a.StoragePath).HasMaxLength(1000);
    }
}

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        builder.Property(c => c.ColorHex).HasMaxLength(7);
    }
}

public sealed class MessageCategoryConfiguration : IEntityTypeConfiguration<MessageCategory>
{
    public void Configure(EntityTypeBuilder<MessageCategory> builder)
    {
        builder.ToTable("MessageCategories");
        builder.HasKey(mc => new { mc.MessageId, mc.CategoryId });
        builder.HasOne(mc => mc.Message).WithMany(m => m.MessageCategories).HasForeignKey(mc => mc.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(mc => mc.Category).WithMany(c => c.MessageCategories).HasForeignKey(mc => mc.CategoryId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> builder)
    {
        builder.ToTable("Rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.MatchType).HasConversion<string>().HasMaxLength(50);
        builder.HasMany(r => r.Conditions).WithOne(c => c.Rule).HasForeignKey(c => c.RuleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.Actions).WithOne(a => a.Rule).HasForeignKey(a => a.RuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RuleConditionConfiguration : IEntityTypeConfiguration<RuleCondition>
{
    public void Configure(EntityTypeBuilder<RuleCondition> builder)
    {
        builder.ToTable("RuleConditions");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Field).HasMaxLength(100);
        builder.Property(c => c.Operator).HasMaxLength(50);
        builder.Property(c => c.Value).HasMaxLength(500);
    }
}

public sealed class RuleActionConfiguration : IEntityTypeConfiguration<RuleAction>
{
    public void Configure(EntityTypeBuilder<RuleAction> builder)
    {
        builder.ToTable("RuleActions");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActionType).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.TargetFolderId).HasMaxLength(50);
        builder.Property(a => a.CategoryName).HasMaxLength(100);
        builder.Property(a => a.ForwardTo).HasMaxLength(320);
    }
}

public sealed class SavedSearchConfiguration : IEntityTypeConfiguration<SavedSearch>
{
    public void Configure(EntityTypeBuilder<SavedSearch> builder)
    {
        builder.ToTable("SavedSearches");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Query).HasMaxLength(1000);
    }
}

public sealed class SignatureConfiguration : IEntityTypeConfiguration<Signature>
{
    public void Configure(EntityTypeBuilder<Signature> builder)
    {
        builder.ToTable("Signatures");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.HtmlContent).HasColumnType("TEXT");
    }
}

public sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.ToTable("MessageTemplates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Subject).HasMaxLength(500);
        builder.Property(t => t.HtmlBody).HasColumnType("TEXT");
        builder.Property(t => t.TextBody).HasColumnType("TEXT");
    }
}

public sealed class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettings>
{
    public void Configure(EntityTypeBuilder<AppSettings> builder)
    {
        builder.ToTable("AppSettings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).IsRequired().HasMaxLength(200);
        builder.HasIndex(s => s.Key).IsUnique();
        builder.Property(s => s.Value).HasColumnType("TEXT");
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.EventType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.EntityType).HasMaxLength(100);
        builder.Property(a => a.Description).HasMaxLength(1000);
        builder.Property(a => a.DetailsJson).HasColumnType("TEXT");
        builder.Property(a => a.Severity).HasMaxLength(20);
        builder.HasIndex(a => a.Timestamp);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
