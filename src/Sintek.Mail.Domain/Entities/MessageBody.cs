using Sintek.Mail.Domain.Common;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Corpo de uma mensagem, em tabela separada para não pesar na listagem.
/// </summary>
/// <remarks>
/// <see cref="SanitizedHtml"/> guarda o resultado da higienização e é o <b>único</b>
/// conteúdo que pode chegar ao WebView2. <see cref="HtmlBody"/> preserva o original
/// apenas para reprocessar caso as regras de sanitização mudem — nunca para exibir.
/// </remarks>
public sealed class MessageBody : Entity
{
    private MessageBody(Guid id, Guid messageId, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        MessageId = messageId;
    }

    private MessageBody()
    {
    }

    /// <summary>Mensagem dona deste corpo.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Mensagem dona deste corpo.</summary>
    public Message? Message { get; private set; }

    /// <summary>HTML original, como veio do servidor. Nunca renderizar diretamente.</summary>
    public string? HtmlBody { get; private set; }

    /// <summary>Corpo em texto puro.</summary>
    public string? TextBody { get; private set; }

    /// <summary>HTML já higienizado — o único apto a ser renderizado.</summary>
    public string? SanitizedHtml { get; private set; }

    /// <summary>
    /// Se o HTML referencia recursos externos (imagens, folhas de estilo). Quando
    /// verdadeiro, a interface exibe a barra "Exibir imagens" e só libera o carregamento
    /// depois que o usuário concordar.
    /// </summary>
    public bool HasRemoteContent { get; private set; }

    /// <summary>Se o usuário autorizou o conteúdo remoto desta mensagem.</summary>
    public bool RemoteContentAllowed { get; private set; }

    /// <summary>Instante em que o corpo foi baixado.</summary>
    public DateTimeOffset? DownloadedAt { get; private set; }

    /// <summary>Cria um corpo vazio, ainda não baixado.</summary>
    public static MessageBody Create(Guid messageId, DateTimeOffset createdAt, Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), messageId, createdAt);

    /// <summary>Grava o conteúdo baixado e já higienizado.</summary>
    public void SetContent(
        string? htmlBody,
        string? textBody,
        string? sanitizedHtml,
        bool hasRemoteContent,
        DateTimeOffset now)
    {
        HtmlBody = htmlBody;
        TextBody = textBody;
        SanitizedHtml = sanitizedHtml;
        HasRemoteContent = hasRemoteContent;
        DownloadedAt = now;
        Touch(now);
    }

    /// <summary>Registra a autorização do usuário para carregar o conteúdo remoto.</summary>
    public void AllowRemoteContent(DateTimeOffset now)
    {
        RemoteContentAllowed = true;
        Touch(now);
    }
}
