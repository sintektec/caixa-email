using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Persistence.Configurations;

/// <summary>Mapeamento dos compromissos da agenda.</summary>
public sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CalendarEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Uid).HasMaxLength(512).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(512);
        builder.Property(e => e.Description).HasMaxLength(8192);
        builder.Property(e => e.Location).HasMaxLength(512);
        builder.Property(e => e.MeetingUrl).HasMaxLength(2048);
        builder.Property(e => e.TimeZoneId).HasMaxLength(128);
        builder.Property(e => e.RecurrenceRule).HasMaxLength(1024);
        builder.Property(e => e.OrganizerDisplayName).HasMaxLength(256);

        builder.Property(e => e.OrganizerAddress)
            .HasConversion(
                Converters.ValueObjectConverters.NullableEmailAddressConverter)
            .HasMaxLength(320);

        // O UID é a identidade do evento na norma: é por ele que a atualização enviada
        // pelo organizador encontra o compromisso que já está aqui. Único por conta, e não
        // global, porque duas contas podem legitimamente ter sido convidadas para a mesma
        // reunião.
        builder.HasIndex(e => new { e.AccountId, e.Uid }).IsUnique();

        // A grade sempre pergunta por uma janela de tempo.
        builder.HasIndex(e => new { e.AccountId, e.StartsAt });

        builder.Property(e => e.RemoteHref).HasMaxLength(2048);
        builder.Property(e => e.RemoteETag).HasMaxLength(512);
        builder.Property(e => e.SyncState).HasConversion<int>();

        // O href é a identidade de rede do recurso, independente do UID: servidores nomeiam
        // o recurso como querem, e nem a Google nem o iCloud usam o UID. Único por coleção,
        // porque a mesma reunião pode estar espelhada em dois calendários remotos.
        builder.HasIndex(e => new { e.RemoteCalendarId, e.RemoteHref }).IsUnique();

        // A varredura da fila de envio pergunta por estado dentro de um calendário.
        builder.HasIndex(e => new { e.RemoteCalendarId, e.SyncState });

        builder.HasOne<RemoteCalendar>()
            .WithMany()
            .HasForeignKey(e => e.RemoteCalendarId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(e => e.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        // A mensagem em que o convite chegou pode ser apagada pela limpeza de cache sem
        // que o compromisso vá junto: a agenda não depende da mensagem para existir.
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(e => e.SourceMessageId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(e => e.Attendees)
            .WithOne(a => a.CalendarEvent)
            .HasForeignKey(a => a.CalendarEventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(CalendarEvent.Attendees))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Mapeamento dos participantes de um compromisso.</summary>
public sealed class EventAttendeeConfiguration : IEntityTypeConfiguration<EventAttendee>
{
    public void Configure(EntityTypeBuilder<EventAttendee> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EventAttendees");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Address)
            .HasConversion(
                Converters.ValueObjectConverters.EmailAddressConverter,
                Converters.ValueObjectConverters.EmailAddressComparer)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(a => a.DisplayName).HasMaxLength(256);

        builder.HasIndex(a => new { a.CalendarEventId, a.Address }).IsUnique();
    }
}

/// <summary>Mapeamento das coleções de calendário espelhadas do servidor.</summary>
public sealed class RemoteCalendarConfiguration : IEntityTypeConfiguration<RemoteCalendar>
{
    public void Configure(EntityTypeBuilder<RemoteCalendar> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RemoteCalendars");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.CollectionUrl).HasMaxLength(2048).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Color).HasMaxLength(32);
        builder.Property(c => c.Provider).HasConversion<int>();
        builder.Property(c => c.LastSyncError).HasMaxLength(2048);

        // O token de sincronização é uma URI opaca do servidor — guardada como texto, nunca
        // interpretada. Alguns servidores emitem tokens longos, daí a folga.
        builder.Property(c => c.SyncToken).HasMaxLength(2048);
        builder.Property(c => c.CTag).HasMaxLength(512);

        builder.HasIndex(c => new { c.AccountId, c.CollectionUrl }).IsUnique();

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
