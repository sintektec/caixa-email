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

/// <summary>Configuração de servidores descoberta automaticamente.</summary>
/// <param name="ImapHost">Servidor IMAP.</param>
/// <param name="ImapPort">Porta IMAP.</param>
/// <param name="ImapSecurity">Modo de proteção do IMAP.</param>
/// <param name="SmtpHost">Servidor SMTP.</param>
/// <param name="SmtpPort">Porta SMTP.</param>
/// <param name="SmtpSecurity">Modo de proteção do SMTP.</param>
/// <param name="RecommendedAuthentication">Autenticação recomendada pelo provedor.</param>
/// <param name="OAuthProvider">Provedor de identidade, quando OAuth é recomendado.</param>
public readonly record struct DiscoveredServerSettings(
    string ImapHost,
    int ImapPort,
    SecureSocketMode ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    SecureSocketMode SmtpSecurity,
    AuthenticationType RecommendedAuthentication,
    OAuthProviderKind OAuthProvider);

/// <summary>
/// Descobre a configuração de servidores a partir do endereço de e-mail.
/// </summary>
/// <remarks>
/// A especificação exige configuração automática além da manual. A descoberta tenta, em
/// ordem: provedores conhecidos (Gmail, Microsoft 365), registros SRV do DNS conforme a
/// RFC 6186 e, por último, as convenções usuais (<c>imap.dominio</c>,
/// <c>mail.dominio</c>).
/// </remarks>
public interface IAutodiscoverService
{
    /// <summary>
    /// Descobre a configuração para o endereço informado, ou devolve
    /// <see langword="null"/> quando nada foi encontrado e o usuário precisa configurar
    /// manualmente.
    /// </summary>
    Task<DiscoveredServerSettings?> DiscoverAsync(
        string emailAddress, CancellationToken cancellationToken = default);
}
