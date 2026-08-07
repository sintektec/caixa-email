namespace Sintek.Mail.Infrastructure.Calendar.CalDav;

/// <summary>
/// Normaliza o <c>&lt;D:href&gt;</c> das respostas do WebDAV.
/// </summary>
/// <remarks>
/// <para>
/// O mesmo recurso volta de formas diferentes na mesma sessão: absoluto
/// (<c>https://cal.exemplo.com/bernard/work/abcd.ics</c>) numa resposta e relativo
/// (<c>/bernard/work/abcd.ics</c>) na seguinte, com ou sem barra final em coleções, e
/// <b>percent-encoded de formas diferentes</b> — <c>%40</c> ou <c>@</c>, <c>%2F</c> em
/// maiúscula ou minúscula. Guardar a forma crua faz a busca pelo href falhar em um recurso
/// que existe, e o efeito é um evento duplicado a cada sincronização.
/// </para>
/// <para>
/// A chave guardada é resolvida contra a URI da requisição e comparada decodificada. Para
/// <b>emitir</b> a requisição de volta usa-se a URI absoluta, nunca a forma decodificada:
/// um <c>#</c> ou um <c>?</c> no nome do recurso quebraria o endereço remontado.
/// </para>
/// </remarks>
internal static class CalDavHref
{
    /// <summary>
    /// Resolve um href contra a URI que produziu a resposta.
    /// </summary>
    /// <returns>A URI absoluta, ou <see langword="null"/> quando o href não é utilizável.</returns>
    internal static Uri? Resolve(Uri requestUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(requestUri, href.Trim(), out var absolute) ? absolute : null;
    }

    /// <summary>A forma canônica que vai para o banco: esquema, autoridade e caminho, decodificados.</summary>
    internal static string Key(Uri absolute)
        => absolute.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped);

    /// <summary>Resolve e canoniza de uma vez.</summary>
    internal static string? KeyOf(Uri requestUri, string? href)
        => Resolve(requestUri, href) is { } absolute ? Key(absolute) : null;

    /// <summary>
    /// Reconstrói a URI de requisição a partir da chave guardada.
    /// </summary>
    /// <remarks>
    /// A chave é decodificada; reescapar é o que devolve um endereço emitível. O
    /// <see cref="Uri"/> faz isso ao construir com <see cref="UriKind.Absolute"/>.
    /// </remarks>
    internal static Uri ToRequestUri(string key) => new(key, UriKind.Absolute);

    /// <summary>Garante a barra final que o WebDAV exige em coleção.</summary>
    internal static string AsCollection(string key)
        => key.EndsWith('/') ? key : key + "/";
}
