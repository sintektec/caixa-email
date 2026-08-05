using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>Um anexo escolhido no compositor, já presente no disco local.</summary>
/// <param name="FileName">Nome exibido ao destinatário.</param>
/// <param name="FilePath">Caminho do arquivo no disco.</param>
/// <param name="ContentType">Tipo MIME.</param>
/// <param name="Size">Tamanho em bytes.</param>
public readonly record struct ComposedAttachment(
    string FileName, string FilePath, string ContentType, long Size);

/// <summary>Conteúdo do compositor a gravar ou enviar.</summary>
public sealed record ComposeMessageCommand
{
    /// <summary>Conta que escreve.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Rascunho existente sendo editado, quando houver.</summary>
    public Guid? DraftId { get; init; }

    /// <summary>Destinatários, por campo de endereçamento.</summary>
    public IReadOnlyList<DraftRecipient> Recipients { get; init; } = [];

    /// <summary>Assunto.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Corpo em HTML.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Corpo em texto puro.</summary>
    public string? TextBody { get; init; }

    /// <summary>Anexos escolhidos.</summary>
    public IReadOnlyList<ComposedAttachment> Attachments { get; init; } = [];

    /// <summary>Message-ID ao qual esta mensagem responde.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>Cadeia References da conversa.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Conversa à qual pertence.</summary>
    public Guid? ThreadId { get; init; }

    /// <summary>Prioridade declarada.</summary>
    public MessageImportance Importance { get; init; } = MessageImportance.Normal;

    /// <summary>Se pede confirmação de leitura.</summary>
    public bool RequestReadReceipt { get; init; }

    /// <summary>
    /// Instante do envio agendado. Nulo envia assim que a fila drenar.
    /// </summary>
    /// <remarks>
    /// O agendamento é a data em que a operação da fila fica elegível: a fila já respeita
    /// <c>NextAttemptAt</c>, então não existe um segundo mecanismo de espera para manter
    /// em sincronia com o primeiro.
    /// </remarks>
    public DateTimeOffset? ScheduledSendAt { get; init; }
}

/// <summary>Resultado da gravação ou do envio.</summary>
/// <param name="Succeeded">Se a operação concluiu.</param>
/// <param name="MessageId">Identificador local da mensagem.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct ComposeMessageResult(bool Succeeded, Guid? MessageId, string? ErrorMessage);

