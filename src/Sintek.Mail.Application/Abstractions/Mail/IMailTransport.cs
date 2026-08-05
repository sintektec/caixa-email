using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Abstractions.Mail;

/// <summary>Resultado de um teste de conexão e autenticação.</summary>
/// <param name="Succeeded">Se a conexão e a autenticação funcionaram.</param>
/// <param name="ErrorMessage">Mensagem exibível quando falhou.</param>
/// <param name="IsAuthenticationFailure">
/// Se a falha foi de credencial. Distingue "servidor inacessível" de "senha errada" —
/// a ação que o usuário precisa tomar é diferente em cada caso.
/// </param>
public readonly record struct ConnectionTestResult(
    bool Succeeded,
    string? ErrorMessage,
    bool IsAuthenticationFailure)
{
    /// <summary>Sucesso.</summary>
    public static ConnectionTestResult Success() => new(true, null, false);

    /// <summary>Falha de conexão ou de protocolo.</summary>
    public static ConnectionTestResult Failure(string message) => new(false, message, false);

    /// <summary>Falha de autenticação.</summary>
    public static ConnectionTestResult AuthenticationFailure(string message) => new(false, message, true);
}

/// <summary>Descrição de uma pasta encontrada no servidor.</summary>
/// <param name="RemotePath">Caminho completo no servidor.</param>
/// <param name="Name">Último segmento do caminho.</param>
/// <param name="Delimiter">Separador hierárquico do servidor.</param>
/// <param name="FolderType">Papel inferido a partir dos atributos especiais do IMAP.</param>
/// <param name="IsSubscribed">Se a pasta está assinada.</param>
public readonly record struct RemoteFolder(
    string RemotePath,
    string Name,
    char Delimiter,
    Domain.Enums.FolderType FolderType,
    bool IsSubscribed);

/// <summary>
/// Cliente IMAP: leitura de pastas e mensagens e aplicação de alterações no servidor.
/// </summary>
/// <remarks>
/// Cada implementação atende uma conta e mantém a conexão enquanto for útil. A camada de
/// Aplicação nunca fala IMAP diretamente — é o que permite testar a orquestração de
/// sincronização sem servidor algum.
/// </remarks>
public interface IImapClient : IAsyncDisposable
{
    /// <summary>Se a conexão está aberta e autenticada.</summary>
    bool IsConnected { get; }

    /// <summary>Conecta e autentica.</summary>
    Task<ConnectionTestResult> ConnectAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Encerra a conexão.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Lista as pastas do servidor.</summary>
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Lê o estado de sincronização de uma pasta (UIDVALIDITY, contadores).</summary>
    Task<FolderSyncState> OpenFolderAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca os cabeçalhos das mensagens com UID acima de <paramref name="sinceUid"/>.
    /// </summary>
    Task<IReadOnlyList<FetchedMessage>> FetchHeadersAsync(
        string remotePath, long sinceUid, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca apenas os marcadores que mudaram desde um <c>MODSEQ</c> (CONDSTORE, RFC 7162).
    /// </summary>
    /// <remarks>
    /// É o caminho barato para descobrir o que outra sessão alterou em mensagens antigas:
    /// sem CONDSTORE seria preciso reler os marcadores da pasta inteira a cada ciclo, o
    /// que em uma caixa com dezenas de milhares de mensagens custa mais que todo o resto
    /// da sincronização somado.
    /// </remarks>
    Task<IReadOnlyList<FetchedFlags>> FetchFlagChangesAsync(
        string remotePath, long sinceModSeq, CancellationToken cancellationToken = default);

    /// <summary>Baixa o corpo e os metadados de anexo de uma mensagem.</summary>
    Task<FetchedBody?> FetchBodyAsync(
        string remotePath, long uid, CancellationToken cancellationToken = default);

    /// <summary>Baixa o conteúdo de um anexo.</summary>
    Task<Stream?> FetchAttachmentAsync(
        string remotePath, long uid, string partSpecifier, CancellationToken cancellationToken = default);

    /// <summary>Aplica ou remove marcadores no servidor.</summary>
    Task SetFlagsAsync(
        string remotePath,
        IReadOnlyCollection<long> uids,
        MessageFlagChange change,
        CancellationToken cancellationToken = default);

    /// <summary>Move mensagens entre pastas do servidor.</summary>
    Task<IReadOnlyDictionary<long, long>> MoveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyCollection<long> uids,
        CancellationToken cancellationToken = default);

