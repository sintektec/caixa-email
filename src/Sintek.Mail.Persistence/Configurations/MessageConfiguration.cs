using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Subject).HasMaxLength(500);
        builder.Property(m => m.SubjectNormalized).HasMaxLength(500);
        builder.Property(m => m.FromAddress).HasMaxLength(320);
        builder.Property(m => m.Preview).HasMaxLength(500);
        builder.Property(m => m.MessageId).HasMaxLength(500);
        builder.Property(m => m.Importance).HasConversion<string>().HasMaxLength(50);
        builder.Property(m => m.SyncState).HasConversion<string>().HasMaxLength(50);
        builder.HasOne(m => m.Body).WithOne(b => b.Message).HasForeignKey<MessageBody>(b => b.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.Addresses).WithOne(a => a.Message).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.Attachments).WithOne(a => a.Message).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.FolderId, m.IsRead });
        builder.HasIndex(m => new { m.AccountId, m.SyncState });
        builder.HasIndex(m => m.MessageId);
        builder.HasIndex(m => m.ThreadId);
    }
}
