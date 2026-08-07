using MimeKit;
using MimeKit.Utils;
using Sintek.Mail.Application.Abstractions.Mail;

// A prioridade existe nos dois mundos com o mesmo nome; o apelido diz de qual se fala.
using DomainImportance = Sintek.Mail.Domain.Enums.MessageImportance;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>Escreve uma mensagem no formato MIME da RFC 5322.</summary>
/// <remarks>
/// O mesmo documento serve ao envio por SMTP e à gravação em Itens Enviados por
/// <c>APPEND</c>. Serializar duas vezes, por caminhos diferentes, faria a cópia guardada
/// divergir do que o destinatário recebeu — e a divergência só apareceria meses depois,
/// numa discussão sobre o que exatamente foi enviado.
/// </remarks>
public sealed class MimeMessageWriter : IMimeMessageWriter
{
    /// <inheritdoc />
    public async Task<Stream> WriteAsync(
        OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        var mime = Compose(message);
        var stream = new MemoryStream();

        await mime.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;

        return stream;
    }

    /// <summary>Monta a mensagem MIME.</summary>
    public static MimeMessage Compose(OutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromDisplayName ?? message.From, message.From));

        AddAll(mime.To, message.To);
        AddAll(mime.Cc, message.Cc);
        AddAll(mime.Bcc, message.Bcc);

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        mime.Subject = message.Subject;
        mime.MessageId = MimeUtils.GenerateMessageId();

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            mime.InReplyTo = message.InReplyTo;
        }

        foreach (var reference in message.References)
        {
            mime.References.Add(reference);
        }

        var builder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        };

        foreach (var attachment in message.Attachments)
        {
            var entity = builder.Attachments.Add(attachment.FilePath);

            if (!string.IsNullOrWhiteSpace(attachment.FileName))
            {
                entity.ContentDisposition ??= new ContentDisposition(ContentDisposition.Attachment);
                entity.ContentDisposition.FileName = attachment.FileName;
            }

            // Content-ID presente significa imagem embutida no corpo: ela precisa ser
            // "inline", senão o cliente do destinatário a lista como anexo solto e o
            // <img src="cid:..."> não encontra nada.
            if (!string.IsNullOrWhiteSpace(attachment.ContentId))
            {
                entity.ContentId = attachment.ContentId;
                entity.ContentDisposition!.Disposition = ContentDisposition.Inline;
            }
        }

        mime.Body = CalendarPartBuilder.Build(builder, message.Calendar);

        ApplyImportance(mime, message.Importance);

        if (message.RequestReadReceipt)
        {
            // O cabeçalho pede a confirmação ao cliente do destinatário, que decide se
            // pergunta a ele. Não há como forçar, e nenhum cliente sério envia sem avisar.
            mime.Headers.Add(HeaderId.DispositionNotificationTo, message.From);
        }

        return mime;
    }

    private static void AddAll(InternetAddressList list, IReadOnlyList<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (MailboxAddress.TryParse(address, out var parsed))
            {
                list.Add(parsed);
            }
        }
    }

    /// <summary>
    /// Declara a prioridade nos dois cabeçalhos em uso.
    /// </summary>
    /// <remarks>
    /// <c>Importance</c> é o do padrão; <c>X-Priority</c> é o que o Outlook lê. Enviar só
    /// um deles faz a prioridade sumir em metade dos clientes do mercado.
    /// </remarks>
    private static void ApplyImportance(MimeMessage mime, DomainImportance importance)
    {
        switch (importance)
        {
            case DomainImportance.High:
                mime.Importance = MessageImportance.High;
                mime.Priority = MessagePriority.Urgent;
                mime.XPriority = XMessagePriority.High;
                break;

            case DomainImportance.Low:
                mime.Importance = MessageImportance.Low;
                mime.Priority = MessagePriority.NonUrgent;
                mime.XPriority = XMessagePriority.Low;
                break;
        }
    }
}
