using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Sync;

/// <summary>Resultado da sincronização de uma pasta.</summary>
/// <param name="Added">Mensagens novas trazidas do servidor.</param>
/// <param name="Updated">Mensagens já conhecidas cujos marcadores mudaram.</param>
/// <param name="RemovedRemotely">Mensagens que sumiram do servidor.</param>
/// <param name="RedirectedToPending">Mensagens desviadas pela regra de Diretório de Domínio.</param>
/// <param name="FullResync">Se o UIDVALIDITY mudou e a pasta teve de ser lida do zero.</param>
public readonly record struct FolderSyncResult(
    int Added,
    int Updated,
    int RemovedRemotely,
    int RedirectedToPending,
    bool FullResync);

/// <summary>
/// Sincroniza o conteúdo de uma pasta com o servidor.
/// </summary>
/// <remarks>
/// <para>
/// A estratégia é incremental por UID: o servidor atribui UIDs crescentes dentro de uma
/// pasta, então tudo acima de <c>LastSeenUid</c> é novidade. É bem mais barato do que
/// comparar a pasta inteira a cada ciclo, que é o que um cliente ingênuo faz.
/// </para>
/// <para>
/// <b>UIDVALIDITY é a exceção que derruba tudo.</b> Quando ele muda, o servidor está
/// dizendo que os UIDs foram reatribuídos — normalmente após restauração de backup ou
/// migração. Os UIDs locais passam a apontar para mensagens diferentes das originais, e
/// seguir incremental faria marcadores e exclusões caírem sobre mensagens erradas. A única
/// saída correta é ler a pasta do zero.
/// </para>
/// <para>
/// A regra de Diretório de Domínio não é avaliada aqui: mensagem recém-chegada em pasta
/// restrita vai para <see cref="MoveMessageHandler.ClassifyArrivalAsync"/>, que é o único
/// lugar autorizado a consultar o avaliador de pertencimento.
/// </para>
/// </remarks>
public sealed class MessageSyncService
{
    /// <summary>Quantos cabeçalhos buscar por ida ao servidor.</summary>
    /// <remarks>
    /// A primeira sincronização de uma caixa antiga pode ter dezenas de milhares de
    /// mensagens. Em lotes, a árvore de navegação começa a se preencher em segundos em vez
    /// de ficar vazia até o fim.
    /// </remarks>
    private const int FetchBatchSize = 200;

    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IImapClient _imapClient;
    private readonly MoveMessageHandler _moveMessage;
    private readonly UseCases.Rules.ApplyArrivalRulesHandler _arrivalRules;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MessageSyncService> _logger;

