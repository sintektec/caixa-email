using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

public sealed class MessageAddressConfiguration : IEntityTypeConfiguration<MessageAddress>
{
    public void Configure(EntityTypeBuilder<MessageAddress> builder)
    {
        builder.ToTable("MessageAddresses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Address).IsRequired().HasMaxLength(320);
        builder.Property(a => a.DisplayName).HasMaxLength(200);
        builder.Property(a => a.Domain).IsRequired().HasMaxLength(253);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(50);
        builder.HasIndex(a => a.Domain);
        builder.HasIndex(a => new { a.MessageId, a.Kind });
    }
}