    /// <summary>Copia mensagens para outra pasta, mantendo-as na origem.</summary>
    Task<IReadOnlyDictionary<long, long>> CopyAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyCollection<long> uids,
        CancellationToken cancellationToken = default);

    /// <summary>Expurga definitivamente as mensagens marcadas para exclusão.</summary>
    Task ExpungeAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Assina ou desassina uma pasta.</summary>
    Task SetSubscriptionAsync(
        string remotePath, bool isSubscribed, CancellationToken cancellationToken = default);

    /// <summary>Indica se o servidor anuncia suporte a IDLE.</summary>
    bool SupportsIdle { get; }

    /// <summary>
    /// Espera por atividade na pasta, devolvendo quando algo mudou ou o tempo esgotar.
    /// </summary>
    /// <remarks>
    /// Com IDLE a espera é passiva e a mensagem nova chega em segundos. Sem ele, a
    /// implementação faz uma sondagem única e devolve — quem chama trata os dois casos da
    /// mesma forma, e é o que permite a um servidor antigo continuar funcionando, apenas
    /// com latência maior.
    /// </remarks>
    /// <returns><see langword="true"/> quando houve mudança; <see langword="false"/> no tempo esgotado.</returns>
    Task<bool> WaitForChangesAsync(
        string remotePath, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Cria uma pasta no servidor.</summary>
    Task CreateFolderAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Renomeia uma pasta no servidor.</summary>
    Task RenameFolderAsync(string remotePath, string newRemotePath, CancellationToken cancellationToken = default);

    /// <summary>Exclui uma pasta no servidor.</summary>
    Task DeleteFolderAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Grava uma mensagem na pasta indicada (APPEND), usado para rascunhos.</summary>
    Task<long?> AppendAsync(
        string remotePath, Stream messageStream, bool isDraft, CancellationToken cancellationToken = default);
}

/// <summary>Estado de sincronização de uma pasta do servidor.</summary>
/// <param name="UidValidity">UIDVALIDITY corrente.</param>
/// <param name="HighestModSeq">HIGHESTMODSEQ, quando o servidor suporta CONDSTORE.</param>
/// <param name="UidNext">Próximo UID que o servidor atribuirá.</param>
/// <param name="TotalCount">Total de mensagens.</param>
/// <param name="UnreadCount">Mensagens não lidas.</param>
public readonly record struct FolderSyncState(
    long UidValidity,
    long? HighestModSeq,
    long UidNext,
    int TotalCount,
    int UnreadCount);

/// <summary>Alteração de marcadores a aplicar no servidor.</summary>
/// <param name="Seen">Marcador de lida, quando informado.</param>
/// <param name="Flagged">Sinalizador, quando informado.</param>
/// <param name="Answered">Marcador de respondida, quando informado.</param>
/// <param name="Deleted">Marcador de exclusão, quando informado.</param>
/// <param name="Junk">
/// Classificação de lixo eletrônico, quando informada. Vira as palavras-chave
/// <c>$Junk</c>/<c>$NotJunk</c> no servidor — é como servidores modernos recebem o
/// treinamento do filtro. Mover a mensagem de pasta sem o marcador faria o servidor
/// continuar classificando errado indefinidamente.
/// </param>
public readonly record struct MessageFlagChange(
    bool? Seen = null,
    bool? Flagged = null,
    bool? Answered = null,
    bool? Deleted = null,
    bool? Junk = null);

/// <summary>Cabeçalhos de uma mensagem trazidos do servidor.</summary>
public sealed record FetchedMessage
{
    /// <summary>UID na pasta.</summary>
    public required long Uid { get; init; }

    /// <summary>MODSEQ, quando disponível.</summary>
    public long? ModSeq { get; init; }

    /// <summary>Message-ID da RFC 5322.</summary>
    public required string MessageId { get; init; }

    /// <summary>Cabeçalho In-Reply-To.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>Cabeçalho References, como veio.</summary>
    public string? References { get; init; }

    /// <summary>Assunto.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Remetente.</summary>
    public string? FromAddress { get; init; }

