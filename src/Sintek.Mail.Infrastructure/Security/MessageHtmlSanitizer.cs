using System.Net;
using Ganss.Xss;
using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>
/// Higieniza o HTML de mensagens com a biblioteca HtmlSanitizer.
/// </summary>
/// <remarks>
/// <para>
/// Corpo de e-mail é conteúdo hostil por definição: qualquer pessoa pode enviá-lo. Esta
/// classe é a primeira das duas camadas de defesa exigidas pela especificação; a segunda
/// é o WebView2 configurado sem scripts, sem DevTools e com navegação bloqueada. Nenhuma
/// das duas sozinha basta.
/// </para>
/// <para>
/// O bloqueio de imagens remotas não é só uma questão de banda: carregar uma imagem
/// hospedada pelo remetente confirma a ele que a mensagem foi aberta, quando foi, e a
/// partir de qual endereço IP. É a técnica clássica de rastreamento por pixel — por isso
/// a especificação exige bloqueá-las por padrão.
/// </para>
/// </remarks>
public sealed class MessageHtmlSanitizer : Application.Abstractions.Security.IHtmlSanitizer
{
    /// <summary>
    /// Atributos que provocam uma requisição de rede ao serem renderizados.
    /// </summary>
    private static readonly string[] ResourceAttributes =
        ["src", "srcset", "background", "poster", "data-src", "lowsrc"];

    /// <inheritdoc />
    public SanitizedHtmlResult Sanitize(string? html, bool allowRemoteContent = false)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SanitizedHtmlResult(string.Empty, false, 0);
        }

        var removedRemoteReferences = 0;
        var sawRemoteReference = false;

        var sanitizer = CreateSanitizer(allowRemoteContent);

        // O contador vem dos eventos do próprio sanitizador: é a única forma de saber o
        // que foi de fato removido, em vez de tentar adivinhar reprocessando o HTML.
        sanitizer.RemovingAttribute += (_, args) =>
        {
            if (IsResourceAttribute(args.Attribute.Name) && IsRemoteUri(args.Attribute.Value))
            {
                removedRemoteReferences++;
                sawRemoteReference = true;
            }
        };

        sanitizer.RemovingStyle += (_, _) => sawRemoteReference = true;

        var sanitized = sanitizer.Sanitize(html);

        // Quando o conteúdo remoto é permitido, nada é removido — mas ainda precisamos
        // informar à interface que a mensagem o contém.
        if (allowRemoteContent)
        {
            sawRemoteReference = ContainsRemoteReference(sanitized);
        }

        return new SanitizedHtmlResult(sanitized, sawRemoteReference, removedRemoteReferences);
    }

    /// <inheritdoc />
    public string PlainTextToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Escapar antes de inserir quebras: a ordem inversa transformaria um corpo com
        // "&lt;br&gt;" digitado literalmente em uma quebra real.
        var encoded = WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);

        return $"<div class=\"sintek-plain-text\">{encoded}</div>";
    }

    /// <summary>
    /// Monta um sanitizador com a política de segurança da aplicação.
    /// </summary>
    /// <remarks>
    /// Uma instância por chamada: os manipuladores de evento carregam estado da mensagem
    /// sendo processada, e reaproveitar a instância entre threads misturaria contadores
    /// de mensagens diferentes.
    /// </remarks>
    private static HtmlSanitizer CreateSanitizer(bool allowRemoteContent)
    {
        var sanitizer = new HtmlSanitizer();

        // Conteúdo ativo: removido sempre, sem exceção e sem opção de configuração.
        foreach (var tag in new[] { "script", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input", "button", "textarea", "select", "meta", "link", "base" })
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        // Manipuladores de evento (onclick, onerror, onload…) nunca são permitidos. O
        // onerror de uma <img> é o vetor clássico de execução em HTML de e-mail.
        var eventHandlers = sanitizer.AllowedAttributes
            .Where(a => a.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var handler in eventHandlers)
        {
            sanitizer.AllowedAttributes.Remove(handler);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");

        // 'cid:' referencia anexos embutidos, que já estão no disco local: são seguros e
        // não geram tráfego de rede.
        sanitizer.AllowedSchemes.Add("cid");

        if (allowRemoteContent)
        {
            sanitizer.AllowedSchemes.Add("http");
            sanitizer.AllowedSchemes.Add("https");
        }
        else
        {
            // Sem http/https nos esquemas permitidos, o sanitizador remove os atributos
            // de recurso que apontariam para fora — inclusive os de dentro de CSS.
            sanitizer.AllowedCssProperties.Remove("background-image");
            sanitizer.AllowedCssProperties.Remove("background");
            sanitizer.AllowedCssProperties.Remove("list-style-image");
        }

        // 'data:' fica de fora mesmo com conteúdo remoto liberado: um SVG em data URI
        // pode carregar script, e o ganho de exibir uma imagem embutida assim não
        // compensa abrir essa porta.
        sanitizer.AllowedSchemes.Remove("data");

        // Links abrem no navegador do sistema, nunca dentro do painel de leitura.
        sanitizer.AllowedAttributes.Add("target");

        // KeepChildNodes fica FALSO (o padrão). Com ele ligado, o conteúdo de uma tag
        // removida sobrevive como texto — e o corpo de um <script> passaria a ser exibido
        // ao usuário como se fosse parte da mensagem. Inerte, mas confuso e revelador do
        // payload de um ataque.
        sanitizer.KeepChildNodes = false;

        return sanitizer;
    }

    private static bool IsResourceAttribute(string attributeName)
        => ResourceAttributes.Contains(attributeName, StringComparer.OrdinalIgnoreCase);

    private static bool IsRemoteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.TrimStart();

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool ContainsRemoteReference(string html)
        => html.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || html.Contains("https://", StringComparison.OrdinalIgnoreCase);
}
