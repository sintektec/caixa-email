using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Uma mensagem de e-mail armazenada localmente.
/// </summary>
/// <remarks>
/// O corpo vive em <see cref="MessageBody"/>, em tabela separada. A listagem de uma
/// pasta lê centenas de linhas desta entidade e nenhuma delas precisa do corpo; mantê-lo
/// aqui faria cada rolagem arrastar megabytes de HTML sem necessidade.
/// </remarks>
public sealed class Message : Entity
{
    private readonly List<MessageAddress> _addresses = [];
    private readonly List<Attachment> _attachments = [];
    private readonly List<MessageCategory> _categories = [];

    private Message(
        Guid id,
        Guid accountId,
        Guid folderId,
        string messageId,
        DateTimeOffset sentAt,
        DateTimeOffset receivedAt,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        FolderId = folderId;
        MessageId = messageId;
        SentAt = sentAt;
        ReceivedAt = receivedAt;
    }

    private Message()
    {
    }

    /// <summary>Conta que possui a mensagem.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Conta que possui a mensagem.</summary>
    public Account? Account { get; private set; }

    /// <summary>Pasta em que a mensagem está.</summary>
    public Guid FolderId { get; private set; }

    /// <summary>Pasta em que a mensagem está.</summary>
    public Folder? Folder { get; private set; }

    /// <summary>Conversa à qual a mensagem pertence.</summary>
    public Guid? ThreadId { get; private set; }

    /// <summary>Message-ID da RFC 5322. Identidade estável entre servidores.</summary>
    public string MessageId { get; private set; } = string.Empty;

    /// <summary>Cabeçalho In-Reply-To.</summary>
    public string? InReplyTo { get; private set; }

    /// <summary>Cabeçalho References, como veio.</summary>
    public string? ReferencesRaw { get; private set; }

    /// <summary>UID da mensagem na pasta IMAP. Válido apenas para o UIDVALIDITY corrente.</summary>
    public long? Uid { get; private set; }

    /// <summary>MODSEQ (CONDSTORE) da última alteração conhecida no servidor.</summary>
    public long? ModSeq { get; private set; }

    /// <summary>Resultado do SPF, conforme o servidor informou.</summary>
    public AuthenticationResult SpfResult { get; private set; } = AuthenticationResult.Unknown;

    /// <summary>Resultado do DKIM.</summary>
    public AuthenticationResult DkimResult { get; private set; } = AuthenticationResult.Unknown;

    /// <summary>Resultado do DMARC.</summary>
    public AuthenticationResult DmarcResult { get; private set; } = AuthenticationResult.Unknown;

    /// <summary>Se o servidor classificou a mensagem como lixo eletrônico.</summary>
    public bool IsFlaggedAsSpamByServer { get; private set; }

    /// <summary>
    /// Pontuação de spam atribuída pelo servidor, quando informada.
    /// </summary>
    /// <remarks>
    /// A escala varia entre implementações — SpamAssassin e Rspamd usam faixas diferentes —,
    /// então o número só é exibido, nunca comparado com um limiar próprio. Quem decide o que
    /// é spam é o servidor.
    /// </remarks>
    public double? SpamScore { get; private set; }

    /// <summary>Assunto, como veio.</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>
    /// Assunto sem prefixos de resposta e encaminhamento, em minúsculas. Usado para
    /// agrupar por conversa quando os cabeçalhos References não bastam — o que acontece
    /// sempre que alguém responde a partir de um cliente que os descarta.
    /// </summary>
    public string SubjectNormalized { get; private set; } = string.Empty;

    /// <summary>Endereço do remetente, desnormalizado para ordenar e exibir a listagem.</summary>
    public EmailAddress? FromAddress { get; private set; }

    /// <summary>Nome exibido do remetente.</summary>
    public string? FromDisplayName { get; private set; }

    /// <summary>Instante em que a mensagem foi enviada, com o fuso do cabeçalho Date.</summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>Instante em que o servidor recebeu a mensagem (INTERNALDATE).</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Primeiras linhas do corpo em texto, para a prévia na listagem.</summary>
    public string Preview { get; private set; } = string.Empty;

    /// <summary>Tamanho total em bytes.</summary>
    public long Size { get; private set; }

    /// <summary>Se a mensagem tem anexos não embutidos.</summary>
    public bool HasAttachments { get; private set; }

