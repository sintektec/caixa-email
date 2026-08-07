using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Calendar;

/// <summary>O que a importação de um convite produziu.</summary>
public enum InvitationImportOutcome
{
    /// <summary>O documento não é iCalendar interpretável.</summary>
    NotCalendar = 0,

    /// <summary>Evento novo criado.</summary>
    Created = 1,

    /// <summary>Evento existente atualizado.</summary>
    Updated = 2,

    /// <summary>Evento cancelado.</summary>
    Cancelled = 3,

    /// <summary>Resposta de participante registrada.</summary>
    ResponseRecorded = 4,

    /// <summary>Descartado por ser mais antigo que a versão conhecida.</summary>
    DiscardedAsStale = 5,

    /// <summary>Recusado pela regra do Diretório de Domínio.</summary>
    BlockedByDomainRule = 6,

    /// <summary>Nada a fazer — método não tratado, ou evento desconhecido.</summary>
    Ignored = 7,
}

/// <summary>Resultado da importação.</summary>
/// <param name="Outcome">O que aconteceu.</param>
/// <param name="EventId">Evento afetado, quando houver.</param>
/// <param name="Message">Motivo exibível, vazio quando não há o que explicar.</param>
public readonly record struct InvitationImportResult(
    InvitationImportOutcome Outcome, Guid? EventId, string Message);

/// <summary>
/// Traz para a agenda os convites que chegam por e-mail.
/// </summary>
/// <remarks>
/// <para>
/// Teams, Google Meet e Outlook enviam o mesmo <c>text/calendar</c> da RFC 5545 — este
/// handler cobre os três, e cobre também Zoom, Webex e qualquer outro que respeite a norma.
/// </para>
/// <para>
/// <b>Sequência menor nunca sobrescreve maior.</b> Convite antigo que chega atrasado —
/// reencaminhado por alguém, ou retido por um servidor lento — desfaria a atualização mais
/// recente e mudaria a reunião de volta para o horário errado. A recusa é registrada em
/// auditoria, e não em silêncio: um convite que some sem explicação parece defeito.
/// </para>
/// </remarks>
public sealed class ImportInvitationHandler
{
    private readonly ICalendarRepository _calendar;
    private readonly IAccountRepository _accounts;
    private readonly IDomainDirectoryRepository _directories;
    private readonly ICalendarSerializer _serializer;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportInvitationHandler> _logger;

