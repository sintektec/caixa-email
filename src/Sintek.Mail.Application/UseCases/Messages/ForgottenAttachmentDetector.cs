using System.Text.RegularExpressions;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>
/// Avisa quando o texto promete um anexo que não existe.
/// </summary>
/// <remarks>
/// <para>
/// A especificação pede o aviso, e ele só é útil se errar pouco. Um aviso que aparece em
/// metade das mensagens legítimas é fechado sem ler, e aí deixa de avisar justamente quando
/// deveria.
/// </para>
/// <para>
/// Daí duas escolhas. As expressões exigem a palavra do anexo <b>perto de um verbo de
/// envio</b> — "segue em anexo", "attached is" —, em vez de disparar em qualquer ocorrência
/// de "anexo": mensagens sobre contratos falam de anexos o tempo todo sem carregar nenhum. E
/// o texto citado é descartado, porque a mensagem original quase sempre menciona o anexo dela
/// e não é isso que está sendo redigido agora.
/// </para>
/// </remarks>
public static partial class ForgottenAttachmentDetector
{
    /// <summary>Expressões que indicam intenção de anexar, em português e em inglês.</summary>
    [GeneratedRegex(
        """
        (seg[ue]\w*\s+(em\s+)?anexo)
        |(anexo\s+(o|a|os|as|segue|est|abaixo))
        |(em\s+anexo)
        |(anexad[oa]s?\b)
        |(encaminho\s+.{0,20}anexo)
        |(please\s+find\s+attached)
        |(attached\s+(is|are|you))
        |(i\s+(have\s+)?attach(ed)?)
        |(see\s+attach(ed|ment))
        |(attachment\s+(below|included))
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex AttachmentIntentPattern();

    /// <summary>Linhas de citação e o cabeçalho que as precede.</summary>
    [GeneratedRegex(
        @"^\s*(>|-{2,}\s*Mensagem|Em .{0,60} escreveu:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex QuotedLinePattern();

    /// <summary>
    /// Indica se o rascunho promete um anexo e não tem nenhum.
    /// </summary>
    /// <param name="bodyText">Corpo em texto puro do rascunho.</param>
    /// <param name="attachmentCount">Quantos anexos o rascunho já tem.</param>
    public static bool ShouldWarn(string? bodyText, int attachmentCount)
    {
        if (attachmentCount > 0 || string.IsNullOrWhiteSpace(bodyText))
        {
            return false;
        }

        var authored = StripQuotedText(bodyText);

        if (string.IsNullOrWhiteSpace(authored))
        {
            return false;
        }

        try
        {
            return AttachmentIntentPattern().IsMatch(authored);
        }
        catch (RegexMatchTimeoutException)
        {
            // Corpo patológico não pode travar o envio. Na dúvida, não avisa: o custo de um
            // aviso perdido é menor do que o de uma janela que congela ao clicar em Enviar.
            return false;
        }
    }

    /// <summary>
    /// Descarta a citação, mantendo só o que o usuário escreveu agora.
    /// </summary>
    /// <remarks>
    /// A varredura para na primeira linha de citação: daí para baixo é tudo mensagem
    /// anterior. Filtrar linha a linha em vez de cortar deixaria passar o corpo citado de
    /// quem responde acima da citação sem marcador de ">".
    /// </remarks>
    internal static string StripQuotedText(string bodyText)
    {
        var lines = bodyText.Split('\n');
        var authored = new List<string>();

        foreach (var line in lines)
        {
            if (QuotedLinePattern().IsMatch(line))
            {
                break;
            }

            authored.Add(line);
        }

        return string.Join('\n', authored);
    }
}
