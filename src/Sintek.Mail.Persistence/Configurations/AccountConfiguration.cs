using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.EmailAddress).IsRequired().HasMaxLength(320);
        builder.HasIndex(a => a.EmailAddress);
        builder.Property(a => a.DisplayName).HasMaxLength(200);
        builder.Property(a => a.ImapHost).HasMaxLength(253);
        builder.Property(a => a.SmtpHost).HasMaxLength(253);
        builder.Property(a => a.ImapSecurity).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.SmtpSecurity).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.AuthenticationType).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.OAuthProvider).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.SyncStatus).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.BodyDownloadPolicy).HasConversion<string>().HasMaxLength(50);
        builder.HasMany(a => a.Folders).WithOne(f => f.Account).HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(a => a.Signatures).WithOne(s => s.Account).HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
