using System.Globalization;
using System.Net;
using System.Text;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>Que tipo de rascunho montar a partir de uma mensagem existente.</summary>
public enum DraftKind
{
    /// <summary>Mensagem nova, sem origem.</summary>
    New,

    /// <summary>Resposta só ao remetente.</summary>
    Reply,

    /// <summary>Resposta ao remetente e a todos os destinatários visíveis.</summary>
    ReplyAll,

    /// <summary>Encaminhamento com o conteúdo citado.</summary>
    Forward,
}

/// <summary>Um destinatário do rascunho.</summary>
/// <param name="Kind">Campo de endereçamento.</param>
/// <param name="Address">Endereço.</param>
/// <param name="DisplayName">Nome exibido, quando conhecido.</param>
public readonly record struct DraftRecipient(AddressKind Kind, EmailAddress Address, string? DisplayName);

/// <summary>Conteúdo montado de um rascunho.</summary>
/// <param name="Subject">Assunto já com o prefixo adequado.</param>
/// <param name="Recipients">Destinatários preenchidos.</param>
/// <param name="HtmlBody">Corpo em HTML, com a citação.</param>
/// <param name="TextBody">Corpo em texto puro, com a citação.</param>
/// <param name="InReplyTo">Message-ID ao qual esta mensagem responde.</param>
/// <param name="References">Cadeia References da conversa.</param>
/// <param name="ThreadId">Conversa à qual o rascunho pertence.</param>
public sealed record DraftContent(
    string Subject,
    IReadOnlyList<DraftRecipient> Recipients,
    string? HtmlBody,
    string? TextBody,
    string? InReplyTo,
    IReadOnlyList<string> References,
    Guid? ThreadId);

/// <summary>
/// Monta o conteúdo inicial de respostas e encaminhamentos.
/// </summary>
/// <remarks>
/// Função pura, sem repositório nem rede: dado a mensagem de origem e o corpo dela, devolve o
/// que o compositor deve exibir. É o que permite verificar as regras chatas — quem entra em
/// cópia, o que sai da lista, como o assunto é prefixado — sem montar meia aplicação.
/// </remarks>
public static class DraftComposer
{
    /// <summary>Prefixo de resposta. "Re:" é o único universalmente reconhecido.</summary>
    private const string ReplyPrefix = "Re: ";

    /// <summary>
    /// Prefixo de encaminhamento.
    /// </summary>
    /// <remarks>
    /// "Enc:" é o que o Outlook em português usa, e este produto imita o Outlook. O
    /// <c>Message.NormalizeSubject</c> reconhece "enc", "fw" e "fwd" ao agrupar conversas,
    /// então a escolha não quebra o encadeamento com quem usa outro cliente.
    /// </remarks>
    private const string ForwardPrefix = "Enc: ";

    /// <summary>Monta o rascunho.</summary>
    /// <param name="kind">Tipo de rascunho.</param>
    /// <param name="source">Mensagem de origem. Ignorada quando o tipo é <see cref="DraftKind.New"/>.</param>
    /// <param name="body">Corpo da origem, quando já baixado.</param>
    /// <param name="accountAddress">Endereço da conta que responde, para não se autoincluir.</param>
    /// <param name="signature">Assinatura da conta, quando configurada.</param>
    public static DraftContent Compose(
        DraftKind kind,
        Message? source,
        MessageBody? body,
        EmailAddress accountAddress,
        string? signature = null)
    {
        ArgumentNullException.ThrowIfNull(accountAddress);

        if (kind == DraftKind.New || source is null)
        {
            return new DraftContent(
                string.Empty, [], SignatureHtml(signature), signature, null, [], null);
        }

        var recipients = kind switch
        {
            DraftKind.Reply => ReplyRecipients(source, accountAddress),
            DraftKind.ReplyAll => ReplyAllRecipients(source, accountAddress),
            _ => [],
        };

        var subject = kind == DraftKind.Forward
            ? Prefix(source.Subject, ForwardPrefix)
            : Prefix(source.Subject, ReplyPrefix);

        var references = BuildReferences(source);

        return new DraftContent(
            subject,
            recipients,
            BuildHtmlBody(kind, source, body, signature),
            BuildTextBody(kind, source, body, signature),
            kind == DraftKind.Forward ? null : source.MessageId,
            kind == DraftKind.Forward ? [] : references,
            kind == DraftKind.Forward ? null : source.ThreadId);
    }

    /// <summary>
    /// Acrescenta o prefixo, sem empilhar.
    /// </summary>
    /// <remarks>
    /// "Re: Re: Re: Proposta" é o sintoma clássico de cliente que não verifica antes de
    /// prefixar. A comparação é feita sobre o assunto já normalizado pelo domínio, que
    /// reconhece as variantes em português e em inglês.
    /// </remarks>
    internal static string Prefix(string subject, string prefix)
    {
        var trimmed = (subject ?? string.Empty).Trim();

        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : prefix + trimmed;
    }

