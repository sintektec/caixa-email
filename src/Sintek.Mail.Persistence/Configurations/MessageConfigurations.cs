using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Persistence.Converters;

namespace Sintek.Mail.Persistence.Configurations;

/// <summary>Mapeamento das pastas.</summary>
public sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Folders");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name).HasMaxLength(256).IsRequired();
        builder.Property(f => f.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(f => f.RemotePath).HasMaxLength(1024);
        builder.Property(f => f.FolderType).HasConversion<int>();

        // Filtrado porque as pastas locais (Pendências, Caixa de Saída) têm caminho
        // vazio: sem o filtro, a segunda pasta local de cada conta violaria o índice.
        builder.HasIndex(f => new { f.AccountId, f.RemotePath })
            .IsUnique()
            .HasFilter("\"RemotePath\" <> ''");

        builder.HasIndex(f => f.ParentFolderId);
        builder.HasIndex(f => new { f.AccountId, f.FolderType });

        // Consultado a cada movimentação de mensagem para descobrir se a pasta é restrita.
        builder.HasIndex(f => f.EffectiveRestrictionDomainDirectoryId);

        builder.HasOne(f => f.ParentFolder)
            .WithMany(f => f.Children)
            .HasForeignKey(f => f.ParentFolderId)
            // Restrict: excluir uma pasta com subpastas precisa ser uma decisão explícita
            // do usuário, não um efeito colateral em cascata.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DomainDirectory>()
            .WithMany()
            .HasForeignKey(f => f.RestrictedToDomainDirectoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Ignore(f => f.IsDomainRestricted);
        builder.Ignore(f => f.IsRestrictionInherited);

        builder.Metadata.FindNavigation(nameof(Folder.Children))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento das mensagens.</summary>
public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.MessageId).HasMaxLength(998).IsRequired();
        builder.Property(m => m.InReplyTo).HasMaxLength(998);
        builder.Property(m => m.Subject).HasMaxLength(2048);
        builder.Property(m => m.SubjectNormalized).HasMaxLength(2048);
        builder.Property(m => m.Preview).HasMaxLength(512);
        builder.Property(m => m.FromDisplayName).HasMaxLength(256);
        builder.Property(m => m.Importance).HasConversion<int>();
        builder.Property(m => m.SyncState).HasConversion<int>();

        builder.Property(m => m.FromAddress)
            .HasConversion(
                ValueObjectConverters.NullableEmailAddressConverter,
                ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320);

        // Índice principal da listagem: a interface sempre pede "as mensagens desta
        // pasta, da mais recente para a mais antiga".
        builder.HasIndex(m => new { m.FolderId, m.ReceivedAt })
            .IsDescending(false, true);

        // Deduplicação durante a sincronização: o Message-ID identifica a mensagem
        // independentemente da pasta ou do UID.
        builder.HasIndex(m => new { m.AccountId, m.MessageId });

        builder.HasIndex(m => m.ThreadId);

        // A fila de saída procura exatamente por isto: o que ainda não foi propagado.
        builder.HasIndex(m => new { m.AccountId, m.SyncState });

        builder.HasIndex(m => new { m.AccountId, m.Uid });
        builder.HasIndex(m => m.ScheduledSendAt);

        builder.HasOne(m => m.Folder)
            .WithMany()
            .HasForeignKey(m => m.FolderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Account)
            .WithMany()
            .HasForeignKey(m => m.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Body)
            .WithOne(b => b.Message)
            .HasForeignKey<MessageBody>(b => b.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.Addresses)
            .WithOne(a => a.Message)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.Attachments)
            .WithOne(a => a.Message)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.Categories)
            .WithOne(c => c.Message)
            .HasForeignKey(c => c.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Message.Addresses))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Message.Attachments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Message.Categories))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento dos participantes das mensagens.</summary>
public sealed class MessageAddressConfiguration : IEntityTypeConfiguration<MessageAddress>
{
    public void Configure(EntityTypeBuilder<MessageAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MessageAddresses");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Kind).HasConversion<int>();
        builder.Property(a => a.DisplayName).HasMaxLength(256);

        builder.Property(a => a.Address)
            .HasConversion(ValueObjectConverters.EmailAddressConverter, ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(a => a.Domain)
            .HasConversion(ValueObjectConverters.EmailDomainConverter, ValueObjectConverters.EmailDomainComparer)
            .HasMaxLength(253)
            .IsRequired();

        builder.HasIndex(a => new { a.MessageId, a.Kind });

        // O índice que torna viável a regra de Diretório de Domínio: responder "quais
        // mensagens têm participante em sintek.com.br?" sem varrer a caixa inteira.
        builder.HasIndex(a => a.Domain);

        builder.HasIndex(a => a.Address);
    }
}

/// <summary>Mapeamento dos corpos de mensagem.</summary>
public sealed class MessageBodyConfiguration : IEntityTypeConfiguration<MessageBody>
{
    public void Configure(EntityTypeBuilder<MessageBody> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MessageBodies");
        builder.HasKey(b => b.Id);
        builder.HasIndex(b => b.MessageId).IsUnique();
    }
}

/// <summary>Mapeamento dos anexos.</summary>
public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.FileName).HasMaxLength(512).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(256).IsRequired();
        builder.Property(a => a.ContentId).HasMaxLength(512);
        builder.Property(a => a.PartSpecifier).HasMaxLength(64);

        // O conteúdo fica em arquivo no disco; a coluna guarda apenas o caminho.
        builder.Property(a => a.StoragePath).HasMaxLength(1024);

        builder.HasIndex(a => a.MessageId);
        builder.HasIndex(a => a.FileName);
    }
}

/// <summary>Mapeamento das conversas.</summary>
public sealed class MessageThreadConfiguration : IEntityTypeConfiguration<MessageThread>
{
    public void Configure(EntityTypeBuilder<MessageThread> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MessageThreads");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.SubjectNormalized).HasMaxLength(2048);
        builder.HasIndex(t => new { t.AccountId, t.SubjectNormalized });
        builder.HasIndex(t => t.LastMessageAt);
    }
}

/// <summary>Mapeamento das categorias.</summary>
public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(128).IsRequired();
        builder.Property(c => c.ColorHex).HasMaxLength(7).IsRequired();

        builder.HasIndex(c => new { c.AccountId, c.Name }).IsUnique();
    }
}

/// <summary>Mapeamento da associação entre mensagens e categorias.</summary>
public sealed class MessageCategoryConfiguration : IEntityTypeConfiguration<MessageCategory>
{
    public void Configure(EntityTypeBuilder<MessageCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MessageCategories");
        builder.HasKey(mc => new { mc.MessageId, mc.CategoryId });

        builder.HasOne(mc => mc.Category)
            .WithMany()
            .HasForeignKey(mc => mc.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(mc => mc.CategoryId);
    }
}
