using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Sync;

/// <summary>
/// Converte uma mensagem guardada localmente no formato que o transporte SMTP consome.
/// </summary>
/// <remarks>
/// Fica na camada de Aplicação, e não na de Infraestrutura, porque a conversão é regra de
/// negócio: quem entra em Para, quem entra em cópia oculta, quais anexos já foram baixados
/// e podem ser enviados. O transporte apenas escreve MIME e fala com o servidor.
/// </remarks>
public static class OutgoingMessageBuilder
{
    /// <summary>
    /// Monta a mensagem de saída, ou devolve <see langword="null"/> quando ela não está em
    /// condições de ser enviada.
    /// </summary>
    /// <remarks>
    /// Recusa em três situações, todas por perda de dados evitável: sem remetente, sem
    /// destinatário algum, ou com anexo que ainda não terminou de baixar. Enviar uma
    /// mensagem sem o anexo que o usuário anexou é pior do que não enviar — ela chega
    /// aparentemente completa e ninguém percebe a falta.
    /// </remarks>
    public static OutgoingMessage? Build(Message message, MessageBody? body)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.FromAddress is null)
        {
            return null;
        }

        var to = AddressesOf(message, AddressKind.To);
        var cc = AddressesOf(message, AddressKind.Cc);
        var bcc = AddressesOf(message, AddressKind.Bcc);

        if (to.Count == 0 && cc.Count == 0 && bcc.Count == 0)
        {
            return null;
        }

        var attachments = new List<OutgoingAttachment>();

        foreach (var attachment in message.Attachments)
        {
            if (!attachment.IsDownloaded || string.IsNullOrWhiteSpace(attachment.StoragePath))
            {
                return null;
            }

            attachments.Add(new OutgoingAttachment(
                attachment.FileName,
                attachment.StoragePath,
                attachment.ContentType,
                attachment.IsInline ? attachment.ContentId : null));
        }

        return new OutgoingMessage
        {
            From = message.FromAddress.Value,
            FromDisplayName = message.FromDisplayName,
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = message.Subject,
            HtmlBody = body?.HtmlBody,
            TextBody = body?.TextBody,
            Attachments = attachments,
            InReplyTo = message.InReplyTo,
            References = SplitReferences(message.ReferencesRaw),
            Importance = message.Importance,
            RequestReadReceipt = message.ReadReceiptRequested,
            Calendar = body?.CalendarPayload is { } payload
                ? new OutgoingCalendarPart(body.CalendarMethod ?? "REQUEST", payload)
                : null,
        };
    }

    private static IReadOnlyList<string> AddressesOf(Message message, AddressKind kind)
        => message.Addresses
            .Where(a => a.Kind == kind)
            .Select(a => a.Address.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Quebra o cabeçalho References na lista de Message-IDs.
    /// </summary>
    /// <remarks>
    /// O cabeçalho é uma sequência separada por espaço, e clientes diferentes usam quebras
    /// de linha no meio. Dividir por qualquer espaço em branco cobre as duas formas.
    /// </remarks>
    internal static IReadOnlyList<string> SplitReferences(string? referencesRaw)
        => string.IsNullOrWhiteSpace(referencesRaw)
            ? []
            : referencesRaw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
