using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

/// <summary>Mapeamento do histórico de destinatários.</summary>
public sealed class RecipientHistoryConfiguration : IEntityTypeConfiguration<RecipientHistory>
{
    public void Configure(EntityTypeBuilder<RecipientHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RecipientHistory");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.Address)
            .HasConversion(
                Converters.ValueObjectConverters.EmailAddressConverter,
                Converters.ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(h => h.DisplayName).HasMaxLength(256);

        // Um endereço aparece uma única vez por conta: a entrada acumula usos em vez de
        // se repetir. Sem esta restrição, uma corrida entre dois envios simultâneos
        // criaria duas linhas e a sugestão passaria a mostrar o mesmo endereço duas vezes.
        builder.HasIndex(h => new { h.AccountId, h.Address }).IsUnique();

        // A consulta de sugestão filtra pela conta e ordena por uso e recência.
        builder.HasIndex(h => new { h.AccountId, h.LastUsedAt }).IsDescending(false, true);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(h => h.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Mapeamento do catálogo de contatos.</summary>
public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Contacts");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(c => c.GivenName).HasMaxLength(128);
        builder.Property(c => c.FamilyName).HasMaxLength(128);
        builder.Property(c => c.Organization).HasMaxLength(256);
        builder.Property(c => c.JobTitle).HasMaxLength(256);
        builder.Property(c => c.PhoneNumber).HasMaxLength(64);
        builder.Property(c => c.Notes).HasMaxLength(4096);
        builder.Property(c => c.ExternalId).HasMaxLength(256);

        builder.HasIndex(c => new { c.AccountId, c.DisplayName });

        // O UID do vCard é o que impede a reimportação do mesmo arquivo de duplicar o
        // catálogo. Único por conta, não global: o mesmo contato pode legitimamente
        // existir em duas contas.
        builder.HasIndex(c => new { c.AccountId, c.ExternalId }).IsUnique();

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.Emails)
            .WithOne(e => e.Contact)
            .HasForeignKey(e => e.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Contact.Emails))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento dos endereços de um contato.</summary>
public sealed class ContactEmailConfiguration : IEntityTypeConfiguration<ContactEmail>
{
    public void Configure(EntityTypeBuilder<ContactEmail> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContactEmails");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Address)
            .HasConversion(
                Converters.ValueObjectConverters.EmailAddressConverter,
                Converters.ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(e => e.Label).HasMaxLength(64);

        builder.HasIndex(e => new { e.ContactId, e.Address }).IsUnique();
        builder.HasIndex(e => e.Address);
    }
}
