using System.Text;
using MimeKit;
using Sintek.Mail.Application.Abstractions.Mail;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>
/// Acrescenta a parte <c>text/calendar</c> a uma mensagem de saída.
/// </summary>
/// <remarks>
/// <para>
/// A parte entra em um <c>multipart/alternative</c> ao lado do texto, e <b>não</b> como
/// anexo. É essa forma que faz o cliente do destinatário processar o convite ou a resposta
/// sozinho — o Outlook atualiza o <c>PARTSTAT</c> na agenda dele sem pedir nada. Como
/// anexo, ele mostraria um arquivo <c>.ics</c> para a pessoa abrir à mão, e a metade que
/// não abre nunca responde.
/// </para>
/// <para>
/// O parâmetro <c>method</c> vai no <c>Content-Type</c>, e não só dentro do documento: a
/// RFC 6047 exige os dois, e clientes que leem só o cabeçalho tratariam a resposta como
/// convite novo.
/// </para>
/// </remarks>
internal static class CalendarPartBuilder
{
    /// <summary>Monta o corpo da mensagem, com a parte de calendário quando houver.</summary>
    public static MimeEntity Build(BodyBuilder builder, OutgoingCalendarPart? calendar)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var body = builder.ToMessageBody();

        if (calendar is not { } part || string.IsNullOrWhiteSpace(part.Content))
        {
            return body;
        }

        var contentType = new ContentType("text", "calendar")
        {
            Charset = "utf-8",
        };

        contentType.Parameters.Add("method", part.Method);

        var entity = new MimePart(contentType)
        {
            // Base64 e não 7bit: o documento carrega acentos no assunto e no nome dos
            // participantes, e dobra de linha em campo longo. Quoted-printable também
            // serviria, mas base64 não corre o risco de um servidor reescrever a dobra.
            ContentTransferEncoding = ContentEncoding.Base64,
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(part.Content))),
        };

        // Uma alternativa só, com o corpo existente: cliente que não entende iCalendar
        // continua vendo o texto.
        var alternative = new MultipartAlternative { body, entity };

        return alternative;
    }
}