    /// <summary>Se a mensagem foi lida.</summary>
    public bool IsRead { get; private set; }

    /// <summary>Se a mensagem está sinalizada.</summary>
    public bool IsFlagged { get; private set; }

    /// <summary>Prioridade declarada.</summary>
    public MessageImportance Importance { get; private set; } = MessageImportance.Normal;

    /// <summary>Se a mensagem é um rascunho.</summary>
    public bool IsDraft { get; private set; }

    /// <summary>Se a mensagem já foi respondida.</summary>
    public bool IsAnswered { get; private set; }

    /// <summary>
    /// Exclusão lógica. A mensagem some da interface mas permanece no banco até o
    /// expurgo, que é o que permite restaurá-la da lixeira offline.
    /// </summary>
    public bool IsDeleted { get; private set; }

    /// <summary>Situação da mensagem perante o servidor.</summary>
    public MessageSyncState SyncState { get; private set; } = MessageSyncState.Synced;

    /// <summary>Instante do envio agendado, quando houver.</summary>
    public DateTimeOffset? ScheduledSendAt { get; private set; }

    /// <summary>Se o remetente pediu confirmação de leitura.</summary>
    public bool ReadReceiptRequested { get; private set; }

    /// <summary>
    /// Se a confirmação de leitura já foi respondida — enviada ou recusada pelo usuário.
    /// </summary>
    /// <remarks>
    /// Guardado para que a pergunta não reapareça a cada vez que a mensagem é aberta.
    /// Perguntar de novo depois de um "não" trata a recusa como se não tivesse valido.
    /// </remarks>
    public bool ReadReceiptHandled { get; private set; }

    /// <summary>Corpo da mensagem, carregado sob demanda.</summary>
    public MessageBody? Body { get; private set; }

    /// <summary>Participantes da mensagem.</summary>
    public IReadOnlyCollection<MessageAddress> Addresses => _addresses;

    /// <summary>Anexos.</summary>
    public IReadOnlyCollection<Attachment> Attachments => _attachments;

    /// <summary>Categorias aplicadas.</summary>
    public IReadOnlyCollection<MessageCategory> Categories => _categories;

    /// <summary>Cria uma mensagem.</summary>
    public static Message Create(
        Guid accountId,
        Guid folderId,
        string messageId,
        DateTimeOffset sentAt,
        DateTimeOffset receivedAt,
        DateTimeOffset createdAt,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        return new Message(
            id ?? Guid.CreateVersion7(),
            accountId,
            folderId,
            messageId.Trim(),
            sentAt,
            receivedAt,
            createdAt);
    }

    /// <summary>Preenche os campos vindos dos cabeçalhos.</summary>
    public void SetHeaders(
        string subject,
        EmailAddress? fromAddress,
        string? fromDisplayName,
        string? inReplyTo,
        string? referencesRaw,
        DateTimeOffset now)
    {
        Subject = subject ?? string.Empty;
        SubjectNormalized = NormalizeSubject(Subject);
        FromAddress = fromAddress;
        FromDisplayName = string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName.Trim();
        InReplyTo = inReplyTo;
        ReferencesRaw = referencesRaw;
        Touch(now);
    }

    /// <summary>Preenche os metadados de exibição e tamanho.</summary>
    public void SetContentMetadata(
        string preview,
        long size,
        bool hasAttachments,
        MessageImportance importance,
        bool readReceiptRequested,
        DateTimeOffset now)
    {
        Preview = preview ?? string.Empty;
        Size = size;
        HasAttachments = hasAttachments;
        Importance = importance;
        ReadReceiptRequested = readReceiptRequested;
        Touch(now);
    }

    /// <summary>
    /// Registra o que o servidor apurou sobre a autenticação e a classificação da mensagem.
    /// </summary>
    /// <remarks>
    /// Nada aqui é verificado por nós. SPF, DKIM e DMARC exigem consultar o DNS no momento em
    /// que a mensagem chegou ao servidor de recebimento; refazer a verificação dias depois,
    /// do lado do cliente, daria resultado diferente e errado — chaves DKIM rotacionam e
    /// registros SPF mudam.
    /// </remarks>
    public void SetAuthenticationResults(
        AuthenticationResult spf,
        AuthenticationResult dkim,
        AuthenticationResult dmarc,
        bool isFlaggedAsSpam,
        double? spamScore,
        DateTimeOffset now)
    {
        SpfResult = spf;
        DkimResult = dkim;
        DmarcResult = dmarc;
        IsFlaggedAsSpamByServer = isFlaggedAsSpam;
        SpamScore = spamScore;
        Touch(now);
    }

