using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>
/// Cliente IMAP baseado em MailKit.
/// </summary>
/// <remarks>
/// Uma instância atende uma conta e mantém a conexão aberta enquanto for útil: reconectar
/// e reautenticar a cada operação multiplicaria a latência de cada sincronização e, em
/// provedores com limite de conexões, levaria a bloqueio temporário.
/// </remarks>
public sealed class MailKitImapClient : Application.Abstractions.Mail.IImapClient
{
    private readonly MailKitAuthenticator _authenticator;
    private readonly ILogger<MailKitImapClient> _logger;
    private readonly ImapClient _client = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IMailFolder? _openFolder;

    /// <summary>Cancela o IDLE em curso; nulo quando não há nenhum.</summary>
    private CancellationTokenSource? _idle;

    public MailKitImapClient(MailKitAuthenticator authenticator, ILogger<MailKitImapClient> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConnected => _client.IsConnected && _client.IsAuthenticated;

    /// <summary>
    /// Toma a conexão para uma operação, interrompendo o IDLE se houver um em curso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>O <c>ImapClient</c> do MailKit não aceita dois comandos ao mesmo tempo</b>, e a
    /// segunda chamada morre com "The ImapClient is currently busy processing a command in
    /// another thread". Neste aplicativo isso é rotina, não caso raro: o laço de
    /// sincronização roda em segundo plano enquanto a pessoa clica numa mensagem, e as duas
    /// coisas caem na mesma conexão. Pior, comandos intercalados deixam a pasta selecionada
    /// incoerente, e aí um <c>FETCH</c> por UID falha com <c>MessageNotFoundException</c> —
    /// um sintoma que não se parece nada com a causa.
    /// </para>
    /// <para>
    /// <b>Serializar sozinho não bastaria.</b> O IDLE segura a conexão por até 29 minutos; um
    /// clique atrás dele esperaria meia hora, que é o mesmo travamento com outro nome. Por
    /// isso quem chega cancela o IDLE em curso antes de pedir a vez: o IDLE trata cancelamento
    /// como fim normal, devolve o que já viu, e o agendador entra de novo na rodada seguinte.
    /// </para>
    /// </remarks>
    private async Task<Guard> LockAsync(CancellationToken cancellationToken)
    {
        // Fora da ordem não funciona: pedir a vez antes de cancelar deixaria quem chega
        // esperando o IDLE inteiro, que é justamente o que se quer evitar.
        try
        {
            _idle?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // O IDLE terminou entre a leitura do campo e o cancelamento. Não há o que
            // interromper, e é o resultado desejado de qualquer forma.
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Guard(_gate);
    }

    /// <summary>Devolve a vez ao sair do escopo.</summary>
    private readonly struct Guard(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> ConnectAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);

        if (IsConnected)
        {
            return ConnectionTestResult.Success();
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }

        return await _authenticator.ConnectImapAsync(_client, account, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }

        _openFolder = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();

        var folders = new List<RemoteFolder>();

        foreach (var ns in _client.PersonalNamespaces)
        {
            var root = await _client.GetFolderAsync(ns.Path, cancellationToken).ConfigureAwait(false);

            foreach (var folder in await root.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false))
            {
                await CollectFoldersAsync(folder, folders, cancellationToken).ConfigureAwait(false);
            }
        }

        return folders;
    }