    public ImportInvitationHandler(
        ICalendarRepository calendar,
        IAccountRepository accounts,
        IDomainDirectoryRepository directories,
        ICalendarSerializer serializer,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ImportInvitationHandler> logger)
    {
        _calendar = calendar;
        _accounts = accounts;
        _directories = directories;
        _serializer = serializer;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Importa o conteúdo de uma parte <c>text/calendar</c>.
    /// </summary>
    /// <param name="accountId">Conta que recebeu.</param>
    /// <param name="content">Conteúdo do documento iCalendar.</param>
    /// <param name="sourceMessageId">Mensagem em que o convite chegou.</param>
    public async Task<InvitationImportResult> ImportAsync(
        Guid accountId,
        string content,
        Guid? sourceMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (_serializer.Read(content) is not { } document || document.Events.Count == 0)
        {
            return new InvitationImportResult(InvitationImportOutcome.NotCalendar, null, string.Empty);
        }

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return new InvitationImportResult(
                InvitationImportOutcome.Ignored, null, "A conta informada não existe.");
        }

        var directory = await _directories
            .GetByIdAsync(account.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        InvitationImportResult result = default;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Um documento pode trazer mais de um evento; o resultado devolvido é o do
            // último, que é o que a interface exibe. Todos são processados.
            foreach (var data in document.Events)
            {
                result = await ApplyAsync(
                    account, directory, document.Method, data, sourceMessageId, ct)
                    .ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task<InvitationImportResult> ApplyAsync(
        Account account,
        DomainDirectory? directory,
        CalendarMethod method,
        CalendarEventData data,
        Guid? sourceMessageId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var existing = await _calendar
            .GetByUidAsync(account.Id, data.Uid, cancellationToken)
            .ConfigureAwait(false);

        // Segunda via de identidade: a biblioteca gera um UID aleatório quando o documento
        // não traz um, e sem isto rebaixar o corpo da mensagem criaria um compromisso novo
        // a cada vez.
        if (existing is null && sourceMessageId is { } messageId)
        {
            existing = await _calendar
                .GetBySourceMessageAsync(messageId, cancellationToken)
                .ConfigureAwait(false);
        }

        switch (method)
        {
            case CalendarMethod.Cancel:
                return existing is null
                    ? new InvitationImportResult(
                        InvitationImportOutcome.Ignored, null,
                        "O cancelamento é de um compromisso que não está na agenda.")
                    : Cancel(existing, data, now);

            case CalendarMethod.Reply:
                return existing is null
                    ? new InvitationImportResult(
                        InvitationImportOutcome.Ignored, null,
                        "A resposta é de um compromisso que não está na agenda.")
                    : RecordResponse(existing, data, now);

            case CalendarMethod.Request:
            case CalendarMethod.Publish:
                return await CreateOrUpdateAsync(
                    account, directory, data, existing, sourceMessageId, now, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return new InvitationImportResult(
                    InvitationImportOutcome.Ignored, existing?.Id,
                    "O convite usa um método que este produto não trata.");
        }
    }

    private async Task<InvitationImportResult> CreateOrUpdateAsync(
        Account account,
        DomainDirectory? directory,
        CalendarEventData data,
        CalendarEvent? existing,
        Guid? sourceMessageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (data.StartsAt is not { } startsAt)
        {
            return new InvitationImportResult(
                InvitationImportOutcome.Ignored, existing?.Id,
                "O convite não traz horário de início.");
        }

        var membership = EvaluateDomainRule(account, directory, data);

        if (!membership.IsMember && directory?.InvalidEmailAction == InvalidEmailAction.Block)
        {
            await RecordBlockAsync(account, directory, data, cancellationToken).ConfigureAwait(false);

            return new InvitationImportResult(
                InvitationImportOutcome.BlockedByDomainRule, existing?.Id,
                "O convite foi recusado: nenhum participante pertence ao Diretório de Domínio "
                + "desta conta.");
        }

        // Nos modos que não bloqueiam, o convite entra e o desvio fica registrado — é o que
        // permite ao usuário revisar depois sem perder o compromisso agora.
        if (!membership.IsMember && directory is not null)
        {
            await RecordBlockAsync(account, directory, data, cancellationToken).ConfigureAwait(false);
        }

        var endsAt = data.EndsAt ?? startsAt;
        var target = existing;

        if (target is null)
        {
            target = CalendarEvent.Create(account.Id, data.Uid, data.Summary, startsAt, endsAt, now);
            await _calendar.AddAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var applied = target.ApplyUpdate(
            data.Sequence,
            data.Summary,
            data.Description,
            data.Location,
            data.MeetingUrl,
            startsAt,
            endsAt,
            data.IsAllDay,
            data.TimeZoneId,
            data.Status,
            data.RecurrenceRule,
            now);

        if (!applied)
        {
            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.InvitationOutOfOrderDiscarded,
                    "Convite descartado por ser anterior à versão já conhecida.",
                    now,
                    AuditSeverity.Information,
                    entityType: nameof(CalendarEvent),
                    entityId: target.Id,
                    accountId: account.Id),
                cancellationToken).ConfigureAwait(false);

            return new InvitationImportResult(
                InvitationImportOutcome.DiscardedAsStale, target.Id,
                "Chegou uma versão mais antiga deste convite; a agenda manteve a mais recente.");
        }

        target.SetOrganizer(data.OrganizerAddress, data.OrganizerDisplayName, now);

        if (sourceMessageId is { } messageId)
        {
            target.LinkToMessage(messageId, now);
        }

        SyncAttendees(target, data, now);

        _logger.LogInformation(
            "Convite {Uid} aplicado à agenda da conta {AccountId}.", data.Uid, account.Id);

        return new InvitationImportResult(
            existing is null ? InvitationImportOutcome.Created : InvitationImportOutcome.Updated,
            target.Id,
            membership.IsMember
                ? string.Empty
                : "O convite não satisfaz a regra do Diretório de Domínio desta conta e foi registrado.");
    }

    private static InvitationImportResult Cancel(
        CalendarEvent target, CalendarEventData data, DateTimeOffset now)
        => target.Cancel(data.Sequence, now)
            ? new InvitationImportResult(InvitationImportOutcome.Cancelled, target.Id, string.Empty)
            : new InvitationImportResult(
                InvitationImportOutcome.DiscardedAsStale, target.Id,
                "Chegou um cancelamento anterior à versão já conhecida; a agenda o ignorou.");

    /// <summary>
    /// Registra a resposta que um participante mandou de volta.
    /// </summary>
    /// <remarks>
    /// Um <c>REPLY</c> não altera horário nem assunto, só o <c>PARTSTAT</c> de quem
    /// respondeu. Deixá-lo passar pelo caminho de atualização faria a resposta de um
    /// participante reescrever o evento inteiro com o que o cliente dele achou de enviar.
    /// </remarks>
    private static InvitationImportResult RecordResponse(
        CalendarEvent target, CalendarEventData data, DateTimeOffset now)
    {
        var recorded = 0;

        foreach (var attendee in data.Attendees)
        {
            if (target.SetAttendeeResponse(attendee.Address, attendee.Response, now))
            {
                recorded++;
            }
        }

        return recorded > 0
            ? new InvitationImportResult(
                InvitationImportOutcome.ResponseRecorded, target.Id, string.Empty)
            : new InvitationImportResult(
                InvitationImportOutcome.Ignored, target.Id,
                "A resposta é de quem não está mais na lista de participantes.");
    }

    /// <summary>Entrega ao evento a lista de participantes lida do convite.</summary>
    /// <remarks>
    /// A regra de preservar a resposta já dada vive na entidade — ver
    /// <see cref="CalendarEvent.SyncAttendees"/>.
    /// </remarks>
    private static void SyncAttendees(CalendarEvent target, CalendarEventData data, DateTimeOffset now)
        => target.SyncAttendees(
            [.. data.Attendees.Select(a => new AttendeeSnapshot(
                a.Address, a.DisplayName, a.Role, a.Response))],
            now);

    /// <summary>
    /// Aplica ao convite a mesma regra de Diretório de Domínio que vale para as mensagens.
    /// </summary>
    /// <remarks>
    /// Organizador e participantes entram como remetente e destinatários. Sem isto a agenda
    /// seria um produto genérico grudado ao lado do cliente de e-mail, em vez de parte dele.
    /// </remarks>
    private static DomainMembershipResult EvaluateDomainRule(
        Account account, DomainDirectory? directory, CalendarEventData data)
    {
        if (directory is null)
        {
            return new DomainMembershipResult(true, DomainMembershipReason.ParticipantMatched);
        }

        var participants = new List<MessageParticipant>();

        if (data.OrganizerAddress is { } organizer)
        {
            participants.Add(new MessageParticipant(AddressKind.From, organizer.Domain));
        }

        foreach (var attendee in data.Attendees)
        {
            participants.Add(new MessageParticipant(AddressKind.To, attendee.Domain()));
        }

        // Convite sem participante nenhum é compromisso próprio: a conta é o participante.
        if (participants.Count == 0)
        {
            participants.Add(new MessageParticipant(AddressKind.To, account.EmailAddress.Domain));
        }

        return DomainMembershipEvaluator.Evaluate(directory, participants);
    }

    private async Task RecordBlockAsync(
        Account account, DomainDirectory directory, CalendarEventData data,
        CancellationToken cancellationToken)
        => await _audit.RecordAsync(
            AuditLogEntry.Record(
                AuditEventType.InvitationBlockedByDomainRule,
                // Assunto e participantes ficam de fora: a auditoria não registra conteúdo.
                $"Convite não satisfaz a regra do diretório (ação configurada: {directory.InvalidEmailAction}).",
                _timeProvider.GetUtcNow(),
                AuditSeverity.Warning,
                entityType: nameof(CalendarEvent),
                accountId: account.Id,
                domainDirectoryId: directory.Id),
            cancellationToken).ConfigureAwait(false);
}

/// <summary>Atalhos usados pela avaliação de domínio dos convites.</summary>
internal static class CalendarAttendeeExtensions
{
    /// <summary>Domínio do participante.</summary>
    public static EmailDomain Domain(this CalendarAttendeeData attendee) => attendee.Address.Domain;
}