    /// <summary>Associa a mensagem a uma conversa.</summary>
    public void AssignThread(Guid threadId, DateTimeOffset now)
    {
        ThreadId = threadId;
        Touch(now);
    }

    /// <summary>Adiciona um participante.</summary>
    public void AddAddress(MessageAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _addresses.Add(address);
    }

    /// <summary>Adiciona um anexo.</summary>
    public void AddAttachment(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _attachments.Add(attachment);
        HasAttachments = _attachments.Any(a => !a.IsInline);
    }

    /// <summary>
    /// Remove todos os participantes.
    /// </summary>
    /// <remarks>
    /// Existe para a reedição de rascunho, que substitui a lista inteira: sem isso, cada
    /// gravação somaria os destinatários de todas as edições anteriores.
    /// </remarks>
    public void ClearAddresses() => _addresses.Clear();

    /// <summary>Remove todos os anexos. Mesma razão de <see cref="ClearAddresses"/>.</summary>
    public void ClearAttachments()
    {
        _attachments.Clear();
        HasAttachments = false;
    }

    /// <summary>Define o corpo da mensagem.</summary>
    public void SetBody(MessageBody body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        Body = body;
        Touch(now);
    }

    /// <summary>
    /// Marca como lida ou não lida.
    /// </summary>
    /// <remarks>
    /// A gravação é local e imediata; a mensagem entra em
    /// <see cref="MessageSyncState.PendingUpdate"/> para que a fila de saída propague o
    /// marcador quando houver conexão. É esse par — grava já, reconcilia depois — que
    /// sustenta o modo offline-first.
    /// </remarks>
    public void SetRead(bool isRead, DateTimeOffset now)
    {
        if (IsRead == isRead)
        {
            return;
        }

        IsRead = isRead;
        MarkPending(MessageSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Aplica ou remove o sinalizador.</summary>
    public void SetFlagged(bool isFlagged, DateTimeOffset now)
    {
        if (IsFlagged == isFlagged)
        {
            return;
        }

        IsFlagged = isFlagged;
        MarkPending(MessageSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Define a prioridade.</summary>
    public void SetImportance(MessageImportance importance, DateTimeOffset now)
    {
        if (Importance == importance)
        {
            return;
        }

        Importance = importance;
        MarkPending(MessageSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>Marca como respondida.</summary>
    public void MarkAnswered(DateTimeOffset now)
    {
        if (IsAnswered)
        {
            return;
        }

        IsAnswered = true;
        MarkPending(MessageSyncState.PendingUpdate);
        Touch(now);
    }

    /// <summary>
    /// Move a mensagem para outra pasta.
    /// </summary>
    /// <remarks>
    /// A validação de domínio NÃO acontece aqui. Ela é responsabilidade do caso de uso
    /// de movimentação, que consulta o <c>DomainMembershipEvaluator</c> antes de chamar
    /// este método — a mensagem sozinha não conhece a regra da pasta de destino.
    /// </remarks>
    public void MoveTo(Guid folderId, DateTimeOffset now)
    {
        if (FolderId == folderId)
        {
            return;
        }

        FolderId = folderId;
        MarkPending(MessageSyncState.PendingMove);
        Touch(now);
    }

    /// <summary>Marca como excluída (exclusão lógica, restaurável da lixeira).</summary>
    public void MarkDeleted(DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        MarkPending(MessageSyncState.PendingDelete);
        Touch(now);
    }

    /// <summary>Restaura uma mensagem excluída logicamente.</summary>
    /// <remarks>
    /// Atribui o estado diretamente, sem passar por <see cref="MarkPending"/>. Restaurar
    /// é a reversão explícita de uma exclusão, e o guarda que impede o rebaixamento de
    /// <see cref="MessageSyncState.PendingDelete"/> — correto para alterações de marcador
    /// — deixaria a mensagem eternamente pendente de exclusão.
    /// </remarks>
    public void Restore(Guid targetFolderId, DateTimeOffset now)
    {
        IsDeleted = false;
        FolderId = targetFolderId;

        // Uma mensagem que só existia localmente continua só local depois de restaurada:
        // não há o que mover em servidor nenhum.
        if (SyncState is not (MessageSyncState.LocalOnly or MessageSyncState.PendingUpload))
        {
            SyncState = MessageSyncState.PendingMove;
        }

        Touch(now);
    }

    /// <summary>Registra que o pedido de confirmação de leitura já foi decidido.</summary>
    public void MarkReadReceiptHandled(DateTimeOffset now)
    {
        ReadReceiptHandled = true;
        Touch(now);
    }

    /// <summary>Agenda o envio para um instante futuro.</summary>
    public void ScheduleSend(DateTimeOffset sendAt, DateTimeOffset now)
    {
        if (sendAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sendAt), "O envio agendado precisa ser em um instante futuro.");
        }

        ScheduledSendAt = sendAt;
        Touch(now);
    }

    /// <summary>Cancela o agendamento de envio.</summary>
    public void CancelSchedule(DateTimeOffset now)
    {
        ScheduledSendAt = null;
        Touch(now);
    }

    /// <summary>Marca a mensagem como rascunho local.</summary>
    public void MarkAsDraft(DateTimeOffset now)
    {
        IsDraft = true;
        SyncState = MessageSyncState.LocalOnly;
        Touch(now);
    }

    /// <summary>Grava a identidade da mensagem no servidor após a sincronização.</summary>
    public void SetRemoteIdentity(long uid, long? modSeq, DateTimeOffset now)
    {
        Uid = uid;
        ModSeq = modSeq;
        Touch(now);
    }

    /// <summary>Confirma que a mensagem está idêntica ao servidor.</summary>
    public void MarkSynced(DateTimeOffset now)
    {
        SyncState = MessageSyncState.Synced;
        Touch(now);
    }

    /// <summary>Sinaliza divergência entre a versão local e a do servidor.</summary>
    public void MarkConflicted(DateTimeOffset now)
    {
        SyncState = MessageSyncState.Conflict;
        Touch(now);
    }

    /// <summary>
    /// Registra que há alteração local pendente, sem sobrescrever um estado mais forte.
    /// </summary>
    /// <remarks>
    /// A ordem importa: uma mensagem marcada para exclusão que também teve o marcador de
    /// leitura alterado continua pendente de exclusão. Rebaixá-la para
    /// <see cref="MessageSyncState.PendingUpdate"/> faria a fila propagar o marcador e
    /// esquecer a exclusão. Conflito e criação local nunca são rebaixados.
    /// </remarks>
    private void MarkPending(MessageSyncState desired)
    {
        if (SyncState is MessageSyncState.LocalOnly
            or MessageSyncState.PendingUpload
            or MessageSyncState.Conflict)
        {
            return;
        }

        if (SyncState == MessageSyncState.PendingDelete)
        {
            return;
        }

        if (SyncState == MessageSyncState.PendingMove && desired == MessageSyncState.PendingUpdate)
        {
            return;
        }

        SyncState = desired;
    }

    /// <summary>
    /// Remove prefixos de resposta e encaminhamento do assunto para agrupar conversas.
    /// </summary>
    /// <remarks>
    /// Cobre as formas em português e inglês, com ou sem contador (<c>Re:</c>, <c>RES:</c>,
    /// <c>ENC:</c>, <c>Fwd:</c>, <c>Re[2]:</c>), aplicadas repetidamente porque cadeias
    /// longas acumulam prefixos ("Re: Enc: Re: ...").
    /// </remarks>
    public static string NormalizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        ReadOnlySpan<string> prefixes = ["re", "res", "enc", "fw", "fwd", "encaminhada", "in"];

        var span = subject.AsSpan().Trim();
        bool stripped;

        do
        {
            stripped = false;

            foreach (var prefix in prefixes)
            {
                if (!span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = span[prefix.Length..];

                // Aceita o contador opcional que alguns clientes inserem: "Re[2]:".
                if (rest.Length > 0 && rest[0] == '[')
                {
                    var close = rest.IndexOf(']');
                    if (close > 0 && int.TryParse(rest[1..close], out _))
                    {
                        rest = rest[(close + 1)..];
                    }
                }

                if (rest.Length == 0 || rest[0] != ':')
                {
                    continue;
                }

                span = rest[1..].TrimStart();
                stripped = true;
                break;
            }
        }
        while (stripped && span.Length > 0);

        return span.Trim().ToString().ToLowerInvariant();
    }
}