    private static async Task CollectFoldersAsync(
        IMailFolder folder, List<RemoteFolder> accumulator, CancellationToken cancellationToken)
    {
        accumulator.Add(new RemoteFolder(
            folder.FullName,
            folder.Name,
            folder.DirectorySeparator,
            MapFolderType(folder),
            folder.IsSubscribed));

        // \Noinferiors avisa que a pasta não pode ter filhas; consultá-las provocaria um
        // erro do servidor.
        if (folder.Attributes.HasFlag(FolderAttributes.NoInferiors))
        {
            return;
        }

        foreach (var child in await folder.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false))
        {
            await CollectFoldersAsync(child, accumulator, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Infere o papel da pasta pelos atributos especiais do IMAP (RFC 6154).
    /// </summary>
    /// <remarks>
    /// Os atributos são o único jeito confiável: o nome varia com o idioma do servidor —
    /// "Sent", "Itens Enviados", "Enviados" e "Gesendete Objekte" são todos a mesma pasta.
    /// </remarks>
    private static FolderType MapFolderType(IMailFolder folder)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.Inbox))
        {
            return FolderType.Inbox;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Sent))
        {
            return FolderType.Sent;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Drafts))
        {
            return FolderType.Drafts;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Trash))
        {
            return FolderType.Trash;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Junk))
        {
            return FolderType.Junk;
        }

        if (folder.Attributes.HasFlag(FolderAttributes.Archive))
        {
            return FolderType.Archive;
        }

        return FolderType.Custom;
    }

    /// <inheritdoc />
    public async Task<FolderSyncState> OpenFolderAsync(
        string remotePath, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);

        return new FolderSyncState(
            folder.UidValidity,
            folder.Supports(FolderFeature.ModSequences) ? (long)folder.HighestModSeq : null,
            folder.UidNext?.Id ?? 0,
            folder.Count,
            folder.Unread);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedMessage>> FetchHeadersAsync(
        string remotePath, long sinceUid, int limit, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);

        // Busca por faixa de UID, não por índice: índices mudam quando qualquer mensagem
        // é expurgada, e a sincronização perderia o ponto de partida.
        var range = new UniqueIdRange(new UniqueId((uint)Math.Max(sinceUid + 1, 1)), UniqueId.MaxValue);

        const MessageSummaryItems items =
            MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate
            | MessageSummaryItems.Size
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.Headers;

        var summaries = await folder.FetchAsync(range, items, cancellationToken).ConfigureAwait(false);

        return summaries
            .OrderBy(s => s.UniqueId.Id)
            .Take(limit)
            .Select(ToFetchedMessage)
            .ToList();
    }

    private static FetchedMessage ToFetchedMessage(IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault();

        var addresses = new List<FetchedAddress>();
        AppendAddresses(addresses, envelope?.From, AddressKind.From);
        AppendAddresses(addresses, envelope?.Sender, AddressKind.Sender);
        AppendAddresses(addresses, envelope?.To, AddressKind.To);
        AppendAddresses(addresses, envelope?.Cc, AddressKind.Cc);
        AppendAddresses(addresses, envelope?.Bcc, AddressKind.Bcc);
        AppendAddresses(addresses, envelope?.ReplyTo, AddressKind.ReplyTo);

        var verdict = ReadServerVerdict(summary.Headers);

        return new FetchedMessage
        {
            Uid = summary.UniqueId.Id,
            ModSeq = summary.ModSeq.HasValue ? (long)summary.ModSeq.Value : null,

            // Um Message-ID ausente é raro mas acontece com remetentes malfeitos; sem um
            // valor estável a deduplicação falharia, então sintetizamos a partir do UID.
            MessageId = envelope?.MessageId ?? $"<uid-{summary.UniqueId.Id}@sintek.local>",
            InReplyTo = envelope?.InReplyTo,
            References = summary.References is { Count: > 0 } ? string.Join(' ', summary.References) : null,
            Subject = envelope?.Subject ?? string.Empty,
            FromAddress = from?.Address,
            FromDisplayName = from?.Name,
            Addresses = addresses,

            // O cabeçalho Date pode vir ausente ou absurdo; a data interna do servidor é
            // a referência confiável para ordenar a caixa.
            SentAt = envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.UnixEpoch,
            ReceivedAt = summary.InternalDate ?? envelope?.Date ?? DateTimeOffset.UnixEpoch,
            Size = summary.Size ?? 0,
            HasAttachments = summary.Attachments.Any(),
            IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            IsFlagged = summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            IsDraft = summary.Flags?.HasFlag(MessageFlags.Draft) ?? false,
            IsAnswered = summary.Flags?.HasFlag(MessageFlags.Answered) ?? false,
            Importance = MapImportance(summary),
            ReadReceiptRequested = summary.Headers?.Contains(HeaderId.DispositionNotificationTo) ?? false,
            SpfResult = verdict.Spf,
            DkimResult = verdict.Dkim,
            DmarcResult = verdict.Dmarc,
            IsFlaggedAsSpam = verdict.IsFlaggedAsSpam,
            SpamScore = verdict.SpamScore,
        };
    }

    /// <summary>
    /// Lê o veredito do servidor a partir dos cabeçalhos.
    /// </summary>
    /// <remarks>
    /// Nada é reverificado aqui. SPF, DKIM e DMARC dependem de consultar o DNS no instante em
    /// que a mensagem chegou; refazer isso dias depois daria resultado diferente e errado.
    /// </remarks>
    private static ServerVerdict ReadServerVerdict(HeaderList? headers)
        => headers is null
            ? default
            : AuthenticationResultsParser.Parse(
                headers[HeaderId.AuthenticationResults],
                headers["X-Spam-Flag"],
                headers["X-Spam-Status"],
                headers["X-Spam-Score"]);

    private static void AppendAddresses(
        List<FetchedAddress> accumulator, InternetAddressList? list, AddressKind kind)
    {
        if (list is null)
        {
            return;
        }

        foreach (var mailbox in list.Mailboxes)
        {
            accumulator.Add(new FetchedAddress(kind, mailbox.Address, mailbox.Name));
        }
    }

    private static Domain.Enums.MessageImportance MapImportance(IMessageSummary summary)
    {
        var priority = summary.Headers?[HeaderId.Importance] ?? summary.Headers?[HeaderId.XPriority];

        if (string.IsNullOrWhiteSpace(priority))
        {
            return Domain.Enums.MessageImportance.Normal;
        }

        if (priority.Contains("high", StringComparison.OrdinalIgnoreCase) || priority.StartsWith('1'))
        {
            return Domain.Enums.MessageImportance.High;
        }

        if (priority.Contains("low", StringComparison.OrdinalIgnoreCase) || priority.StartsWith('5'))
        {
            return Domain.Enums.MessageImportance.Low;
        }

        return Domain.Enums.MessageImportance.Normal;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<IReadOnlyList<FetchedFlags>> FetchFlagChangesAsync(
        string remotePath, long sinceModSeq, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);

        // Servidor sem CONDSTORE não tem o que responder aqui. Devolver vazio deixa o
        // chamador seguir com a reconciliação por UID, que é correta e mais cara.
        if (!folder.Supports(FolderFeature.ModSequences))
        {
            return [];
        }

        const MessageSummaryItems items =
            MessageSummaryItems.UniqueId
            | MessageSummaryItems.Flags
            | MessageSummaryItems.ModSeq;

        // A sobrecarga com modseq é o CHANGEDSINCE da RFC 7162: o servidor devolve apenas
        // o que mudou, e não a pasta inteira.
        var summaries = await folder
            .FetchAsync(0, -1, (ulong)Math.Max(sinceModSeq, 0), items, cancellationToken)
            .ConfigureAwait(false);

        return summaries
            .Where(s => s.UniqueId.Id > 0)
            .Select(s => new FetchedFlags(
                s.UniqueId.Id,
                s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                s.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                s.Flags?.HasFlag(MessageFlags.Answered) ?? false,
                s.ModSeq.HasValue ? (long)s.ModSeq.Value : null))
            .ToList();
    }

    public async Task<FetchedBody?> FetchBodyAsync(
        string remotePath, long uid, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);

        MimeMessage? message;

        try
        {
            message = await folder
                .GetMessageAsync(new UniqueId((uint)uid), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MessageNotFoundException)
        {
            // A mensagem saiu do servidor entre a listagem e o download — apagada por outro
            // cliente, movida, ou expurgada. É rotina em caixa compartilhada, não defeito, e
            // derrubar a leitura por isso trocaria uma linha que sumiu por uma tela de erro.
            _logger.LogInformation(
                "A mensagem {Uid} não está mais em {RemotePath}.", uid, remotePath);

            return null;
        }

        if (message is null)
        {
            return null;
        }

        var attachments = new List<FetchedAttachment>();
        var index = 0;

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            index++;
            var isInline = part.ContentDisposition?.Disposition == ContentDisposition.Inline
                || !string.IsNullOrEmpty(part.ContentId);

            if (!part.IsAttachment && !isInline)
            {
                continue;
            }

            attachments.Add(new FetchedAttachment(
                part.FileName ?? $"parte-{index}",
                part.ContentType.MimeType,
                part.Content?.Stream?.Length ?? 0,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                part.ContentId,
                isInline));
        }

        return new FetchedBody
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
            Attachments = attachments,
            CalendarPayload = ExtractCalendarPayload(message),
        };
    }

    /// <summary>
    /// Devolve o documento iCalendar da mensagem, se houver.
    /// </summary>
    /// <remarks>
    /// Teams, Outlook e Meet mandam o convite como parte <c>text/calendar</c> dentro de um
    /// <c>multipart/alternative</c>; alguns repetem o mesmo documento como anexo
    /// <c>.ics</c>. A primeira parte encontrada basta — são o mesmo conteúdo.
    /// </remarks>
    private static string? ExtractCalendarPayload(MimeMessage message)
    {
        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            if (!part.ContentType.IsMimeType("text", "calendar"))
            {
                continue;
            }

            var text = part.Text;

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<Stream?> FetchAttachmentAsync(
        string remotePath, long uid, string partSpecifier, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);

        var bodyPart = new BodyPartBasic(new ContentType("application", "octet-stream"), partSpecifier)
        {
            PartSpecifier = partSpecifier,
        };

        var entity = await folder
            .GetBodyPartAsync(new UniqueId((uint)uid), bodyPart, cancellationToken)
            .ConfigureAwait(false);

        // Content nulo significa parte sem conteúdo carregado — devolver um fluxo vazio
        // enganaria quem chama, que gravaria um anexo de zero byte como se fosse válido.
        if (entity is not MimePart { Content: not null } part)
        {
            return null;
        }

        var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        return buffer;
    }

    /// <inheritdoc />
    public async Task SetFlagsAsync(
        string remotePath,
        IReadOnlyCollection<long> uids,
        MessageFlagChange change,
        CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(uids);

        if (uids.Count == 0)
        {
            return;
        }

        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);
        var ids = uids.Select(u => new UniqueId((uint)u)).ToList();

        var toAdd = MessageFlags.None;
        var toRemove = MessageFlags.None;

        Accumulate(change.Seen, MessageFlags.Seen, ref toAdd, ref toRemove);
        Accumulate(change.Flagged, MessageFlags.Flagged, ref toAdd, ref toRemove);
        Accumulate(change.Answered, MessageFlags.Answered, ref toAdd, ref toRemove);
        Accumulate(change.Deleted, MessageFlags.Deleted, ref toAdd, ref toRemove);

        if (change.Junk is { } junk)
        {
            // $Junk/$NotJunk são mutuamente exclusivas: aplicar uma sem remover a outra
            // deixaria a mensagem com os dois vereditos ao mesmo tempo.
            var toApply = new HashSet<string> { junk ? "$Junk" : "$NotJunk" };
            var toClear = new HashSet<string> { junk ? "$NotJunk" : "$Junk" };

            await folder.AddFlagsAsync(ids, MessageFlags.None, toApply, silent: true, cancellationToken)
                .ConfigureAwait(false);
            await folder.RemoveFlagsAsync(ids, MessageFlags.None, toClear, silent: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (toAdd != MessageFlags.None)
        {
            await folder.AddFlagsAsync(ids, toAdd, silent: true, cancellationToken).ConfigureAwait(false);
        }

        if (toRemove != MessageFlags.None)
        {
            await folder.RemoveFlagsAsync(ids, toRemove, silent: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Accumulate(
        bool? desired, MessageFlags flag, ref MessageFlags toAdd, ref MessageFlags toRemove)
    {
        switch (desired)
        {
            case true:
                toAdd |= flag;
                break;
            case false:
                toRemove |= flag;
                break;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, long>> MoveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyCollection<long> uids,
        CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(uids);

        if (uids.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        var source = await OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var destination = await _client.GetFolderAsync(destinationPath, cancellationToken).ConfigureAwait(false);

        var ids = uids.Select(u => new UniqueId((uint)u)).ToList();
        var moved = await source.MoveToAsync(ids, destination, cancellationToken).ConfigureAwait(false);

        // O servidor devolve o mapeamento de UIDs apenas quando suporta UIDPLUS. Sem ele,
        // as mensagens são localizadas na pasta de destino pelo Message-ID, na próxima
        // sincronização.
        var result = new Dictionary<long, long>();
        foreach (var pair in moved)
        {
            result[pair.Key.Id] = pair.Value.Id;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, long>> CopyAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyCollection<long> uids,
        CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(uids);

        if (uids.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        var source = await OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var destination = await _client.GetFolderAsync(destinationPath, cancellationToken).ConfigureAwait(false);

        var ids = uids.Select(u => new UniqueId((uint)u)).ToList();
        var copied = await source.CopyToAsync(ids, destination, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<long, long>();
        foreach (var pair in copied)
        {
            result[pair.Key.Id] = pair.Value.Id;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task ExpungeAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);
        await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetSubscriptionAsync(
        string remotePath, bool isSubscribed, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();

        var folder = await _client.GetFolderAsync(remotePath, cancellationToken).ConfigureAwait(false);

        if (isSubscribed)
        {
            await folder.SubscribeAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await folder.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool SupportsIdle => _client.IsConnected && _client.Capabilities.HasFlag(ImapCapabilities.Idle);

    /// <inheritdoc />
    /// <remarks>
    /// Com IDLE a espera é passiva e o servidor avisa em segundos. Sem ele, a alternativa
    /// honesta é devolver imediatamente e deixar o agendador sondar: fingir uma espera com
    /// <c>Task.Delay</c> daria a impressão de recebimento imediato onde não há.
    /// </remarks>
    public async Task<bool> WaitForChangesAsync(
        string remotePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // O teto de 29 minutos vem da RFC 2177: passado esse tempo, muitos servidores e
        // intermediários derrubam a conexão em silêncio, e o cliente ficaria esperando um
        // aviso que nunca viria.
        var effective = timeout > TimeSpan.FromMinutes(29) ? TimeSpan.FromMinutes(29) : timeout;

        using var deadline = new CancellationTokenSource(effective);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);

        // Publicado **antes** de pedir a vez, e esta ordem é o ponto. Publicar depois deixa
        // uma janela entre tomar o cadeado e anunciar a espera: quem chega ali encontra o
        // campo nulo, não cancela nada, e fica atrás de até 29 minutos de IDLE — travamento
        // com outro nome, e foi exatamente o que sobrou da primeira tentativa de conserto.
        // Publicar antes é seguro porque cancelar uma espera que ainda não começou apenas
        // faz o IDLE terminar assim que começar.
        _idle = linked;

        try
        {
            using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
            EnsureConnected();

            if (!SupportsIdle)
            {
                return false;
            }

            var folder = await OpenAsync(remotePath, cancellationToken).ConfigureAwait(false);
            var changed = false;

            void OnCountChanged(object? sender, EventArgs e) => changed = true;
            void OnFlagsChanged(object? sender, MessageFlagsChangedEventArgs e) => changed = true;

            folder.CountChanged += OnCountChanged;
            folder.MessageFlagsChanged += OnFlagsChanged;

            try
            {
                await _client.IdleAsync(linked.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fim normal de um IDLE: o tempo esgotou, ou alguém precisou da conexão. Nos
                // dois casos o que já foi observado vale, e o agendador entra de novo na
                // rodada seguinte.
            }
            finally
            {
                folder.CountChanged -= OnCountChanged;
                folder.MessageFlagsChanged -= OnFlagsChanged;
            }

            return changed;
        }
        finally
        {
            _idle = null;
        }
    }

    /// <inheritdoc />
    public async Task CreateFolderAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();

        var separator = _client.PersonalNamespaces[0].DirectorySeparator;
        var segments = remotePath.Split(separator);

        // O MailKit anota estes retornos como possivelmente nulos. Um namespace pessoal
        // ausente é uma condição real (servidor fora do padrão) e merece erro explícito,
        // não um NullReferenceException mais adiante.
        var current = await _client
            .GetFolderAsync(_client.PersonalNamespaces[0].Path, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "O servidor IMAP não expôs o namespace pessoal; não é possível criar pastas.");

        // Cria os níveis intermediários que faltarem: o IMAP não cria hierarquia
        // implicitamente, e uma pasta aninhada falharia se o pai não existisse.
        foreach (var segment in segments)
        {
            IMailFolder? existing = null;

            foreach (var child in await current.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    existing = child;
                    break;
                }
            }

            current = existing
                ?? await current.CreateAsync(segment, true, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"O servidor recusou a criação da pasta '{segment}'.");
        }
    }

    /// <inheritdoc />
    public async Task RenameFolderAsync(
        string remotePath, string newRemotePath, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();

        var folder = await _client.GetFolderAsync(remotePath, cancellationToken).ConfigureAwait(false);
        var separator = folder.DirectorySeparator;
        var lastSeparator = newRemotePath.LastIndexOf(separator);

        var parentPath = lastSeparator > 0 ? newRemotePath[..lastSeparator] : _client.PersonalNamespaces[0].Path;
        var newName = lastSeparator > 0 ? newRemotePath[(lastSeparator + 1)..] : newRemotePath;

        var parent = await _client.GetFolderAsync(parentPath, cancellationToken).ConfigureAwait(false);
        await folder.RenameAsync(parent, newName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteFolderAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        EnsureConnected();

        var folder = await _client.GetFolderAsync(remotePath, cancellationToken).ConfigureAwait(false);
        await folder.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long?> AppendAsync(
        string remotePath, Stream messageStream, bool isDraft, CancellationToken cancellationToken = default)
    {
        using var guard = await LockAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(messageStream);

        EnsureConnected();

        var folder = await _client.GetFolderAsync(remotePath, cancellationToken).ConfigureAwait(false);
        var message = await MimeMessage.LoadAsync(messageStream, cancellationToken).ConfigureAwait(false);

        var flags = isDraft ? MessageFlags.Draft | MessageFlags.Seen : MessageFlags.Seen;
        var uid = await folder.AppendAsync(message, flags, cancellationToken).ConfigureAwait(false);

        return uid?.Id;
    }

    private async Task<IMailFolder> OpenAsync(string remotePath, CancellationToken cancellationToken)
    {
        EnsureConnected();

        // Reabrir a pasta já aberta custaria uma ida e volta ao servidor por operação —
        // caro em um laço de sincronização que processa a mesma pasta muitas vezes.
        if (_openFolder is not null
            && string.Equals(_openFolder.FullName, remotePath, StringComparison.Ordinal)
            && _openFolder.IsOpen)
        {
            return _openFolder;
        }

        var folder = await _client.GetFolderAsync(remotePath, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

        _openFolder = folder;
        return folder;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "O cliente IMAP não está conectado. Chame ConnectAsync antes de qualquer operação.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Falha ao desconectar não pode escapar de Dispose e mascarar a exceção real
            // que levou ao descarte.
            _logger.LogDebug(ex, "Falha ao desconectar o cliente IMAP durante o descarte.");
        }

        _client.Dispose();
        _gate.Dispose();
    }
}
