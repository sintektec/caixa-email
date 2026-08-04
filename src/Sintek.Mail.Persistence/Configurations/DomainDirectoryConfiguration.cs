using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class DomainDirectoryConfiguration : IEntityTypeConfiguration<DomainDirectory>
{
    public void Configure(EntityTypeBuilder<DomainDirectory> builder)
    {
        builder.ToTable("Domains");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.DomainName).IsRequired().HasMaxLength(253);
        builder.HasIndex(d => d.DomainName).IsUnique();
        builder.Property(d => d.Description).HasMaxLength(500);
        builder.Property(d => d.ValidationMode).HasConversion<string>().HasMaxLength(50);
        builder.Property(d => d.InvalidEmailAction).HasConversion<string>().HasMaxLength(50);
        builder.HasMany(d => d.Accounts).WithOne(a => a.Domain).HasForeignKey(a => a.DomainId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(d => d.Aliases).WithOne(a => a.Domain).HasForeignKey(a => a.DomainId).OnDelete(DeleteBehavior.Cascade);
    }
}
