using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Sync;

/// <summary>
/// Drena a fila de saída, aplicando no servidor as ações que o usuário já executou
/// localmente.
/// </summary>
/// <remarks>
/// <para>
/// É a metade "reconcilia depois" do modo offline-first. A outra metade — gravar já — está
/// nos casos de uso, que persistem o efeito e enfileiram a operação na mesma transação.
/// </para>
/// <para>
/// O processamento é estritamente sequencial por conta, na ordem de
/// <see cref="OutboxOperation.Sequence"/>. Paralelizar aqui pareceria uma otimização
/// óbvia e quebraria a semântica: "mover para Arquivados" seguido de "marcar como lida"
/// aplicados fora de ordem fariam a segunda operação procurar a mensagem na pasta errada.
/// </para>
/// <para>
/// Uma falha interrompe o lote daquela conta em vez de pular para a próxima operação —
/// pelo mesmo motivo. As demais contas seguem normalmente.
/// </para>
/// </remarks>
public sealed class OutboxProcessor : IOutboxDrainer
{
    /// <summary>Quantas operações processar por ciclo, por conta.</summary>
    private const int BatchSize = 50;

    /// <summary>Espera base do recuo exponencial entre tentativas.</summary>
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Teto do recuo, para que uma conta com problema não pare de tentar.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IOutboxRepository _outbox;
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IImapClient _imapClient;
    private readonly ISmtpSender _smtpSender;
    private readonly IMimeMessageWriter _messageWriter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IOutboxRepository outbox,
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        ISmtpSender smtpSender,
        IMimeMessageWriter messageWriter,
        TimeProvider timeProvider,
        ILogger<OutboxProcessor> logger)
    {
        _outbox = outbox;
        _messages = messages;
        _folders = folders;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _imapClient = imapClient;
        _smtpSender = smtpSender;
        _messageWriter = messageWriter;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Processa as operações pendentes de uma conta.
    /// </summary>
    /// <returns>Quantas operações foram concluídas com sucesso.</returns>
    public async Task<int> DrainAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var now = _timeProvider.GetUtcNow();
        var pending = await _outbox.ListReadyAsync(account.Id, now, BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        var connection = await _imapClient.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        if (!connection.Succeeded)
        {
            _logger.LogInformation(
                "Fila da conta {AccountId} adiada: sem conexão com o servidor.", account.Id);
            return 0;
        }

        var completed = 0;

        foreach (var operation in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                operation.MarkInProgress(_timeProvider.GetUtcNow());
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);

                operation.MarkCompleted(_timeProvider.GetUtcNow());
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(operation, ex, cancellationToken).ConfigureAwait(false);

                // Interrompe o lote desta conta: as operações seguintes dependem da ordem,
                // e aplicá-las sobre um estado inconsistente pioraria a divergência.
                break;
            }
        }

        return completed;
    }

    private async Task ExecuteAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        switch (operation.OperationType)
        {
            case OutboxOperationType.MarkAsRead:
            case OutboxOperationType.MarkAsUnread:
            case OutboxOperationType.SetFlag:
            case OutboxOperationType.ClearFlag:
                await ApplyFlagsAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.MoveMessage:
                await ApplyMoveAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.DeleteMessage:
                await ApplyDeleteAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.ExpungeFolder:
                await ApplyExpungeAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.CopyMessage:
                await ApplyCopyAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.SendMessage:
                await ApplySendAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.AppendDraft:
                await ApplyAppendDraftAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.CreateFolder:
            case OutboxOperationType.RenameFolder:
            case OutboxOperationType.DeleteFolder:
            case OutboxOperationType.SetFolderSubscription:
                await ApplyFolderOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Cada tipo novo precisa de tratamento explícito. Ignorar em silêncio
                // faria a operação ser marcada como concluída sem nunca ter sido aplicada.
                throw new NotSupportedException(
                    $"A operação {operation.OperationType} ainda não é aplicada pelo processador da fila.");
        }
    }

    private async Task ApplyFlagsAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var (message, folder) = await ResolveMessageAsync(operation, cancellationToken).ConfigureAwait(false);
        if (message is null || folder is null || message.Uid is null)
        {
            return;
        }

        var payload = Deserialize<FlagChangePayload>(operation.PayloadJson);

        await _imapClient.SetFlagsAsync(
            folder.RemotePath,
            [message.Uid.Value],
            new MessageFlagChange(payload.Seen, payload.Flagged, payload.Answered, Junk: payload.Junk),
            cancellationToken).ConfigureAwait(false);

        message.MarkSynced(_timeProvider.GetUtcNow());
    }

    private async Task ApplyMoveAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var payload = Deserialize<MoveMessagePayload>(operation.PayloadJson);

        var message = await _messages.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Uid is null)
        {
            return;
        }

        var source = await _folders.GetByIdAsync(payload.SourceFolderId, cancellationToken).ConfigureAwait(false);
        var target = await _folders.GetByIdAsync(payload.TargetFolderId, cancellationToken).ConfigureAwait(false);

        if (source is null || target is null)
        {
            return;
        }

        // Pastas locais — pendências, caixa de saída — não existem no servidor. Mover
        // para elas é uma decisão puramente local e não gera comando IMAP.
        //
        // Pasta com sincronização desligada cai no mesmo caso, e por um motivo que custou
        // caro: as pastas padrão nascem com RemotePath adivinhado ("Sent", "Trash",
        // "Drafts"), e só "INBOX" é padronizado pela RFC 3501. Num Gmail — cujos caminhos
        // reais são "[Gmail]/Trash", "[Gmail]/Sent Mail" — nenhum dos chutes casa, e o
        // espelhamento desliga a sincronização delas por não as encontrar no servidor.
        //
        // Emitir o MOVE para esse caminho inexistente rendia FolderNotFoundException. E como
        // a fila é sequencial e para na primeira falha, a operação travava todas as seguintes
        // indefinidamente: dezoito movimentações presas, e a exclusão nunca chegando ao
        // servidor (D-050).
        if (target.IsLocalOnly || source.IsLocalOnly || !target.SyncEnabled || !source.SyncEnabled)
        {
            message.MarkSynced(_timeProvider.GetUtcNow());
            return;
        }

        var moved = await _imapClient
            .MoveAsync(source.RemotePath, target.RemotePath, [message.Uid.Value], cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        if (moved.TryGetValue(message.Uid.Value, out var newUid))
        {
            message.SetRemoteIdentity(newUid, null, now);
        }
        else
        {
            // Sem UIDPLUS o servidor não informa o novo UID. Zerá-lo faz a próxima
            // sincronização da pasta de destino reconciliar a mensagem pelo Message-ID.
            message.SetRemoteIdentity(0, null, now);
        }

        message.MarkSynced(now);
    }

    private async Task ApplyDeleteAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var (message, folder) = await ResolveMessageAsync(operation, cancellationToken).ConfigureAwait(false);
        if (message is null || folder is null || message.Uid is null)
        {
            return;
        }

        await _imapClient.SetFlagsAsync(
            folder.RemotePath,
            [message.Uid.Value],
            new MessageFlagChange(Deleted: true),
            cancellationToken).ConfigureAwait(false);

        var payload = Deserialize<DeleteMessagePayload>(operation.PayloadJson);
        if (payload.Permanent)
        {
            await _imapClient.ExpungeAsync(folder.RemotePath, cancellationToken).ConfigureAwait(false);
        }

        message.MarkSynced(_timeProvider.GetUtcNow());
    }

    private async Task ApplyExpungeAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var folder = await _folders.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (folder is null || folder.IsLocalOnly)
        {
            return;
        }

        await _imapClient.ExpungeAsync(folder.RemotePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyCopyAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var payload = Deserialize<MoveMessagePayload>(operation.PayloadJson);

        var message = await _messages.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (message?.Uid is null)
        {
            return;
        }

        var source = await _folders.GetByIdAsync(payload.SourceFolderId, cancellationToken).ConfigureAwait(false);
        var target = await _folders.GetByIdAsync(payload.TargetFolderId, cancellationToken).ConfigureAwait(false);

        if (source is null || target is null || source.IsLocalOnly || target.IsLocalOnly)
        {
            return;
        }

        await _imapClient
            .CopyAsync(source.RemotePath, target.RemotePath, [message.Uid.Value], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Envia a mensagem por SMTP e grava a cópia em Itens Enviados.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O <c>APPEND</c> em Enviados acontece depois do envio e <b>não</b> desfaz o envio se
    /// falhar. A mensagem já saiu; repetir a operação a enviaria de novo, e o destinatário
    /// receberia duas. Uma cópia ausente em Enviados é um incômodo; uma mensagem duplicada
    /// é um problema real com terceiros.
    /// </para>
    /// <para>
    /// Recusa definitiva do servidor — endereço inexistente, mensagem grande demais —
    /// encerra a operação em vez de gastar tentativas: nenhuma delas mudaria a resposta.
    /// </para>
    /// </remarks>
    private async Task ApplySendAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var message = await _messages
            .GetWithParticipantsAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return;
        }

        var outgoing = OutgoingMessageBuilder.Build(message, message.Body);

        if (outgoing is null)
        {
            throw new NotSupportedException(
                "A mensagem não está em condições de ser enviada: falta remetente, destinatário " +
                "ou o download de um anexo.");
        }

        var account = await _accounts.GetByIdAsync(operation.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conta {operation.AccountId} não encontrada.");

        var result = await _smtpSender.SendAsync(account, outgoing, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (result.IsPermanentFailure)
            {
                throw new NotSupportedException(result.ErrorMessage ?? "O servidor recusou a mensagem.");
            }

            throw new InvalidOperationException(result.ErrorMessage ?? "Falha ao enviar a mensagem.");
        }

        var now = _timeProvider.GetUtcNow();
        var sent = await _folders
            .GetByTypeAsync(operation.AccountId, FolderType.Sent, cancellationToken).ConfigureAwait(false);

        if (sent is not null && !sent.IsLocalOnly)
        {
            try
            {
                await AppendAsync(message, sent, isDraft: false, cancellationToken).ConfigureAwait(false);
                message.MoveTo(sent.Id, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "A mensagem {MessageId} foi enviada, mas a cópia em Itens Enviados falhou.",
                    message.Id);
            }
        }

        message.MarkSynced(now);
    }

    private async Task ApplyAppendDraftAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var message = await _messages
            .GetWithParticipantsAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return;
        }

        var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);

        if (folder is null || folder.IsLocalOnly)
        {
            return;
        }

        var uid = await AppendAsync(message, folder, isDraft: true, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        if (uid.HasValue)
        {
            message.SetRemoteIdentity(uid.Value, null, now);
        }

        message.MarkSynced(now);
    }

    private async Task<long?> AppendAsync(
        Message message, Folder folder, bool isDraft, CancellationToken cancellationToken)
    {
        var outgoing = OutgoingMessageBuilder.Build(message, message.Body)
            ?? throw new NotSupportedException("A mensagem não pôde ser montada para gravação no servidor.");

        await using var stream = await _messageWriter
            .WriteAsync(outgoing, cancellationToken).ConfigureAwait(false);

        return await _imapClient
            .AppendAsync(folder.RemotePath, stream, isDraft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Aplica no servidor uma operação de pasta já executada localmente.
    /// </summary>
    /// <remarks>
    /// Pasta local não gera comando: pendências e caixa de saída não existem no IMAP, e
    /// tentar criá-las lá poluiria a caixa postal com pastas que só fazem sentido aqui.
    /// </remarks>
    private async Task ApplyFolderOperationAsync(OutboxOperation operation, CancellationToken cancellationToken)
    {
        var payload = Deserialize<FolderOperationPayload>(operation.PayloadJson);

        if (payload.IsLocalOnly || string.IsNullOrWhiteSpace(payload.RemotePath))
        {
            return;
        }

        switch (operation.OperationType)
        {
            case OutboxOperationType.CreateFolder:
                await _imapClient.CreateFolderAsync(payload.RemotePath, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.RenameFolder:
                if (string.IsNullOrWhiteSpace(payload.NewRemotePath))
                {
                    throw new NotSupportedException("A renomeação de pasta não trouxe o novo caminho.");
                }

                await _imapClient
                    .RenameFolderAsync(payload.RemotePath, payload.NewRemotePath, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case OutboxOperationType.DeleteFolder:
                await _imapClient.DeleteFolderAsync(payload.RemotePath, cancellationToken).ConfigureAwait(false);
                break;

            case OutboxOperationType.SetFolderSubscription:
                await _imapClient
                    .SetSubscriptionAsync(payload.RemotePath, payload.IsSubscribed, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task<(Message? Message, Folder? Folder)> ResolveMessageAsync(
        OutboxOperation operation, CancellationToken cancellationToken)
    {
        var message = await _messages.GetByIdAsync(operation.EntityId, cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return (null, null);
        }

        var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
        return (message, folder);
    }

    private async Task HandleFailureAsync(
        OutboxOperation operation, Exception exception, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // NotSupportedException indica falha de programação, não instabilidade de rede:
        // repetir nunca vai resolver e só gastaria as tentativas restantes.
        var isPermanent = exception is NotSupportedException or JsonException;

        operation.MarkFailed(exception.Message, now + ComputeBackoff(operation.AttemptCount), now, isPermanent);

        _logger.LogWarning(
            exception,
            "Operação {OperationType} da conta {AccountId} falhou na tentativa {Attempt}.",
            operation.OperationType, operation.AccountId, operation.AttemptCount);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calcula o recuo exponencial com dispersão aleatória.
    /// </summary>
    /// <remarks>
    /// A dispersão evita que várias contas que perderam a conexão ao mesmo tempo voltem a
    /// tentar exatamente no mesmo instante, derrubando de novo o servidor que acabou de se
    /// recuperar.
    /// </remarks>
    internal static TimeSpan ComputeBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount, 10);
        var delay = BaseRetryDelay * Math.Pow(2, Math.Max(exponent - 1, 0));

        if (delay > MaxRetryDelay)
        {
            delay = MaxRetryDelay;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(delay.TotalMilliseconds * 0.2)));
        return delay + jitter;
    }

    private static TPayload Deserialize<TPayload>(string json)
        where TPayload : struct
        => JsonSerializer.Deserialize<TPayload>(json);
}