    /// <summary>Nome exibido do remetente.</summary>
    public string? FromDisplayName { get; init; }

    /// <summary>Participantes, por campo de endereçamento.</summary>
    public IReadOnlyList<FetchedAddress> Addresses { get; init; } = [];

    /// <summary>Data do cabeçalho Date, com fuso preservado.</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>INTERNALDATE do servidor.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Tamanho em bytes.</summary>
    public long Size { get; init; }

    /// <summary>Se a mensagem tem anexos.</summary>
    public bool HasAttachments { get; init; }

    /// <summary>Se está marcada como lida.</summary>
    public bool IsRead { get; init; }

    /// <summary>Se está sinalizada.</summary>
    public bool IsFlagged { get; init; }

    /// <summary>Se é rascunho.</summary>
    public bool IsDraft { get; init; }

    /// <summary>Se já foi respondida.</summary>
    public bool IsAnswered { get; init; }

    /// <summary>Prioridade declarada.</summary>
    public Domain.Enums.MessageImportance Importance { get; init; } = Domain.Enums.MessageImportance.Normal;

    /// <summary>Se o remetente pediu confirmação de leitura.</summary>
    public bool ReadReceiptRequested { get; init; }

    /// <summary>Resultado do SPF apurado pelo servidor de recebimento.</summary>
    public Domain.Enums.AuthenticationResult SpfResult { get; init; }

    /// <summary>Resultado do DKIM.</summary>
    public Domain.Enums.AuthenticationResult DkimResult { get; init; }

    /// <summary>Resultado do DMARC.</summary>
    public Domain.Enums.AuthenticationResult DmarcResult { get; init; }

    /// <summary>Se o servidor classificou a mensagem como lixo eletrônico.</summary>
    public bool IsFlaggedAsSpam { get; init; }

    /// <summary>Pontuação de spam informada pelo servidor, quando houver.</summary>
    public double? SpamScore { get; init; }
}

/// <summary>Um participante trazido do servidor, ainda em texto.</summary>
/// <param name="Kind">Campo de endereçamento.</param>
/// <param name="Address">Endereço.</param>
/// <param name="DisplayName">Nome exibido.</param>
public readonly record struct FetchedAddress(
    Domain.Enums.AddressKind Kind,
    string Address,
    string? DisplayName);

/// <summary>Marcadores de uma mensagem, sem o resto do cabeçalho.</summary>
/// <param name="Uid">UID da mensagem na pasta.</param>
/// <param name="IsRead">Marcador \Seen.</param>
/// <param name="IsFlagged">Marcador \Flagged.</param>
/// <param name="IsAnswered">Marcador \Answered.</param>
/// <param name="ModSeq">MODSEQ da alteração.</param>
public readonly record struct FetchedFlags(
    long Uid,
    bool IsRead,
    bool IsFlagged,
    bool IsAnswered,
    long? ModSeq);

/// <summary>Corpo e metadados de anexo trazidos do servidor.</summary>
public sealed record FetchedBody
{
    /// <summary>Corpo em HTML, como veio. Precisa ser higienizado antes de renderizar.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Corpo em texto puro.</summary>
    public string? TextBody { get; init; }

    /// <summary>Anexos descritos pelo BODYSTRUCTURE.</summary>
    public IReadOnlyList<FetchedAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Documento iCalendar trazido junto, quando a mensagem carrega uma parte
    /// <c>text/calendar</c>.
    /// </summary>
    /// <remarks>
    /// Vem no corpo, e não como anexo a baixar depois: um convite é pequeno, e o usuário
    /// espera vê-lo na agenda ao abrir a mensagem — não depois de clicar em um arquivo.
    /// </remarks>
    public string? CalendarPayload { get; init; }
}

/// <summary>Metadados de um anexo, sem o conteúdo.</summary>
/// <param name="FileName">Nome do arquivo declarado.</param>
/// <param name="ContentType">Tipo MIME.</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="PartSpecifier">Identificador da parte MIME, para baixar sob demanda.</param>
/// <param name="ContentId">Content-ID, quando embutido.</param>
/// <param name="IsInline">Se é embutido no corpo.</param>
public readonly record struct FetchedAttachment(
    string FileName,
    string ContentType,
    long Size,
    string PartSpecifier,
    string? ContentId,
    bool IsInline);