    public MessageSyncService(
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        MoveMessageHandler moveMessage,
        UseCases.Rules.ApplyArrivalRulesHandler arrivalRules,
        TimeProvider timeProvider,
        ILogger<MessageSyncService> logger)
    {
        _messages = messages;
        _folders = folders;
        _unitOfWork = unitOfWork;
        _imapClient = imapClient;
        _moveMessage = moveMessage;
        _arrivalRules = arrivalRules;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Sincroniza uma pasta.</summary>
    public async Task<FolderSyncResult> SyncFolderAsync(
        Folder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (folder.IsLocalOnly || !folder.SyncEnabled)
        {
            return default;
        }

        var now = _timeProvider.GetUtcNow();
        var state = await _imapClient.OpenFolderAsync(folder.RemotePath, cancellationToken).ConfigureAwait(false);

        var invalidated = folder.UpdateSyncState(state.UidValidity, state.HighestModSeq, null, now);

        if (invalidated)
        {
            _logger.LogWarning(
                "O UIDVALIDITY da pasta '{RemotePath}' mudou; a pasta será lida do zero.", folder.RemotePath);

            await InvalidateLocalUidsAsync(folder, now, cancellationToken).ConfigureAwait(false);
        }

        var sinceUid = invalidated ? 0 : folder.LastSeenUid ?? 0;
        var added = 0;
        var updated = 0;
        var redirected = 0;
        var highestUid = sinceUid;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fetched = await _imapClient
                .FetchHeadersAsync(folder.RemotePath, highestUid, FetchBatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.Count == 0)
            {
                break;
            }

            foreach (var header in fetched)
            {
                var outcome = await UpsertAsync(folder, header, cancellationToken).ConfigureAwait(false);

                switch (outcome)
                {
                    case UpsertOutcome.Added:
                        added++;
                        break;
                    case UpsertOutcome.AddedAndRedirected:
                        added++;
                        redirected++;
                        break;
                    case UpsertOutcome.Updated:
                        updated++;
                        break;
                }

                highestUid = Math.Max(highestUid, header.Uid);
            }

            // O ponto de partida avança a cada lote gravado. Se a conexão cair no meio de
            // uma caixa grande, o próximo ciclo retoma daqui em vez de recomeçar.
            folder.UpdateSyncState(state.UidValidity, state.HighestModSeq, highestUid, _timeProvider.GetUtcNow());
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (fetched.Count < FetchBatchSize)
            {
                break;
            }
        }

        var removed = await ReconcileDeletionsAsync(folder, state, cancellationToken).ConfigureAwait(false);

        await UpdateCountsAsync(folder, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new FolderSyncResult(added, updated, removed, redirected, invalidated);
    }

    private enum UpsertOutcome
    {
        Added,
        AddedAndRedirected,
        Updated,
        Unchanged,
    }

    /// <summary>
    /// Cria ou atualiza a mensagem correspondente a um cabeçalho vindo do servidor.
    /// </summary>
    /// <remarks>
    /// A busca é por UID e, na falta dele, por <c>Message-ID</c>. O segundo caminho existe
    /// por causa do MOVE em servidor sem UIDPLUS: a mensagem reaparece na pasta de destino
    /// com UID novo, e sem essa reconciliação seria gravada de novo como se fosse outra.
    /// </remarks>
    private async Task<UpsertOutcome> UpsertAsync(
        Folder folder, FetchedMessage header, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var existing = await _messages.GetByUidAsync(folder.Id, header.Uid, cancellationToken).ConfigureAwait(false)
            ?? await _messages.GetByMessageIdAsync(folder.AccountId, header.MessageId, cancellationToken)
                .ConfigureAwait(false);

        if (existing is not null)
        {
            return ApplyRemoteFlags(existing, header, folder, now) ? UpsertOutcome.Updated : UpsertOutcome.Unchanged;
        }

        var message = Message.Create(
            folder.AccountId, folder.Id, header.MessageId, header.SentAt, header.ReceivedAt, now);

        EmailAddress.TryParse(header.FromAddress, out var from);

        message.SetHeaders(
            header.Subject, from, header.FromDisplayName, header.InReplyTo, header.References, now);

        message.SetContentMetadata(
            preview: string.Empty,
            header.Size,
            header.HasAttachments,
            header.Importance,
            header.ReadReceiptRequested,
            now);

        foreach (var participant in header.Addresses)
        {
            if (EmailAddress.TryParse(participant.Address, out var address))
            {
                message.AddAddress(MessageAddress.Create(
                    message.Id, participant.Kind, address, now, participant.DisplayName));
            }
        }

        message.SetRemoteIdentity(header.Uid, header.ModSeq, now);

        // O veredito do servidor é gravado uma vez, na chegada. Reavaliá-lo depois seria
        // impossível: SPF, DKIM e DMARC dependem do DNS no instante em que a mensagem chegou.
        message.SetAuthenticationResults(
            header.SpfResult,
            header.DkimResult,
            header.DmarcResult,
            header.IsFlaggedAsSpam,
            header.SpamScore,
            now);

        ApplyRemoteFlags(message, header, folder, now);
        message.MarkSynced(now);

        await _messages.AddAsync(message, cancellationToken).ConfigureAwait(false);

        // Precisa estar gravada antes da classificação: o avaliador de pertencimento lê os
        // participantes pelo repositório, e eles ainda não existiriam para consulta.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var classification = await _moveMessage
            .ClassifyArrivalAsync(message, folder, cancellationToken).ConfigureAwait(false);

        if (classification.Outcome == MoveMessageOutcome.MovedToPending)
        {
            return UpsertOutcome.AddedAndRedirected;
        }

        // Filtragem local — bloqueados e regras automáticas — só na Caixa de Entrada, que
        // é onde a chegada acontece. Aplicá-la em Enviados ou Arquivados refaria decisões
        // sobre mensagens que o usuário já organizou.
        if (folder.FolderType == FolderType.Inbox)
        {
            await _arrivalRules.HandleAsync(message.Id, cancellationToken).ConfigureAwait(false);
        }

        return UpsertOutcome.Added;
    }

    /// <summary>
    /// Aplica os marcadores do servidor a uma mensagem já conhecida.
    /// </summary>
    /// <remarks>
    /// Alteração local ainda não sincronizada tem precedência: o usuário marcou como lida
    /// offline e a fila ainda não empurrou isso. Deixar o servidor vencer aqui desfaria a
    /// ação dele diante dos próprios olhos, e a fila em seguida a refaria — um pisca-pisca
    /// que parece defeito e é.
    /// </remarks>
    private bool ApplyRemoteFlags(Message message, FetchedMessage header, Folder folder, DateTimeOffset now)
    {
        if (message.SyncState != MessageSyncState.Synced)
        {
            return false;
        }

        var changed = false;

        if (message.IsRead != header.IsRead)
        {
            message.SetRead(header.IsRead, now);
            changed = true;
        }

        if (message.IsFlagged != header.IsFlagged)
        {
            message.SetFlagged(header.IsFlagged, now);
            changed = true;
        }

        if (message.Uid != header.Uid || message.ModSeq != header.ModSeq)
        {
            message.SetRemoteIdentity(header.Uid, header.ModSeq, now);
            changed = true;
        }

        if (changed)
        {
            // As alterações vieram do servidor: já estão sincronizadas por definição, e
            // marcá-las como pendentes faria a fila devolver ao servidor o que ele mandou.
            message.MarkSynced(now);
        }

        return changed;
    }

    /// <summary>
    /// Descobre o que foi apagado no servidor por outra sessão.
    /// </summary>
    /// <remarks>
    /// A comparação só roda quando a contagem local e a do servidor divergem. Listar todos
    /// os UIDs de uma pasta grande a cada ciclo custaria caro para, quase sempre, confirmar
    /// que nada mudou.
    /// </remarks>
    private async Task<int> ReconcileDeletionsAsync(
        Folder folder, FolderSyncState state, CancellationToken cancellationToken)
    {
        var localUids = await _messages.ListUidsByFolderAsync(folder.Id, cancellationToken).ConfigureAwait(false);

        if (localUids.Count <= state.TotalCount)
        {
            return 0;
        }

        var remoteUids = (await _imapClient
                .FetchHeadersAsync(folder.RemotePath, 0, int.MaxValue, cancellationToken).ConfigureAwait(false))
            .Select(h => h.Uid)
            .ToHashSet();

        var now = _timeProvider.GetUtcNow();
        var removed = 0;

        foreach (var uid in localUids.Where(uid => !remoteUids.Contains(uid)))
        {
            var message = await _messages.GetByUidAsync(folder.Id, uid, cancellationToken).ConfigureAwait(false);

            if (message is null || message.SyncState != MessageSyncState.Synced)
            {
                continue;
            }

            _messages.Remove(message);
            removed++;
        }

        if (removed > 0)
        {
            _logger.LogInformation(
                "{Removed} mensagem(ns) da pasta '{RemotePath}' foram apagadas fora deste cliente.",
                removed, folder.RemotePath);

            folder.UpdateSyncState(state.UidValidity, state.HighestModSeq, folder.LastSeenUid, now);
        }

        return removed;
    }

    /// <summary>
    /// Descarta os UIDs locais depois de o servidor reatribuí-los.
    /// </summary>
    /// <remarks>
    /// As mensagens permanecem: elas continuam sendo as mesmas para o usuário, e apagá-las
    /// custaria o histórico offline inteiro. O que se perde é a correspondência com o
    /// servidor, que a leitura completa em seguida reconstrói pelo <c>Message-ID</c>.
    /// </remarks>
    private async Task InvalidateLocalUidsAsync(
        Folder folder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var uids = await _messages.ListUidsByFolderAsync(folder.Id, cancellationToken).ConfigureAwait(false);

        foreach (var uid in uids)
        {
            var message = await _messages.GetByUidAsync(folder.Id, uid, cancellationToken).ConfigureAwait(false);
            message?.SetRemoteIdentity(0, null, now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateCountsAsync(Folder folder, CancellationToken cancellationToken)
    {
        var total = await _folders.CountMessagesAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        var unread = await _messages.CountUnreadAsync(folder.Id, cancellationToken).ConfigureAwait(false);

        folder.UpdateCounts(total, unread, _timeProvider.GetUtcNow());
    }
}
