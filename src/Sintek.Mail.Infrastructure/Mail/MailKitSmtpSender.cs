using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>Envio de mensagens via SMTP com MailKit.</summary>
public sealed class MailKitSmtpSender : ISmtpSender
{
    private readonly MailKitAuthenticator _authenticator;
    private readonly ILogger<MailKitSmtpSender> _logger;

    public MailKitSmtpSender(MailKitAuthenticator authenticator, ILogger<MailKitSmtpSender> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SendResult> SendAsync(
        Account account, OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var client = new SmtpClient();

        var connection = await _authenticator.ConnectSmtpAsync(client, account, cancellationToken)
            .ConfigureAwait(false);

        if (!connection.Succeeded)
        {
            // Falha de autenticação não é permanente no sentido da fila: reautenticar
            // resolve. Já uma falha de conexão merece nova tentativa mais tarde.
            return new SendResult(false, null, connection.ErrorMessage, IsPermanentFailure: false);
        }

        try
        {
            var mime = BuildMimeMessage(account, message);
            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Mensagem enviada pela conta {AccountId}.", account.Id);
            return new SendResult(true, mime.MessageId, null, false);
        }
        catch (SmtpCommandException ex)
        {
            // Códigos 5xx são recusas definitivas: destinatário inexistente, mensagem
            // grande demais, remetente bloqueado. Repetir só gastaria tentativas.
            var isPermanent = ex.StatusCode is >= SmtpStatusCode.CommandUnrecognized;

            _logger.LogWarning(
                "Envio recusado pelo servidor SMTP com código {StatusCode}.", ex.StatusCode);

            return new SendResult(false, null, ex.Message, isPermanent);
        }
        catch (SmtpProtocolException ex)
        {
            return new SendResult(false, null, ex.Message, IsPermanentFailure: false);
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestConnectionAsync(
        Account account, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient();

        var result = await _authenticator.ConnectSmtpAsync(client, account, cancellationToken)
            .ConfigureAwait(false);

        if (client.IsConnected)
        {
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static MimeMessage BuildMimeMessage(Account account, OutgoingMessage message)
    {
        var mime = new MimeMessage();

        mime.From.Add(new MailboxAddress(
            message.FromDisplayName ?? account.DisplayName, message.From));

        AddRecipients(mime.To, message.To);
        AddRecipients(mime.Cc, message.Cc);
        AddRecipients(mime.Bcc, message.Bcc);

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        mime.Subject = message.Subject;

        // Preserva a cadeia da conversa: sem estes cabeçalhos, a resposta aparece como
        // mensagem solta na caixa do destinatário.
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            mime.InReplyTo = message.InReplyTo;
        }

        foreach (var reference in message.References)
        {
            mime.References.Add(reference);
        }

        if (message.Importance != Domain.Enums.MessageImportance.Normal)
        {
            mime.Importance = message.Importance == Domain.Enums.MessageImportance.High
                ? MimeKit.MessageImportance.High
                : MimeKit.MessageImportance.Low;
        }

        if (message.RequestReadReceipt)
        {
            // A confirmação de leitura é um pedido, não uma garantia: o cliente do
            // destinatário decide se a envia, e a maioria pergunta antes.
            mime.Headers.Add(HeaderId.DispositionNotificationTo, message.From);
        }

        var builder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        };

        foreach (var attachment in message.Attachments)
        {
            if (!File.Exists(attachment.FilePath))
            {
                throw new FileNotFoundException(
                    $"O anexo '{attachment.FileName}' não foi encontrado.", attachment.FilePath);
            }

            var entity = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? builder.Attachments.Add(attachment.FilePath)
                : builder.Attachments.Add(attachment.FilePath, ContentType.Parse(attachment.ContentType));

            entity.ContentDisposition ??= new ContentDisposition(ContentDisposition.Attachment);
            entity.ContentDisposition.FileName = attachment.FileName;

            if (!string.IsNullOrWhiteSpace(attachment.ContentId))
            {
                entity.ContentId = attachment.ContentId;
                entity.ContentDisposition.Disposition = ContentDisposition.Inline;
            }
        }

        mime.Body = CalendarPartBuilder.Build(builder, message.Calendar);
        return mime;
    }

    private static void AddRecipients(InternetAddressList list, IReadOnlyList<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                list.Add(MailboxAddress.Parse(address));
            }
        }
    }
}
