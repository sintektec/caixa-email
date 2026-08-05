using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>Um participante como veio declarado em um convite.</summary>
/// <param name="Address">Endereço.</param>
/// <param name="DisplayName">Nome exibido, quando declarado.</param>
/// <param name="Role">Papel na reunião.</param>
/// <param name="Response">Resposta declarada no convite.</param>
/// <remarks>
/// Projeção sem identidade, para que a camada de Aplicação possa entregar a lista lida do
/// documento sem construir entidades — a construção é do próprio evento.
/// </remarks>
public readonly record struct AttendeeSnapshot(
    EmailAddress Address, string? DisplayName, AttendeeRole Role, AttendeeResponse Response);

/// <summary>
/// Um compromisso da agenda, vindo de um convite recebido ou criado pelo usuário.
/// </summary>
/// <remarks>
/// <para>
/// O evento pertence a uma conta, e a conta a um Diretório de Domínio — a agenda é
/// segmentada por cliente pelo mesmo motivo que as pastas e os contatos são.
/// </para>
/// <para>
/// <b>Identidade é o <see cref="Uid"/> do iCalendar, não o identificador local.</b> É por
/// ele que uma atualização enviada pelo organizador encontra o evento que já está aqui; a
/// chave local existe só para o banco.
/// </para>
/// </remarks>
public sealed class CalendarEvent : Entity
{
    private readonly List<EventAttendee> _attendees = [];

    private CalendarEvent(
        Guid id, Guid accountId, string uid, DateTimeOffset startsAt, DateTimeOffset endsAt,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        Uid = uid;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    private CalendarEvent()
    {
    }

    /// <summary>Conta dona do evento.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Identificador do evento na norma — o <c>UID</c> da RFC 5545.</summary>
    public string Uid { get; private set; } = string.Empty;

    /// <summary>
    /// Versão do convite, conforme o <c>SEQUENCE</c>.
    /// </summary>
    /// <remarks>
    /// O organizador o incrementa a cada alteração. É a única defesa contra um convite
    /// antigo chegar atrasado e desfazer a atualização mais recente.
    /// </remarks>
    public int Sequence { get; private set; }

    /// <summary>Assunto do compromisso.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Descrição livre.</summary>
    public string? Description { get; private set; }

    /// <summary>Local declarado.</summary>
    public string? Location { get; private set; }

    /// <summary>
    /// Endereço de entrada da reunião on-line, quando houver.
    /// </summary>
    /// <remarks>
    /// Teams, Meet e Zoom colocam o link em propriedades próprias ou no corpo; a extração
    /// é da camada de infraestrutura. Aqui ele é só um texto que a interface abre.
    /// </remarks>
    public string? MeetingUrl { get; private set; }

    /// <summary>Início, já convertido para um instante absoluto.</summary>
    public DateTimeOffset StartsAt { get; private set; }

    /// <summary>Fim.</summary>
    public DateTimeOffset EndsAt { get; private set; }

    /// <summary>Se ocupa o dia inteiro.</summary>
    public bool IsAllDay { get; private set; }

    /// <summary>
    /// Fuso declarado no convite, guardado como veio.
    /// </summary>
    /// <remarks>
    /// Informativo: o instante já está resolvido em <see cref="StartsAt"/>. Serve para
    /// exibir "14h em São Paulo" a quem está em outro fuso, e para reemitir o convite sem
    /// mudar o fuso que o organizador escolheu.
    /// </remarks>
    public string? TimeZoneId { get; private set; }

    /// <summary>Situação.</summary>
    public CalendarEventStatus Status { get; private set; } = CalendarEventStatus.Confirmed;

    /// <summary>Endereço do organizador.</summary>
    public EmailAddress? OrganizerAddress { get; private set; }

    /// <summary>Nome exibido do organizador.</summary>
    public string? OrganizerDisplayName { get; private set; }

    /// <summary>
    /// Regra de recorrência, como texto <c>RRULE</c>.
    /// </summary>
    /// <remarks>
    /// Guardada crua em vez de decomposta em colunas: quem sabe expandir uma recorrência é
    /// a biblioteca de iCalendar, e uma decomposição própria só criaria uma segunda
    /// interpretação da norma para divergir da primeira.
    /// </remarks>
    public string? RecurrenceRule { get; private set; }

    /// <summary>Mensagem em que o convite chegou, quando veio por e-mail.</summary>
    public Guid? SourceMessageId { get; private set; }

    /// <summary>Se o usuário pediu lembrete.</summary>
    public bool HasReminder { get; private set; }

    /// <summary>Minutos de antecedência do lembrete.</summary>
    public int ReminderMinutesBefore { get; private set; }

    /// <summary>Calendário remoto que hospeda este compromisso, quando há um.</summary>
    public Guid? RemoteCalendarId { get; private set; }

    /// <summary>
    /// Endereço do recurso no servidor.
    /// </summary>
    /// <remarks>
    /// <b>Não tem relação com o <see cref="Uid"/>.</b> Que muitos servidores nomeiem o
    /// recurso como <c>{UID}.ics</c> é coincidência, não contrato: a Google usa
    /// identificadores próprios e o iCloud renomeia. São duas identidades independentes —
    /// o href é a de rede, o UID é a de calendário — e derivar uma da outra quebra na
    /// primeira sincronização com servidor de verdade.
    /// </remarks>
    public string? RemoteHref { get; private set; }

    /// <summary>
    /// ETag do recurso, como o servidor o emitiu — com aspas e tudo.
    /// </summary>
    /// <remarks>
    /// É o que vai em <c>If-Match</c> na escrita, e é o que detecta que outra pessoa
    /// escreveu antes. Guardado verbatim: <c>"2134-314"</c> e <c>2134-314</c> são valores
    /// diferentes para o servidor.
    /// </remarks>
    public string? RemoteETag { get; private set; }

    /// <summary>
    /// O documento iCalendar do servidor, guardado inteiro.
    /// </summary>
    /// <remarks>
    /// Preservado para que uma edição devolva ao servidor o que ele tinha, alterando apenas
    /// os campos tocados. Reserializar a partir do modelo de domínio destruiria em silêncio
    /// o que este produto não modela — <c>X-*</c> de outros clientes, parâmetros de
    /// participante, <c>VALARM</c> que a interface não exibe.
    /// </remarks>
    public string? RawICalendar { get; private set; }

    /// <summary>Estado perante o servidor.</summary>
    public CalendarSyncState SyncState { get; private set; } = CalendarSyncState.LocalOnly;

    /// <summary>Participantes.</summary>
    public IReadOnlyCollection<EventAttendee> Attendees => _attendees;

    /// <summary>Se o evento se repete.</summary>
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceRule);

