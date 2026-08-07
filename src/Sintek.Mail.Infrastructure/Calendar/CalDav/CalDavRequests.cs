using System.Text;
using System.Xml.Linq;

namespace Sintek.Mail.Infrastructure.Calendar.CalDav;

/// <summary>Os corpos XML que este cliente envia.</summary>
/// <remarks>
/// Montados com <see cref="XDocument"/>, e não por concatenação de texto, porque o único
/// valor variável que entra num deles é um <c>href</c> vindo do servidor — e um href com
/// <c>&amp;</c> quebraria um documento montado à mão. Os prefixos declarados aqui são
/// arbitrários e não precisam coincidir com os do servidor.
/// </remarks>
internal static class CalDavRequests
{
    /// <summary>Descobre o principal do usuário autenticado (RFC 5397).</summary>
    internal static string CurrentUserPrincipal() => Serialize(
        new XElement(
            DavXml.Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "current-user-principal"))));

    /// <summary>Descobre a coleção-raiz de calendários do principal (RFC 4791 §6.2.1).</summary>
    internal static string CalendarHomeSet() => Serialize(
        new XElement(
            DavXml.Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", DavXml.CalDav.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.CalDav + "calendar-home-set"))));

    /// <summary>
    /// Lista as coleções de calendário.
    /// </summary>
    /// <remarks>
    /// Tudo o que interessa vem no mesmo pedido — nome, cor, <c>CTag</c>, <c>sync-token</c>,
    /// componentes aceitos e privilégios. Um PROPFIND por propriedade multiplicaria as
    /// viagens por coleção, e quem não conhece uma delas simplesmente devolve 404 naquele
    /// <c>propstat</c>.
    /// </remarks>
    internal static string CalendarCollections() => Serialize(
        new XElement(
            DavXml.Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", DavXml.CalDav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", DavXml.CalendarServer.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ic", DavXml.AppleICal.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "resourcetype"),
                new XElement(DavXml.Dav + "displayname"),
                new XElement(DavXml.Dav + "sync-token"),
                new XElement(DavXml.Dav + "current-user-privilege-set"),
                new XElement(DavXml.CalendarServer + "getctag"),
                new XElement(DavXml.AppleICal + "calendar-color"),
                new XElement(DavXml.CalDav + "supported-calendar-component-set"))));

    /// <summary>Lê só o <c>CTag</c> e o <c>sync-token</c> de uma coleção.</summary>
    internal static string CollectionMarkers() => Serialize(
        new XElement(
            DavXml.Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", DavXml.CalendarServer.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "sync-token"),
                new XElement(DavXml.CalendarServer + "getctag"))));

    /// <summary>
    /// REPORT <c>sync-collection</c> (RFC 6578).
    /// </summary>
    /// <param name="syncToken">
    /// Token guardado, ou <see langword="null"/> para a passada completa — que se pede com o
    /// elemento <b>vazio</b>, não com a ausência dele.
    /// </param>
    /// <remarks>
    /// <b>Sem <c>&lt;D:limit&gt;</c> de propósito.</b> Quando o servidor não consegue truncar
    /// no número pedido, ele falha a requisição inteira com 507; e o Nextcloud tem defeito
    /// conhecido com esse elemento na sincronização inicial. Deixar o servidor truncar
    /// sozinho e tratar a paginação pelo 507 dentro do 207 funciona em todos.
    /// </remarks>
    internal static string SyncCollection(string? syncToken) => Serialize(
        new XElement(
            DavXml.Dav + "sync-collection",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XElement(DavXml.Dav + "sync-token", syncToken ?? string.Empty),
            new XElement(DavXml.Dav + "sync-level", "1"),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "getetag"))));

    /// <summary>
    /// REPORT <c>calendar-query</c> pedindo só os ETags.
    /// </summary>
    /// <remarks>
    /// É o caminho de quem não fala <c>sync-collection</c>. <b>Sem <c>calendar-data</c></b>:
    /// trazer o iCalendar de cada recurso só para descobrir o que mudou torna a listagem de
    /// uma coleção grande cara demais. O conteúdo vem depois, e só do que mudou, num
    /// <c>calendar-multiget</c>.
    /// </remarks>
    internal static string CalendarQueryETags() => Serialize(
        new XElement(
            DavXml.CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", DavXml.CalDav.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "getetag")),
            new XElement(
                DavXml.CalDav + "filter",
                new XElement(
                    DavXml.CalDav + "comp-filter",
                    new XAttribute("name", "VCALENDAR"),
                    new XElement(
                        DavXml.CalDav + "comp-filter",
                        new XAttribute("name", "VEVENT"))))));

    /// <summary>
    /// REPORT <c>calendar-multiget</c> (RFC 4791 §7.9).
    /// </summary>
    /// <remarks>
    /// <c>&lt;C:calendar-data/&gt;</c> vem vazio, sem filtro de componente ou propriedade:
    /// o documento íntegro é guardado para que uma reescrita futura não destrua o que este
    /// produto não modela.
    /// </remarks>
    internal static string CalendarMultiget(IEnumerable<string> hrefs) => Serialize(
        new XElement(
            DavXml.CalDav + "calendar-multiget",
            new XAttribute(XNamespace.Xmlns + "d", DavXml.Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", DavXml.CalDav.NamespaceName),
            new XElement(
                DavXml.Dav + "prop",
                new XElement(DavXml.Dav + "getetag"),
                new XElement(DavXml.CalDav + "calendar-data")),
            hrefs.Select(h => new XElement(DavXml.Dav + "href", h))));

    private static string Serialize(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var builder = new StringBuilder();

        using (var writer = new Utf8StringWriter(builder))
        {
            document.Save(writer, SaveOptions.DisableFormatting);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escritor que declara UTF-8.
    /// </summary>
    /// <remarks>
    /// O <see cref="StringWriter"/> comum informa <see cref="Encoding.Unicode"/>, e o
    /// <see cref="XDocument.Save(TextWriter)"/> escreve isso na declaração — o documento sai
    /// com <c>encoding="utf-16"</c> enquanto os bytes vão em UTF-8. Servidor estrito recusa,
    /// e servidor tolerante decodifica errado o primeiro acento que aparecer.
    /// </remarks>
    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder builder)
            : base(builder)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
