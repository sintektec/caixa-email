using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Persistence.Converters;

namespace Sintek.Mail.Persistence.Configurations;

/// <summary>Mapeamento dos Diretórios de Domínio.</summary>
public sealed class DomainDirectoryConfiguration : IEntityTypeConfiguration<DomainDirectory>
{
    public void Configure(EntityTypeBuilder<DomainDirectory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DomainDirectories");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.DomainName)
            .HasConversion(ValueObjectConverters.EmailDomainConverter, ValueObjectConverters.EmailDomainComparer)
            .HasMaxLength(253)
            .IsRequired();

        // O domínio identifica o diretório: dois diretórios para 'sintek.com.br'
        // tornariam ambíguo a qual deles uma conta pertence.
        builder.HasIndex(d => d.DomainName).IsUnique();

        builder.Property(d => d.Description).HasMaxLength(512);
        builder.Property(d => d.ValidationMode).HasConversion<int>();
        builder.Property(d => d.InvalidEmailAction).HasConversion<int>();

        builder.HasMany(d => d.Aliases)
            .WithOne(a => a.DomainDirectory)
            .HasForeignKey(a => a.DomainDirectoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(d => d.Accounts)
            .WithOne(a => a.DomainDirectory)
            .HasForeignKey(a => a.DomainDirectoryId)
            // Restrict, não Cascade: apagar um diretório não pode levar junto contas
            // inteiras — com todas as mensagens sincronizadas — por um clique. A remoção
            // de contas é uma decisão separada e confirmada.
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata.FindNavigation(nameof(DomainDirectory.Aliases))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(DomainDirectory.Accounts))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento dos domínios adicionais permitidos.</summary>
public sealed class DomainAliasConfiguration : IEntityTypeConfiguration<DomainAlias>
{
    public void Configure(EntityTypeBuilder<DomainAlias> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DomainAliases");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.DomainName)
            .HasConversion(ValueObjectConverters.EmailDomainConverter, ValueObjectConverters.EmailDomainComparer)
            .HasMaxLength(253)
            .IsRequired();

        builder.HasIndex(a => new { a.DomainDirectoryId, a.DomainName }).IsUnique();
    }
}

/// <summary>Mapeamento das contas de e-mail.</summary>
public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Accounts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EmailAddress)
            .HasConversion(ValueObjectConverters.EmailAddressConverter, ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320)
            .IsRequired();

        builder.HasIndex(a => a.EmailAddress).IsUnique();
        builder.HasIndex(a => a.DomainDirectoryId);

        builder.Property(a => a.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(a => a.ImapHost).HasMaxLength(253);
        builder.Property(a => a.SmtpHost).HasMaxLength(253);
        builder.Property(a => a.UserName).HasMaxLength(320);

        // Guarda apenas o identificador da credencial no Windows Credential Manager.
        // Nenhuma senha ou token chega a esta tabela.
        builder.Property(a => a.CredentialKey).HasMaxLength(512).IsRequired();

        builder.Property(a => a.AuthenticationType).HasConversion<int>();
        builder.Property(a => a.OAuthProvider).HasConversion<int>();
        builder.Property(a => a.ImapSecurity).HasConversion<int>();
        builder.Property(a => a.SmtpSecurity).HasConversion<int>();
        builder.Property(a => a.SyncStatus).HasConversion<int>();
        builder.Property(a => a.BodyDownloadPolicy).HasConversion<int>();
        builder.Property(a => a.LastSyncError).HasMaxLength(2048);

        builder.Property(a => a.CalendarProvider).HasConversion<int>();
        builder.Property(a => a.CalendarUrl).HasMaxLength(2048);

        builder.HasMany(a => a.Folders)
            .WithOne(f => f.Account)
            .HasForeignKey(f => f.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Account.Folders))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}
