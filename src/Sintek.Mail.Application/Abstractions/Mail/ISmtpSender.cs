using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Abstractions.Mail;

/// <summary>Uma mensagem pronta para envio.</summary>
public sealed record OutgoingMessage
{
    /// <summary>Remetente.</summary>
    public required string From { get; init; }

    /// <summary>Nome exibido do remetente.</summary>
    public string? FromDisplayName { get; init; }

    /// <summary>Destinatários diretos.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Destinatários em cópia.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Destinatários em cópia oculta.</summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>Endereço de resposta, quando difere do remetente.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Assunto.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Corpo em HTML.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Corpo em texto puro, enviado como alternativa.</summary>
    public string? TextBody { get; init; }

    /// <summary>Anexos, por caminho no disco.</summary>
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = [];

    /// <summary>Message-ID ao qual esta mensagem responde.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>Cadeia References da conversa.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Prioridade declarada.</summary>
    public MessageImportance Importance { get; init; } = MessageImportance.Normal;

    /// <summary>
    /// Documento iCalendar a enviar como parte <c>text/calendar</c>, quando houver.
    /// </summary>
    /// <remarks>
    /// Parte, e não anexo: é o que faz o cliente do destinatário processar a resposta
    /// sozinho — atualizar o <c>PARTSTAT</c> na agenda dele — em vez de mostrar um arquivo
    /// para ele abrir à mão.
    /// </remarks>
    public OutgoingCalendarPart? Calendar { get; init; }

    /// <summary>Se deve pedir confirmação de leitura.</summary>
    public bool RequestReadReceipt { get; init; }
}

/// <summary>Um anexo a ser enviado.</summary>
/// <param name="FileName">Nome exibido ao destinatário.</param>
/// <param name="FilePath">Caminho do arquivo no disco.</param>
/// <param name="ContentType">Tipo MIME.</param>
/// <param name="ContentId">Content-ID, quando embutido no corpo.</param>
public readonly record struct OutgoingAttachment(
    string FileName,
    string FilePath,
    string? ContentType = null,
    string? ContentId = null);

/// <summary>Resultado de um envio.</summary>
/// <param name="Succeeded">Se a mensagem foi aceita pelo servidor.</param>
/// <param name="MessageId">Message-ID atribuído.</param>
/// <param name="ErrorMessage">Mensagem exibível quando falhou.</param>
/// <param name="IsPermanentFailure">
/// Se a recusa é definitiva (endereço inexistente, mensagem grande demais). Nesse caso a
/// fila não deve tentar de novo.
/// </param>
public readonly record struct SendResult(
    bool Succeeded,
    string? MessageId,
    string? ErrorMessage,
    bool IsPermanentFailure);

/// <summary>Envia mensagens via SMTP.</summary>
public interface ISmtpSender
{
    /// <summary>Envia uma mensagem pela conta indicada.</summary>
    Task<SendResult> SendAsync(
        Account account, OutgoingMessage message, CancellationToken cancellationToken = default);

    /// <summary>Testa conexão e autenticação sem enviar nada.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        Account account, CancellationToken cancellationToken = default);
}

/// <summary>Serializa uma mensagem no formato MIME da RFC 5322.</summary>
/// <remarks>
/// Abstraída porque o <c>APPEND</c> do IMAP precisa exatamente do mesmo documento que o
/// envio SMTP produz. Uma segunda serialização escrita à parte faria a cópia em Itens
/// Enviados divergir do que o destinatário recebeu.
/// </remarks>
public interface IMimeMessageWriter
{
    /// <summary>Escreve a mensagem em um fluxo posicionado no início.</summary>
    Task<Stream> WriteAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Parte iCalendar de uma mensagem de saída.</summary>
/// <param name="Method">Valor do parâmetro <c>method</c> — <c>REQUEST</c>, <c>REPLY</c>…</param>
/// <param name="Content">Documento iCalendar completo.</param>
public readonly record struct OutgoingCalendarPart(string Method, string Content);