    /// <summary>Cria um evento.</summary>
    public static CalendarEvent Create(
        Guid accountId,
        string uid,
        string summary,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset createdAt,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        if (endsAt < startsAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt), "O fim do compromisso não pode ser anterior ao início.");
        }

        return new CalendarEvent(id ?? Guid.CreateVersion7(), accountId, uid.Trim(), startsAt, endsAt, createdAt)
        {
            Summary = summary?.Trim() ?? string.Empty,
        };
    }

    /// <summary>
    /// Aplica uma atualização vinda de um convite.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> se a atualização foi aplicada; <see langword="false"/> se foi
    /// recusada por ser mais antiga que a versão já conhecida.
    /// </returns>
    /// <remarks>
    /// <b>Sequência menor nunca sobrescreve maior.</b> Convite antigo que chega atrasado —
    /// reencaminhado por alguém, ou retido por um servidor lento — desfaria a atualização
    /// mais recente e mudaria a reunião de volta para o horário errado. É o mesmo raciocínio
    /// de <see cref="Message.MarkPending"/>, que também recusa rebaixar um estado mais forte.
    /// </remarks>
    public bool ApplyUpdate(
        int sequence,
        string summary,
        string? description,
        string? location,
        string? meetingUrl,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        bool isAllDay,
        string? timeZoneId,
        CalendarEventStatus status,
        string? recurrenceRule,
        DateTimeOffset now)
    {
        if (sequence < Sequence)
        {
            return false;
        }

        Sequence = sequence;
        Summary = summary?.Trim() ?? string.Empty;
        Description = Normalize(description);
        Location = Normalize(location);
        MeetingUrl = Normalize(meetingUrl);
        StartsAt = startsAt;
        EndsAt = endsAt < startsAt ? startsAt : endsAt;
        IsAllDay = isAllDay;
        TimeZoneId = Normalize(timeZoneId);
        Status = status;
        RecurrenceRule = Normalize(recurrenceRule);
        Touch(now);

        return true;
    }

    /// <summary>Registra quem organiza.</summary>
    public void SetOrganizer(EmailAddress? address, string? displayName, DateTimeOffset now)
    {
        OrganizerAddress = address;
        OrganizerDisplayName = Normalize(displayName);
        Touch(now);
    }

    /// <summary>Vincula o evento à mensagem em que o convite chegou.</summary>
    public void LinkToMessage(Guid messageId, DateTimeOffset now)
    {
        SourceMessageId = messageId;
        Touch(now);
    }

    /// <summary>
    /// Cancela o evento.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> se o cancelamento é anterior à versão conhecida e foi
    /// recusado.
    /// </returns>
    /// <remarks>
    /// O evento é marcado, nunca apagado: quem já reservou o horário precisa ver que a
    /// reunião foi cancelada, e um sumiço silencioso da grade é indistinguível de um erro
    /// de sincronização.
    /// </remarks>
    public bool Cancel(int sequence, DateTimeOffset now)
    {
        if (sequence < Sequence)
        {
            return false;
        }

        Sequence = sequence;
        Status = CalendarEventStatus.Cancelled;
        Touch(now);

        return true;
    }

    /// <summary>
    /// Move o compromisso para outro horário, preservando a duração.
    /// </summary>
    /// <remarks>
    /// Quem decide <b>se</b> pode mover é o <see cref="Services.EventMoveEvaluator"/>; esta
    /// entidade só executa. O incremento de <see cref="Sequence"/> fica com quem organiza —
    /// mover a própria cópia de um compromisso sem participantes não é uma nova versão do
    /// convite de ninguém.
    /// </remarks>
    public void MoveTo(DateTimeOffset startsAt, DateTimeOffset now, bool incrementSequence)
    {
        var duration = EndsAt - StartsAt;

        StartsAt = startsAt;
        EndsAt = startsAt + duration;

        if (incrementSequence)
        {
            Sequence++;
        }

        MarkPending(CalendarSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Ajusta o horário de término.</summary>
    public void SetEnd(DateTimeOffset endsAt, DateTimeOffset now)
    {
        EndsAt = endsAt < StartsAt ? StartsAt : endsAt;
        MarkPending(CalendarSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Define os detalhes editados pelo usuário.</summary>
    public void SetDetails(
        string summary, string? description, string? location, string? meetingUrl, DateTimeOffset now)
    {
        Summary = summary?.Trim() ?? string.Empty;
        Description = Normalize(description);
        Location = Normalize(location);
        MeetingUrl = Normalize(meetingUrl);
        MarkPending(CalendarSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Define a regra de recorrência.</summary>
    public void SetRecurrence(string? recurrenceRule, DateTimeOffset now)
    {
        RecurrenceRule = Normalize(recurrenceRule);
        MarkPending(CalendarSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Marca como dia inteiro ou não.</summary>
    public void SetAllDay(bool isAllDay, DateTimeOffset now)
    {
        IsAllDay = isAllDay;
        MarkPending(CalendarSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Configura o lembrete local.</summary>
    public void SetReminder(bool enabled, int minutesBefore, DateTimeOffset now)
    {
        HasReminder = enabled;
        ReminderMinutesBefore = enabled ? Math.Max(minutesBefore, 0) : 0;
        Touch(now);
    }

    /// <summary>Acrescenta ou atualiza um participante.</summary>
    public EventAttendee AddAttendee(
        EmailAddress address,
        DateTimeOffset now,
        string? displayName = null,
        AttendeeRole role = AttendeeRole.Required,
        AttendeeResponse response = AttendeeResponse.NeedsAction)
    {
        ArgumentNullException.ThrowIfNull(address);

        var existing = _attendees.FirstOrDefault(a => a.Address == address);

        if (existing is not null)
        {
            existing.Update(displayName, role, response);
            Touch(now);
            return existing;
        }

        var attendee = EventAttendee.Create(Id, address, displayName, role, response, now);
        _attendees.Add(attendee);
        Touch(now);

        return attendee;
    }

    /// <summary>Remove um participante.</summary>
    public bool RemoveAttendee(EmailAddress address, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(address);

        var attendee = _attendees.FirstOrDefault(a => a.Address == address);

        if (attendee is null)
        {
            return false;
        }

        _attendees.Remove(attendee);
        Touch(now);

        return true;
    }

    /// <summary>
    /// Substitui a lista de participantes pela que veio no convite.
    /// </summary>
    /// <remarks>
    /// <b>A resposta já dada por quem está aqui é preservada.</b> O organizador reenvia o
    /// convite com <c>NEEDS-ACTION</c> para todo mundo a cada alteração, e aceitar essa
    /// lista cegamente apagaria o "aceito" que o usuário acabou de dar — ele veria o pedido
    /// de resposta reaparecer sozinho e não saberia por quê. Resposta explícita no convite
    /// prevalece, porque quem mantém o estado dos outros participantes é o organizador.
    /// </remarks>
    public void SyncAttendees(IReadOnlyCollection<AttendeeSnapshot> attendees, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attendees);

        if (attendees.Count == 0)
        {
            return;
        }

        var known = _attendees.ToDictionary(a => a.Address, a => a.Response);
        var replacement = new List<EventAttendee>(attendees.Count);

        foreach (var snapshot in attendees)
        {
            var response = snapshot.Response == AttendeeResponse.NeedsAction
                && known.TryGetValue(snapshot.Address, out var previous)
                    ? previous
                    : snapshot.Response;

            replacement.Add(EventAttendee.Create(
                Id, snapshot.Address, snapshot.DisplayName, snapshot.Role, response, now));
        }

        _attendees.Clear();
        _attendees.AddRange(replacement);
        Touch(now);
    }

    /// <summary>
    /// Registra a resposta de um participante.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> quando o endereço não é participante deste evento — o caso
    /// de um <c>REPLY</c> de quem foi retirado da reunião.
    /// </returns>
    public bool SetAttendeeResponse(EmailAddress address, AttendeeResponse response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(address);

        var attendee = _attendees.FirstOrDefault(a => a.Address == address);

        if (attendee is null)
        {
            return false;
        }

        attendee.Respond(response);
        Touch(now);

        return true;
    }

    /// <summary>O participante correspondente a um endereço, se houver.</summary>
    public EventAttendee? AttendeeFor(EmailAddress address)
        => address is null ? null : _attendees.FirstOrDefault(a => a.Address == address);

    /// <summary>Se o endereço informado organiza este evento.</summary>
    public bool IsOrganizedBy(EmailAddress address)
        => OrganizerAddress is not null && address is not null && OrganizerAddress == address;

    /// <summary>
    /// Quantos participantes existem além do endereço informado.
    /// </summary>
    /// <remarks>
    /// É o que distingue um compromisso próprio de uma reunião: mover o primeiro não afeta
    /// ninguém, mover o segundo dessincroniza o usuário de quem combinou o horário.
    /// </remarks>
    public int OtherAttendeeCount(EmailAddress address)
        => _attendees.Count(a => address is null || a.Address != address);

    /// <summary>Vincula o compromisso a um calendário remoto.</summary>
    public void BindToRemoteCalendar(Guid remoteCalendarId, DateTimeOffset now)
    {
        RemoteCalendarId = remoteCalendarId;

        if (SyncState == CalendarSyncState.LocalOnly)
        {
            SyncState = CalendarSyncState.PendingCreate;
        }

        Touch(now);
    }

    /// <summary>
    /// Registra que o compromisso está idêntico ao servidor.
    /// </summary>
    /// <remarks>
    /// O <c>href</c>, o <c>ETag</c> e o documento cru vêm juntos porque só fazem sentido
    /// juntos: guardar um ETag sem o conteúdo que ele descreve faz a escrita seguinte
    /// devolver 412 para sempre.
    /// </remarks>
    public void MarkRemoteSynced(
        string remoteHref, string? remoteETag, string? rawICalendar, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteHref);

        RemoteHref = remoteHref.Trim();
        RemoteETag = Normalize(remoteETag);
        RawICalendar = string.IsNullOrWhiteSpace(rawICalendar) ? RawICalendar : rawICalendar;
        SyncState = CalendarSyncState.Synced;
        Touch(now);
    }

    /// <summary>
    /// Marca o compromisso como pendente de envio ao servidor.
    /// </summary>
    /// <remarks>
    /// <b>Nunca rebaixa um estado mais forte.</b> Um compromisso pendente de exclusão que
    /// tem o assunto alterado continua pendente de exclusão; um pendente de criação que é
    /// editado continua pendente de criação, porque ainda não existe lá para ser atualizado.
    /// Reverter isso faz a fila enviar a alteração e esquecer a exclusão, e o compromisso
    /// reaparece na sincronização seguinte. É a mesma regra de
    /// <see cref="Message.MarkPending"/>.
    /// </remarks>
    private void MarkPending(CalendarSyncState state)
    {
        SyncState = SyncState switch
        {
            CalendarSyncState.PendingDelete => CalendarSyncState.PendingDelete,
            CalendarSyncState.PendingCreate => CalendarSyncState.PendingCreate,
            CalendarSyncState.LocalOnly => CalendarSyncState.LocalOnly,
            CalendarSyncState.Conflict => CalendarSyncState.Conflict,
            _ => state,
        };
    }

    /// <summary>Marca que a exclusão precisa chegar ao servidor.</summary>
    public void MarkPendingDelete(DateTimeOffset now)
    {
        SyncState = CalendarSyncState.PendingDelete;
        Touch(now);
    }

    /// <summary>
    /// Marca que os dois lados mudaram desde a última sincronização.
    /// </summary>
    /// <remarks>
    /// O conflito fica visível e espera decisão, em vez de ser resolvido em silêncio. É o
    /// caso em que qualquer escolha automática perde trabalho de alguém.
    /// </remarks>
    public void MarkConflicted(DateTimeOffset now)
    {
        SyncState = CalendarSyncState.Conflict;
        Touch(now);
    }

    /// <summary>Resolve o conflito mantendo a versão local, que volta à fila de envio.</summary>
    public void ResolveConflictKeepingLocal(DateTimeOffset now)
    {
        SyncState = RemoteHref is null
            ? CalendarSyncState.PendingCreate
            : CalendarSyncState.PendingUpdate;

        Touch(now);
    }

    /// <summary>
    /// Resolve o conflito aceitando a versão do servidor.
    /// </summary>
    /// <remarks>
    /// O <c>ETag</c> conhecido é descartado de propósito. Ele é o que o avaliador compara
    /// para decidir se o servidor mudou; mantê-lo faria a passada seguinte concluir que os
    /// dois lados estão iguais e deixaria a cópia local — a que o usuário acabou de
    /// descartar — como a versão final.
    /// </remarks>
    public void ResolveConflictAcceptingRemote(DateTimeOffset now)
    {
        RemoteETag = null;
        SyncState = CalendarSyncState.Synced;
        Touch(now);
    }

    /// <summary>Guarda o documento iCalendar do servidor sem alterar o estado.</summary>
    public void SetRawICalendar(string? rawICalendar, DateTimeOffset now)
    {
        RawICalendar = string.IsNullOrWhiteSpace(rawICalendar) ? null : rawICalendar;
        Touch(now);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Um participante de um evento.</summary>
public sealed class EventAttendee : Entity
{
    private EventAttendee(Guid id, Guid calendarEventId, EmailAddress address, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        CalendarEventId = calendarEventId;
        Address = address;
    }

    private EventAttendee()
    {
    }

    /// <summary>Evento a que pertence.</summary>
    public Guid CalendarEventId { get; private set; }

    /// <summary>Evento a que pertence.</summary>
    public CalendarEvent? CalendarEvent { get; private set; }

    /// <summary>Endereço do participante.</summary>
    public EmailAddress Address { get; private set; } = null!;

    /// <summary>Nome exibido.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Papel na reunião.</summary>
    public AttendeeRole Role { get; private set; } = AttendeeRole.Required;

    /// <summary>Resposta declarada.</summary>
    public AttendeeResponse Response { get; private set; } = AttendeeResponse.NeedsAction;

    /// <summary>Texto exibido: nome e endereço, ou só o endereço.</summary>
    public string DisplayText => DisplayName is null
        ? Address.Value
        : $"{DisplayName} <{Address.Value}>";

    internal static EventAttendee Create(
        Guid calendarEventId,
        EmailAddress address,
        string? displayName,
        AttendeeRole role,
        AttendeeResponse response,
        DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), calendarEventId, address, createdAt)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            Role = role,
            Response = response,
        };

    /// <summary>
    /// Atualiza os dados vindos de um convite novo.
    /// </summary>
    /// <remarks>
    /// Nome vazio não apaga o que já se sabia, pelo mesmo motivo do histórico de
    /// destinatários: o convite atualizado costuma vir só com o endereço.
    /// </remarks>
    internal void Update(string? displayName, AttendeeRole role, AttendeeResponse response)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
        }

        Role = role;
        Response = response;
    }

    internal void Respond(AttendeeResponse response) => Response = response;
}
