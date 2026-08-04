using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("Folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(200);
        builder.Property(f => f.RemotePath).HasMaxLength(500);
        builder.Property(f => f.FolderType).HasConversion<string>().HasMaxLength(50);
        builder.HasOne(f => f.ParentFolder).WithMany(f => f.SubFolders).HasForeignKey(f => f.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(f => f.RestrictedToDomain).WithMany().HasForeignKey(f => f.RestrictedToDomainId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(f => new { f.AccountId, f.RemotePath }).IsUnique();
    }
}