    /// <summary>
    /// Destinatários de uma resposta simples: quem responde é quem pediu para receber.
    /// </summary>
    /// <remarks>
    /// <c>Reply-To</c> tem precedência sobre <c>From</c> — é justamente para isso que o
    /// cabeçalho existe, e listas de discussão dependem dele.
    /// </remarks>
    private static IReadOnlyList<DraftRecipient> ReplyRecipients(Message source, EmailAddress accountAddress)
    {
        var replyTo = source.Addresses.FirstOrDefault(a => a.Kind == AddressKind.ReplyTo);

        if (replyTo is not null)
        {
            return [new DraftRecipient(AddressKind.To, replyTo.Address, replyTo.DisplayName)];
        }

        return source.FromAddress is null
            ? []
            : [new DraftRecipient(AddressKind.To, source.FromAddress, source.FromDisplayName)];
    }

    /// <summary>
    /// Destinatários de "responder a todos".
    /// </summary>
    /// <remarks>
    /// Duas exclusões deliberadas. A própria conta sai da lista: responder a todos e receber
    /// a própria resposta é ruído que ninguém quer. E a cópia oculta nunca entra — quem
    /// estava em CCO estava escondido dos demais, e revelá-lo numa resposta é vazamento de
    /// informação, não conveniência.
    /// </remarks>
    private static IReadOnlyList<DraftRecipient> ReplyAllRecipients(Message source, EmailAddress accountAddress)
    {
        var recipients = new List<DraftRecipient>(ReplyRecipients(source, accountAddress));
        var seen = recipients.Select(r => r.Address.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
        ;
        seen.Add(accountAddress.Value);

        foreach (var participant in source.Addresses)
        {
            if (participant.Kind is not (AddressKind.To or AddressKind.Cc))
            {
                continue;
            }

            if (!seen.Add(participant.Address.Value))
            {
                continue;
            }

            recipients.Add(new DraftRecipient(
                participant.Kind == AddressKind.To ? AddressKind.To : AddressKind.Cc,
                participant.Address,
                participant.DisplayName));
        }

        return recipients;
    }

    /// <summary>
    /// Monta a cadeia References, que é o que mantém a conversa unida.
    /// </summary>
    /// <remarks>
    /// A RFC 5322 manda acrescentar o Message-ID da mensagem respondida ao final da cadeia
    /// que ela trazia. Clientes que descartam o cabeçalho quebram o encadeamento, e é por
    /// isso que o produto também agrupa por assunto normalizado.
    /// </remarks>
    private static IReadOnlyList<string> BuildReferences(Message source)
    {
        var references = new List<string>();

        if (!string.IsNullOrWhiteSpace(source.ReferencesRaw))
        {
            references.AddRange(source.ReferencesRaw.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (!references.Contains(source.MessageId, StringComparer.Ordinal))
        {
            references.Add(source.MessageId);
        }

        return references;
    }

    private static string? SignatureHtml(string? signature)
        => string.IsNullOrWhiteSpace(signature)
            ? null
            : $"<p>{WebUtility.HtmlEncode(signature).Replace("\n", "<br>", StringComparison.Ordinal)}</p>";

    /// <summary>
    /// Monta o corpo em HTML com a citação.
    /// </summary>
    /// <remarks>
    /// O corpo citado entra <b>como veio do sanitizador</b>, nunca o HTML original. O
    /// conteúdo que o usuário vai reenviar não pode carregar script que o painel de leitura
    /// já tinha removido — seria o produto propagando o que existe para conter.
    /// </remarks>
    private static string? BuildHtmlBody(
        DraftKind kind, Message source, MessageBody? body, string? signature)
    {
        var quoted = body?.SanitizedHtml;

        var builder = new StringBuilder();
        builder.Append("<p></p>");

        if (!string.IsNullOrWhiteSpace(signature))
        {
            builder.Append(SignatureHtml(signature));
        }

        builder.Append("<hr>");
        builder.Append(WebUtility.HtmlEncode(Header(kind, source)).Replace("\n", "<br>", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(quoted))
        {
            builder.Append("<blockquote>").Append(quoted).Append("</blockquote>");
        }

        return builder.ToString();
    }

    private static string? BuildTextBody(
        DraftKind kind, Message source, MessageBody? body, string? signature)
    {
        var builder = new StringBuilder();
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(signature))
        {
            builder.AppendLine(signature);
        }

        builder.AppendLine();
        builder.AppendLine(Header(kind, source));

        if (!string.IsNullOrWhiteSpace(body?.TextBody))
        {
            foreach (var line in body.TextBody.Split('\n'))
            {
                builder.Append("> ").AppendLine(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    /// <summary>Cabeçalho da citação, no formato que os clientes de e-mail usam.</summary>
    private static string Header(DraftKind kind, Message source)
    {
        var sender = source.FromDisplayName is { Length: > 0 } name
            ? $"{name} <{source.FromAddress?.Value}>"
            : source.FromAddress?.Value ?? "(remetente desconhecido)";

        // Formato explícito com cultura invariante: o projeto compila em modo de
        // globalização invariante, e pedir "pt-BR" pelo nome lança em tempo de execução. O
        // padrão brasileiro está no próprio formato, então nada se perde.
        var sentAt = source.SentAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        if (kind != DraftKind.Forward)
        {
            return $"Em {sentAt}, {sender} escreveu:";
        }

        var to = string.Join(
            "; ",
            source.Addresses.Where(a => a.Kind == AddressKind.To).Select(a => a.Address.Value));

        return $"""
            ---------- Mensagem encaminhada ----------
            De: {sender}
            Data: {sentAt}
            Assunto: {source.Subject}
            Para: {to}
            """;
    }
}
