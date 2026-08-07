using System.Xml;
using System.Xml.Linq;

namespace Sintek.Mail.Infrastructure.Calendar.CalDav;

/// <summary>
/// Nomes e leitura de XML do WebDAV/CalDAV.
/// </summary>
/// <remarks>
/// <para>
/// <b>Os prefixos são arbitrários.</b> Um servidor escreve <c>&lt;D:response&gt;</c>, outro
/// <c>&lt;d:response&gt;</c>, outro <c>&lt;dav:response&gt;</c> — e todos estão certos.
/// Casar por prefixo, ou por <c>Element("response")</c> sem namespace, devolve zero
/// elementos <b>sem erro nenhum</b>, que é o pior modo de errar. Aqui só se casa por
/// <see cref="XNamespace"/> + nome local.
/// </para>
/// <para>
/// <b><c>DAV:</c> é literal</b>, com os dois-pontos e sem <c>http://</c>. Não é URL, é o
/// namespace inteiro. Escrever <c>"DAV"</c> sem os dois-pontos produz o mesmo silêncio.
/// </para>
/// </remarks>
internal static class DavXml
{
    /// <summary>Namespace do WebDAV. É esse texto mesmo, com os dois-pontos.</summary>
    internal static readonly XNamespace Dav = "DAV:";

    /// <summary>Namespace do CalDAV (RFC 4791).</summary>
    internal static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    /// <summary>Extensões do Calendar Server — é de onde vem o <c>getctag</c>.</summary>
    internal static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    /// <summary>Extensões da Apple — é de onde vem a cor do calendário.</summary>
    internal static readonly XNamespace AppleICal = "http://apple.com/ns/ical/";

    /// <summary>
    /// Configuração de leitura de todo XML que vem da rede.
    /// </summary>
    /// <remarks>
    /// Mesma regra do <c>ClientConfigParser</c>, e pelo mesmo motivo: o host que responde é
    /// escolhido pelo endereço que o usuário digitou. Com DTD ligado, um
    /// <c>&lt;!ENTITY SYSTEM "file:///..."&gt;</c> transformaria a resposta do servidor em
    /// leitura arbitrária de disco.
    /// </remarks>
    internal static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreWhitespace = false,
        CloseInput = false,
    };

    /// <summary>
    /// Lê um documento, ou devolve <see langword="null"/> quando não é XML.
    /// </summary>
    /// <remarks>
    /// Nunca lança, pela mesma razão do <c>IcalNetCalendarSerializer.Read</c>: um corpo
    /// truncado ou uma página de erro em HTML vinda de um proxy são rotina, e derrubar a
    /// sincronização da conta por causa de uma resposta malformada é desproporcional.
    /// </remarks>
    internal static XDocument? Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var textReader = new StringReader(content);
            using var reader = XmlReader.Create(textReader, ReaderSettings);

            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extrai o código numérico de um <c>&lt;D:status&gt;</c> (<c>HTTP/1.1 200 OK</c>).
    /// </summary>
    /// <returns>O código, ou <c>0</c> quando o elemento não existe ou não é legível.</returns>
    internal static int StatusCode(XElement? status)
    {
        if (status is null)
        {
            return 0;
        }

        // "HTTP/1.1 404 Not Found" — o código é o segundo token. Alguns servidores omitem
        // a razão, e um deles escreve "HTTP/1.1 200"; separar por espaço cobre os dois.
        foreach (var token in status.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var code) && code is >= 100 and < 600)
            {
                return code;
            }
        }

        return 0;
    }

    /// <summary>
    /// Busca uma propriedade dentro de uma <c>&lt;D:response&gt;</c>, respeitando o status
    /// de cada <c>&lt;D:propstat&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <b>Cada propriedade tem o próprio status.</b> Uma resposta traz vários
    /// <c>propstat</c>, e um <c>404</c> dentro de um deles significa "esta propriedade não
    /// existe neste recurso" — não que o recurso sumiu. Ler só o primeiro <c>propstat</c>
    /// confunde as duas coisas, que é o erro que mais quebra cliente de CalDAV.
    /// </remarks>
    internal static XElement? FindProperty(XElement response, XName name)
    {
        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            if (StatusCode(propstat.Element(Dav + "status")) is not (0 or >= 200 and < 300))
            {
                continue;
            }

            if (propstat.Element(Dav + "prop")?.Element(name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Lê o texto de uma propriedade, ou <see langword="null"/>.</summary>
    internal static string? PropertyText(XElement response, XName name)
    {
        var value = FindProperty(response, name)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Lê o primeiro <c>&lt;D:href&gt;</c> de dentro de uma propriedade.
    /// </summary>
    /// <remarks>
    /// <c>current-user-principal</c> e <c>calendar-home-set</c> guardam o endereço em um
    /// <c>href</c> aninhado, e o segundo é <c>(DAV:href*)</c> — pode trazer mais de um.
    /// </remarks>
    internal static string? NestedHref(XElement response, XName name)
    {
        var value = FindProperty(response, name)?.Element(Dav + "href")?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Se o documento declara a pré-condição <c>DAV:valid-sync-token</c>.
    /// </summary>
    /// <remarks>
    /// É o sinal de que o servidor esqueceu o token e a próxima passada tem de ser completa.
    /// O SabreDAV — e portanto Nextcloud, ownCloud e Baikal — responde <c>403</c>; outros
    /// respondem <c>409</c>. O código sozinho não distingue "token vencido" de "sem
    /// permissão", e tratar os dois igual apagaria a agenda de uma coleção sem acesso.
    /// </remarks>
    internal static bool DeclaresInvalidSyncToken(XDocument? document)
        => document?.Descendants(Dav + "valid-sync-token").Any() == true;
}