/// <summary>
/// Grava rascunhos e envia mensagens, sempre pelo caminho offline-first.
/// </summary>
/// <remarks>
/// <para>
/// Enviar aqui significa: gravar a mensagem na Caixa de Saída local e enfileirar a operação
/// de envio, <b>na mesma transação</b>. O SMTP acontece depois, quando a fila drenar. É o que
/// faz o botão Enviar funcionar num avião — a mensagem sai quando a rede voltar, e a fila
/// visível mostra que ela ainda não saiu.
/// </para>
/// <para>
/// O rascunho segue o mesmo desenho: grava local, enfileira o <c>APPEND</c> para a pasta de
/// Rascunhos do servidor. Editar de novo substitui o conteúdo e reenfileira.
/// </para>
/// </remarks>
public sealed class ComposeMessageHandler
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly UseCases.Contacts.RecipientHistoryHandler _recipientHistory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComposeMessageHandler> _logger;

    public ComposeMessageHandler(
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        UseCases.Contacts.RecipientHistoryHandler recipientHistory,
        TimeProvider timeProvider,
        ILogger<ComposeMessageHandler> logger)
    {
        _messages = messages;
        _folders = folders;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _recipientHistory = recipientHistory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Grava o rascunho.</summary>
    public Task<ComposeMessageResult> SaveDraftAsync(
        ComposeMessageCommand command, CancellationToken cancellationToken = default)
        => PersistAsync(command, sending: false, cancellationToken);

    /// <summary>
    /// Envia a mensagem — isto é, entrega-a à fila de saída.
    /// </summary>
    /// <remarks>
    /// A validação de destinatário acontece aqui, antes de qualquer gravação. Deixá-la para o
    /// processador da fila transformaria um esquecimento simples em uma operação morta que o
    /// usuário só descobriria na tela da fila.
    /// </remarks>
    public async Task<ComposeMessageResult> SendAsync(
        ComposeMessageCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!command.Recipients.Any(r => r.Kind is AddressKind.To or AddressKind.Cc or AddressKind.Bcc))
        {
            return new ComposeMessageResult(false, null, "Informe ao menos um destinatário.");
        }

        // Agendamento no passado é erro de digitação, não intenção: recusar aqui evita a
        // exceção da entidade e devolve texto que o usuário entende.
        if (command.ScheduledSendAt is { } sendAt && sendAt <= _timeProvider.GetUtcNow())
        {
            return new ComposeMessageResult(
                false, null, "O horário de envio agendado precisa estar no futuro.");
        }

        return await PersistAsync(command, sending: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ComposeMessageResult> PersistAsync(
        ComposeMessageCommand command, bool sending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await _accounts.GetByIdAsync(command.AccountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return new ComposeMessageResult(false, null, "A conta informada não existe.");
        }

        var targetType = sending ? FolderType.Outbox : FolderType.Drafts;
        var target = await _folders.GetByTypeAsync(account.Id, targetType, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new ComposeMessageResult(
                false, null,
                sending
                    ? "A conta não tem Caixa de Saída configurada."
                    : "A conta não tem pasta de Rascunhos configurada.");
        }

        var now = _timeProvider.GetUtcNow();

        var message = command.DraftId is { } draftId
            ? await _messages.GetWithParticipantsAsync(draftId, cancellationToken).ConfigureAwait(false)
            : null;

        ComposeMessageResult result = default;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (message is null)
            {
                // O Message-ID local é provisório e troca no envio, quando o serializador
                // MIME gera o definitivo. Ele existe para a mensagem ter identidade no banco
                // enquanto é rascunho.
                message = Message.Create(
                    account.Id,
                    target.Id,
                    $"<rascunho-{Guid.CreateVersion7():N}@sintek.local>",
                    now,
                    now,
                    now);

                message.MarkAsDraft(now);
                await _messages.AddAsync(message, ct).ConfigureAwait(false);
            }
            else
            {
                message.MoveTo(target.Id, now);
            }

            message.SetHeaders(
                command.Subject,
                account.EmailAddress,
                account.DisplayName,
                command.InReplyTo,
                command.References.Count > 0 ? string.Join(' ', command.References) : null,
                now);

            if (command.ThreadId is { } threadId)
            {
                message.AssignThread(threadId, now);
            }

            message.SetContentMetadata(
                BuildPreview(command.TextBody),
                command.TextBody?.Length ?? 0,
                command.Attachments.Count > 0,
                command.Importance,
                command.RequestReadReceipt,
                now);

            SyncAddresses(message, command, now);
            SyncAttachments(message, command, now);

            var body = message.Body ?? MessageBody.Create(message.Id, now);

            // Conteúdo escrito aqui dispensa sanitização para leitura própria, mas o
            // higienizado é gravado mesmo assim: o painel de leitura só renderiza
            // SanitizedHtml, e um rascunho reaberto passa por ele como qualquer mensagem.
            body.SetContent(command.HtmlBody, command.TextBody, command.HtmlBody, false, now);

            if (message.Body is null)
            {
                message.SetBody(body, now);
            }

            if (sending)
            {
                if (command.ScheduledSendAt is { } sendAt)
                {
                    message.ScheduleSend(sendAt, now);
                }

                await _outbox.EnqueueAsync(
                    account.Id,
                    OutboxOperationType.SendMessage,
                    message.Id,
                    new SendMessagePayload(),
                    ct,
                    notBefore: command.ScheduledSendAt).ConfigureAwait(false);
            }
            else
            {
                await _outbox.EnqueueAsync(
                    account.Id,
                    OutboxOperationType.AppendDraft,
                    message.Id,
                    new SendMessagePayload(CopyToSentFolder: false),
                    ct).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            result = new ComposeMessageResult(true, message.Id, null);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            sending
                ? "Mensagem {MessageId} entregue à fila de envio."
                : "Rascunho {MessageId} gravado.",
            result.MessageId);

        // O histórico é alimentado depois da transação, e só no envio. Depois porque o
        // autocompletar é conveniência e não pode desfazer uma mensagem já enfileirada;
        // só no envio porque um rascunho abandonado não é intenção de escrever para
        // ninguém.
        if (sending && result.Succeeded)
        {
            await _recipientHistory.RecordUseAsync(
                account.Id,
                command.Recipients
                    .Where(r => r.Kind is AddressKind.To or AddressKind.Cc or AddressKind.Bcc)
                    .Select(r => new UseCases.Contacts.UsedRecipient(r.Address, r.DisplayName))
                    .ToList(),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Sincroniza os participantes com o que está no compositor.
    /// </summary>
    /// <remarks>
    /// Substituição completa: o rascunho reeditado reflete a lista atual, não a soma das
    /// listas de todas as edições.
    /// </remarks>
    private static void SyncAddresses(Message message, ComposeMessageCommand command, DateTimeOffset now)
    {
        message.ClearAddresses();

        foreach (var recipient in command.Recipients)
        {
            message.AddAddress(MessageAddress.Create(
                message.Id, recipient.Kind, recipient.Address, now, recipient.DisplayName));
        }
    }

    private static void SyncAttachments(Message message, ComposeMessageCommand command, DateTimeOffset now)
    {
        message.ClearAttachments();

        foreach (var attachment in command.Attachments)
        {
            var entity = Attachment.Create(
                message.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                partSpecifier: string.Empty,
                now);

            // O arquivo já está no disco local — foi o usuário quem o escolheu. Marcar como
            // baixado é o que permite ao montador de envio anexá-lo.
            entity.MarkDownloaded(attachment.FilePath, now);
            message.AddAttachment(entity);
        }
    }

    private static string BuildPreview(string? textBody)
    {
        if (string.IsNullOrWhiteSpace(textBody))
        {
            return string.Empty;
        }

        var singleLine = textBody.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 120 ? singleLine : singleLine[..120];
    }
}
