using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Calendar;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Calendar;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.Sync;

/// <summary>Resultado da sincronização de agenda de uma conta.</summary>
/// <param name="CalendarsMirrored">Calendários remotos conhecidos ao final.</param>
/// <param name="Added">Compromissos trazidos do servidor.</param>
/// <param name="Updated">Compromissos atualizados a partir do servidor.</param>
/// <param name="RemovedLocally">Compromissos apagados aqui porque sumiram de lá.</param>
/// <param name="Pushed">Alterações locais aplicadas no servidor.</param>
/// <param name="Conflicted">Compromissos que ficaram aguardando decisão.</param>
public readonly record struct CalendarSyncResult(
    int CalendarsMirrored, int Added, int Updated, int RemovedLocally, int Pushed, int Conflicted)
{
    /// <summary>Se algo mudou de algum lado.</summary>
    public bool HasChanges => Added + Updated + RemovedLocally + Pushed + Conflicted > 0;
}

/// <summary>
/// Sincroniza a agenda local com o servidor, nos dois sentidos.
/// </summary>
/// <remarks>
/// <para>
/// <b>O envio vem antes da leitura</b>, pelo mesmo motivo que a fila de saída drena antes de
/// ler o IMAP: enquanto o local não subiu, o servidor ainda não sabe do que o usuário fez
/// offline. Ler primeiro traria o estado antigo e desfaria a edição dele — o compromisso
/// voltaria para o horário anterior, e o envio seguinte o moveria de novo. Um pisca-pisca
/// que parece defeito e é.
/// </para>
/// <para>
/// <b>Conflito não é resolvido em silêncio.</b> Quando os dois lados mudaram, o compromisso
/// é marcado e fica esperando decisão. Qualquer escolha automática descarta o trabalho de
/// alguém, e a pessoa só descobre quando procura o que escreveu e não acha. Quem decide é o
/// <see cref="CalendarConflictEvaluator"/>, no domínio.
/// </para>
/// <para>
/// <b>Falha de um calendário não derruba os outros.</b> Uma coleção com permissão revogada,
/// ou um servidor que devolve erro só nela, registra o motivo e a passada segue. Abortar o
/// ciclo inteiro faria uma coleção quebrada esconder a atualização de todas as demais.
/// </para>
/// </remarks>
public sealed class CalendarSyncService
{
    /// <summary>
    /// Quantas passadas de paginação são aceitas antes de desistir.
    /// </summary>
    /// <remarks>
    /// O servidor pagina truncando o lote e pedindo que o cliente repita com o token novo.
    /// Um servidor com defeito pode devolver "há mais" para sempre; sem teto, o laço nunca
    /// termina. O limite é generoso — vinte lotes cobrem qualquer coleção real.
    /// </remarks>
    private const int MaxSyncRounds = 20;

