using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class OutboxOperationConfiguration : IEntityTypeConfiguration<OutboxOperation>
{
    public void Configure(EntityTypeBuilder<OutboxOperation> builder)
    {
        builder.ToTable("OutboxOperations");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.OperationType).HasConversion<string>().HasMaxLength(50);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(o => o.PayloadJson).HasColumnType("TEXT");
        builder.HasOne(o => o.DependsOn).WithMany().HasForeignKey(o => o.DependsOnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(o => new { o.AccountId, o.Status, o.Sequence });
    }
}