    private readonly IRemoteCalendarRepository _calendars;
    private readonly ICalendarRepository _events;
    private readonly ICalendarSerializer _serializer;
    private readonly IEnumerable<ICalendarSyncProvider> _providers;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        IRemoteCalendarRepository calendars,
        ICalendarRepository events,
        ICalendarSerializer serializer,
        IEnumerable<ICalendarSyncProvider> providers,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CalendarSyncService> logger)
    {
        _calendars = calendars;
        _events = events;
        _serializer = serializer;
        _providers = providers;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Executa um ciclo para a conta, escolhendo o provedor pelo protocolo configurado.
    /// </summary>
    /// <remarks>
    /// Conta sem servidor de agenda, com a sincronização desligada ou com um protocolo sem
    /// implementação registrada devolve resultado vazio em vez de erro: a agenda local
    /// funciona por inteiro sem servidor, e o ciclo de e-mail não pode parar por causa disso.
    /// </remarks>
    public async Task<CalendarSyncResult> SyncAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!account.CalendarSyncEnabled || account.CalendarProvider == CalendarProviderKind.None)
        {
            return default;
        }

        var provider = _providers.FirstOrDefault(p => p.Provider == account.CalendarProvider);

        if (provider is null)
        {
            _logger.LogWarning(
                "A conta {AccountId} está configurada para {Protocolo}, que ainda não tem "
                + "implementação registrada.", account.Id, account.CalendarProvider);

            return default;
        }

        return await SyncAsync(account, provider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executa um ciclo completo para a conta com o provedor informado.</summary>
    public async Task<CalendarSyncResult> SyncAsync(
        Account account,
        ICalendarSyncProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(provider);

        var calendars = await MirrorCalendarsAsync(account, provider, cancellationToken)
            .ConfigureAwait(false);

        var added = 0;
        var updated = 0;
        var removed = 0;
        var pushed = 0;
        var conflicted = 0;

        foreach (var calendar in calendars.Where(c => c.SyncEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Enviar antes de ler: ver o comentário da classe.
                pushed += await PushAsync(account, provider, calendar, cancellationToken)
                    .ConfigureAwait(false);

                var pulled = await PullAsync(account, provider, calendar, cancellationToken)
                    .ConfigureAwait(false);

                added += pulled.Added;
                updated += pulled.Updated;
                removed += pulled.RemovedLocally;
                conflicted += pulled.Conflicted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // O motivo entra em LastSyncError, que a interface exibe: nunca credencial,
                // nunca conteúdo de compromisso — daí só ex.Message.
                calendar.MarkSyncFailed(ex.Message, _timeProvider.GetUtcNow());
                await _unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

                _logger.LogWarning(
                    ex, "A sincronização do calendário {CalendarId} falhou.", calendar.Id);
            }
        }

        return new CalendarSyncResult(calendars.Count, added, updated, removed, pushed, conflicted);
    }

    /// <summary>
    /// Espelha a lista de calendários do servidor.
    /// </summary>
    /// <remarks>
    /// <b>Coleção que some da listagem não é apagada.</b> Desliga a sincronização e preserva
    /// o conteúdo, pelo mesmo motivo do <see cref="FolderMirrorService"/>: uma resposta
    /// incompleta do servidor é indistinguível de uma exclusão real, e o custo dos dois
    /// erros não é simétrico. Perder a agenda de um cliente por causa de um 500 momentâneo é
    /// o erro caro.
    /// </remarks>
    private async Task<IReadOnlyList<RemoteCalendar>> MirrorCalendarsAsync(
        Account account, ICalendarSyncProvider provider, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var remote = await provider.DiscoverAsync(account, cancellationToken).ConfigureAwait(false);
        var local = await _calendars.ListByAccountAsync(account.Id, cancellationToken)
            .ConfigureAwait(false);

        if (remote.Count == 0)
        {
            // Descoberta vazia é o caso normal de conta sem servidor de agenda, e também o
            // de um servidor fora do ar. Nos dois, não mexer é o certo.
            return local;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in remote)
        {
            seen.Add(descriptor.CollectionUrl);

            var existing = local.FirstOrDefault(
                c => string.Equals(c.CollectionUrl, descriptor.CollectionUrl, StringComparison.Ordinal));

            if (existing is null)
            {
                existing = RemoteCalendar.Create(
                    account.Id, provider.Provider, descriptor.CollectionUrl,
                    descriptor.DisplayName, now);

                await _calendars.AddAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            existing.Describe(descriptor.DisplayName, descriptor.Color, descriptor.IsReadOnly, now);
            existing.SetSyncEnabled(true, now);
        }

        foreach (var missing in local.Where(c => !seen.Contains(c.CollectionUrl)))
        {
            missing.SetSyncEnabled(false, now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await _calendars.ListByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Aplica no servidor o que mudou aqui.</summary>
    private async Task<int> PushAsync(
        Account account,
        ICalendarSyncProvider provider,
        RemoteCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (calendar.IsReadOnly)
        {
            // Calendário compartilhado só para leitura devolveria 403 a cada tentativa, e a
            // fila retentaria para sempre uma operação que nunca vai passar.
            return 0;
        }

        var pending = await _events.ListPendingAsync(calendar.Id, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var pushed = 0;

        foreach (var target in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = target.SyncState switch
            {
                CalendarSyncState.PendingDelete when target.RemoteHref is { } href
                    => await provider.DeleteAsync(
                        account, calendar, href, target.RemoteETag, cancellationToken)
                        .ConfigureAwait(false),

                CalendarSyncState.PendingUpdate when target.RemoteHref is { } href
                    => await provider.UpdateAsync(
                        account, calendar, href, target.RemoteETag, Serialize(target), cancellationToken)
                        .ConfigureAwait(false),

                _ => await provider.CreateAsync(
                    account, calendar, Serialize(target), cancellationToken).ConfigureAwait(false),
            };

            if (result.IsConflict)
            {
                // O servidor mudou desde o ETag conhecido. Sobrescrever descartaria a
                // alteração da outra pessoa; a decisão fica com o usuário.
                target.MarkConflicted(now);
                continue;
            }

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Envio do compromisso {EventId} recusado: {Motivo}", target.Id, result.ErrorMessage);
                continue;
            }

            if (target.SyncState == CalendarSyncState.PendingDelete)
            {
                _events.Remove(target);
            }
            else if (result.Href is { } finalHref)
            {
                // O documento vem junto quando o provedor precisou relê-lo: o servidor
                // reescreve o que recebe, e o que ficou lá não é o que subiu daqui.
                target.MarkRemoteSynced(finalHref, result.ETag, result.ICalendar, now);
            }

            pushed++;
        }

        if (pushed > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return pushed;
    }

    /// <summary>Traz do servidor o que mudou lá.</summary>
    private async Task<CalendarSyncResult> PullAsync(
        Account account,
        ICalendarSyncProvider provider,
        RemoteCalendar calendar,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var removed = 0;
        var conflicted = 0;

        for (var round = 0; round < MaxSyncRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await provider.FetchChangesAsync(account, calendar, cancellationToken)
                .ConfigureAwait(false);

            var applied = await ApplyChangesAsync(
                account, provider, calendar, changes, cancellationToken).ConfigureAwait(false);

            added += applied.Added;
            updated += applied.Updated;
            removed += applied.RemovedLocally;
            conflicted += applied.Conflicted;

            calendar.MarkSynced(changes.SyncToken, changes.CTag, _timeProvider.GetUtcNow());
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (!changes.HasMore)
            {
                break;
            }

            if (round == MaxSyncRounds - 1)
            {
                _logger.LogWarning(
                    "O calendário {CalendarId} ainda tinha alterações após {Rounds} lotes; "
                    + "o restante virá no próximo ciclo.",
                    calendar.Id, MaxSyncRounds);
            }
        }

        return new CalendarSyncResult(0, added, updated, removed, 0, conflicted);
    }

    private async Task<CalendarSyncResult> ApplyChangesAsync(
        Account account,
        ICalendarSyncProvider provider,
        RemoteCalendar calendar,
        RemoteCalendarChanges changes,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var added = 0;
        var updated = 0;
        var removed = 0;
        var conflicted = 0;

        var seenHrefs = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(RemoteCalendarChange Change, CalendarEvent? Local, CalendarSyncDecision Decision)>(
            changes.Changes.Count);

        // Primeiro decidir, depois buscar conteúdo. A ordem importa: o caminho do CTag lista
        // a coleção inteira a cada alteração, e buscar o documento de tudo o que apareceu
        // baixaria milhares de recursos para aplicar dois. Só o que a decisão vai usar é
        // baixado.
        foreach (var change in changes.Changes)
        {
            seenHrefs.Add(change.Href);

            var local = await _events
                .GetByRemoteHrefAsync(calendar.Id, change.Href, cancellationToken)
                .ConfigureAwait(false);

            var decision = CalendarConflictEvaluator.Evaluate(new CalendarSyncFacts(
                local?.SyncState ?? CalendarSyncState.Synced,
                local?.RemoteETag,
                change.ETag,
                change.Change,
                local is not null));

            pending.Add((change, local, decision));
        }

        // O documento vem num pedido só, em lote: um por recurso multiplicaria as viagens.
        var missingContent = pending
            .Where(p => p.Decision == CalendarSyncDecision.ApplyRemote
                && string.IsNullOrWhiteSpace(p.Change.ICalendar))
            .Select(p => p.Change.Href)
            .ToList();

        var fetched = missingContent.Count > 0
            ? (await provider.FetchResourcesAsync(account, calendar, missingContent, cancellationToken)
                .ConfigureAwait(false))
                .GroupBy(c => c.Href, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().ICalendar, StringComparer.Ordinal)
            : [];

        foreach (var (change, local, decision) in pending)
        {
            switch (decision)
            {
                case CalendarSyncDecision.ApplyRemote:
                    var document = string.IsNullOrWhiteSpace(change.ICalendar)
                        && fetched.TryGetValue(change.Href, out var extra)
                            ? extra
                            : change.ICalendar;

                    var outcomeOrNull = await ApplyRemoteAsync(
                        calendar, local, change, document, now, cancellationToken).ConfigureAwait(false);

                    if (outcomeOrNull is { } outcome)
                    {
                        if (outcome)
                        {
                            added++;
                        }
                        else
                        {
                            updated++;
                        }
                    }

                    break;

                case CalendarSyncDecision.DeleteLocal when local is not null:
                    _events.Remove(local);
                    removed++;
                    break;

                case CalendarSyncDecision.Conflict when local is not null:
                    local.MarkConflicted(now);
                    conflicted++;
                    break;

                default:
                    break;
            }
        }

        // Passada completa: o que existe aqui e não apareceu na listagem foi removido lá.
        // Só vale quando o provedor declara ter enumerado a coleção inteira — numa passada
        // incremental o servidor manda apenas o que mudou, e ausência não significa nada.
        // Deduzir isso de "o token está nulo" apagaria a agenda toda no servidor que não
        // fala sync-collection e responde "o CTag não mudou".
        if (changes.IsFullEnumeration)
        {
            removed += await RemoveVanishedAsync(calendar, seenHrefs, cancellationToken)
                .ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CalendarSyncResult(0, added, updated, removed, 0, conflicted);
    }

    /// <summary>
    /// Aplica um recurso do servidor sobre a agenda local.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> se criou, <see langword="false"/> se atualizou,
    /// <see langword="null"/> se o documento não pôde ser interpretado.
    /// </returns>
    private async Task<bool?> ApplyRemoteAsync(
        RemoteCalendar calendar,
        CalendarEvent? local,
        RemoteCalendarChange change,
        string? document,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document)
            || _serializer.Read(document) is not { } parsed
            || parsed.Events.Count == 0)
        {
            // Um .ics malformado numa coleção de milhares é rotina, não exceção. Descartar
            // o recurso e seguir é o certo; derrubar a coleção inteira, não.
            _logger.LogWarning(
                "Recurso do calendário {CalendarId} descartado por não ser interpretável.", calendar.Id);

            return null;
        }

        var data = parsed.Events[0];

        if (data.StartsAt is not { } startsAt)
        {
            return null;
        }

        var isNew = local is null;

        local ??= CalendarEvent.Create(
            calendar.AccountId, data.Uid, data.Summary, startsAt, data.EndsAt ?? startsAt, now);

        // A regra do SEQUENCE vale aqui como vale para o convite que chega por e-mail: o
        // CalDAV carrega o iCalendar íntegro, então a versão está lá.
        var applied = local.ApplyUpdate(
            data.Sequence,
            data.Summary,
            data.Description,
            data.Location,
            data.MeetingUrl,
            startsAt,
            data.EndsAt ?? startsAt,
            data.IsAllDay,
            data.TimeZoneId,
            data.Status,
            data.RecurrenceRule,
            now);

        if (!applied)
        {
            // SEQUENCE menor que o local: a versão do servidor é antiga (D-024). O ETag não
            // é gravado de propósito — gravá-lo declararia sincronia com um documento que
            // foi recusado, e a próxima escrita local subiria por cima do que está lá.
            _logger.LogInformation(
                "Versão do compromisso {EventId} recusada por SEQUENCE menor que a local.",
                local.Id);

            return null;
        }

        local.SetOrganizer(data.OrganizerAddress, data.OrganizerDisplayName, now);
        local.SyncAttendees(
            [.. data.Attendees.Select(a => new AttendeeSnapshot(
                a.Address, a.DisplayName, a.Role, a.Response))],
            now);

        local.BindToRemoteCalendar(calendar.Id, now);
        local.MarkRemoteSynced(change.Href, change.ETag, document, now);

        if (isNew)
        {
            await _events.AddAsync(local, cancellationToken).ConfigureAwait(false);
        }

        return isNew;
    }

    private async Task<int> RemoveVanishedAsync(
        RemoteCalendar calendar, HashSet<string> seenHrefs, CancellationToken cancellationToken)
    {
        var known = await _events.ListRemoteHrefsAsync(calendar.Id, cancellationToken)
            .ConfigureAwait(false);

        var removed = 0;

        foreach (var href in known.Where(h => !seenHrefs.Contains(h)))
        {
            var target = await _events.GetByRemoteHrefAsync(calendar.Id, href, cancellationToken)
                .ConfigureAwait(false);

            // Alteração local pendente não é apagada por ausência: ela ainda não subiu, e
            // o servidor não teria como listá-la.
            if (target is null || target.SyncState != CalendarSyncState.Synced)
            {
                continue;
            }

            _events.Remove(target);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Monta o documento a enviar.
    /// </summary>
    /// <remarks>
    /// O documento é reescrito a partir do modelo, e isso descarta o que este produto não
    /// modela — <c>X-*</c> de outros clientes, <c>VALARM</c>, parâmetros de participante que
    /// a projeção não carrega. Preservar exigiria costurar as alterações sobre o
    /// <c>RawICalendar</c> guardado, o que só é possível com um editor de documento que a
    /// porta do serializador não expõe. O <c>RawICalendar</c> fica gravado para quando
    /// existir.
    /// </remarks>
    private string Serialize(CalendarEvent target)
        => _serializer.WriteRequest(RespondToInvitationHandler.ToData(target));
}
