using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Calendar.CalDav;

/// <summary>Uma resposta do servidor, já lida.</summary>
/// <param name="StatusCode">Código HTTP.</param>
/// <param name="Body">Corpo, como texto.</param>
/// <param name="ETag">ETag do header, verbatim — com as aspas, se vieram.</param>
/// <param name="RequestUri">Endereço efetivamente atendido, depois dos redirecionamentos.</param>
public readonly record struct CalDavResponse(
    HttpStatusCode StatusCode, string Body, string? ETag, Uri RequestUri)
{
    /// <summary>Se o servidor aceitou.</summary>
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    /// <summary>Se a resposta é uma multistatus do WebDAV.</summary>
    public bool IsMultiStatus => StatusCode == HttpStatusCode.MultiStatus;
}

/// <summary>
/// Emite requisições WebDAV/CalDAV autenticadas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redirecionamento é seguido à mão.</b> O <see cref="HttpClient"/> com
/// <c>AllowAutoRedirect</c> ligado transforma um <c>PROPFIND</c> em <c>GET</c> ao seguir um
/// 301, e <b>descarta o header <c>Authorization</c> quando o destino é outro host</b> — que
/// é exatamente o caso do iCloud, cujo <c>calendar-home-set</c> aponta para a partição da
/// conta em outro nome de servidor. Com o redirecionamento automático, o sintoma é um 401
/// inexplicável logo depois de uma autenticação que funcionou.
/// </para>
/// <para>
/// <b>A autenticação é preemptiva.</b> Vários servidores respondem 401 sem
/// <c>WWW-Authenticate</c> aproveitável, e mesmo os que respondem cobram um ida-e-volta por
/// requisição. O header vai montado desde a primeira.
/// </para>
/// <para>
/// <b>O ETag nunca é lido pela propriedade tipada.</b> Servidores fora da norma devolvem o
/// valor sem aspas, e <see cref="HttpResponseHeaders.ETag"/> lança <see cref="FormatException"/>
/// ao analisá-lo. O valor é guardado cru, com as aspas que vierem: <c>"2134-314"</c> e
/// <c>2134-314</c> são ETags diferentes para o <c>If-Match</c>.
/// </para>
/// </remarks>
public sealed class CalDavTransport
{
    /// <summary>Métodos do WebDAV que o <see cref="HttpMethod"/> não traz prontos.</summary>
    public static readonly HttpMethod Propfind = new("PROPFIND");

    /// <summary>O método REPORT, do WebDAV versioning e usado por todo o CalDAV.</summary>
    public static readonly HttpMethod Report = new("REPORT");

    /// <summary>
    /// Teto de leitura do corpo.
    /// </summary>
    /// <remarks>
    /// Mesma defesa do <c>AutoconfigFetcher</c>: o host é escolhido pelo endereço que o
    /// usuário digitou, e um fluxo sem fim consumiria a memória do processo. O limite é
    /// largo de propósito — uma coleção com milhares de eventos e o iCalendar de cada um
    /// cabe com folga.
    /// </remarks>
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>Quantos redirecionamentos são seguidos antes de desistir.</summary>
    private const int MaxRedirects = 5;

    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentials;
    private readonly IOAuthProviderRegistry _oauthProviders;
    private readonly ILogger<CalDavTransport> _logger;

    public CalDavTransport(
        HttpClient httpClient,
        ICredentialStore credentials,
        IOAuthProviderRegistry oauthProviders,
        ILogger<CalDavTransport> logger)
    {
        _httpClient = httpClient;
        _credentials = credentials;
        _oauthProviders = oauthProviders;
        _logger = logger;
    }

    /// <summary>
    /// Monta o header de autenticação da conta.
    /// </summary>
    /// <remarks>
    /// A credencial sai do <see cref="ICredentialStore"/> a cada chamada e vive só no
    /// escopo dela. Guardá-la em campo seria uma cópia a mais na memória do processo, com
    /// tempo de vida maior do que o necessário, e nada disso entra em log.
    /// </remarks>
    public async Task<AuthenticationHeaderValue?> BuildAuthenticationAsync(
        Account account, CancellationToken cancellationToken)
    {
        if (account.AuthenticationType == AuthenticationType.OAuth2)
        {
            var provider = _oauthProviders.Resolve(account.OAuthProvider);

            if (provider is null)
            {
                return null;
            }

            var token = await provider
                .GetAccessTokenAsync(account.EmailAddress.Value, cancellationToken)
                .ConfigureAwait(false);

            return new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        var password = await _credentials
            .GetSecretAsync(account.CredentialKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var user = account.UserName ?? account.EmailAddress.Value;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        return new AuthenticationHeaderValue("Basic", raw);
    }

    /// <summary>Emite uma requisição, seguindo redirecionamentos à mão.</summary>
    /// <param name="authentication">
    /// Header já montado por <see cref="BuildAuthenticationAsync"/>. Reaproveitá-lo entre as
    /// requisições de um ciclo evita reler o cofre a cada chamada.
    /// </param>
    /// <param name="depth">Valor do header <c>Depth</c>, quando o método o exige.</param>
    public async Task<CalDavResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        AuthenticationHeaderValue? authentication,
        string? body,
        string? contentType,
        string? depth,
        string? ifMatch,
        bool ifNoneMatchAny,
        CancellationToken cancellationToken)
    {
        var current = uri;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (current.Scheme != Uri.UriSchemeHttps)
            {
                // Basic sobre HTTP é a senha em claro no fio. Um redirecionamento para
                // HTTP é justamente o ataque que o cliente precisa recusar.
                throw new InvalidOperationException(
                    $"O servidor de agenda redirecionou para um endereço não-HTTPS ({current.Scheme}).");
            }

            using var request = new HttpRequestMessage(method, current);
            request.Headers.Authorization = authentication;

            if (depth is not null)
            {
                request.Headers.TryAddWithoutValidation("Depth", depth);
            }

            // Suprime os propstat 404 de propriedade ausente. Quem não conhece ignora.
            request.Headers.TryAddWithoutValidation("Prefer", "return-minimal");

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (ifNoneMatchAny)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            }

            if (body is not null)
            {
                // Só o tipo, sem parâmetro: o StringContent lança FormatException se o
                // media type trouxer um ';', e é a codificação que acrescenta o
                // charset=utf-8 que a norma exige no corpo.
                request.Content = new StringContent(
                    body, Encoding.UTF8, MediaTypeOnly(contentType) ?? "application/xml");
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode)
                && response.Headers.Location is { } location
                && hop < MaxRedirects)
            {
                // Resolver contra o endereço atual: o Location vem relativo com frequência.
                current = new Uri(current, location);
                continue;
            }

            var content = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            return new CalDavResponse(response.StatusCode, content, ReadETag(response), current);
        }

        throw new InvalidOperationException(
            $"O servidor de agenda excedeu {MaxRedirects} redirecionamentos.");
    }

    /// <summary>
    /// Lê o ETag sem passar pela propriedade tipada.
    /// </summary>
    /// <remarks>
    /// Um ETag fraco (<c>W/"abc"</c>) não serve para <c>If-Match</c>, que compara forte.
    /// Ele é descartado aqui para que o chamador caia na releitura por <c>GET</c> em vez de
    /// gravar um valor que a próxima escrita rejeitaria com 412 para sempre.
    /// </remarks>
    private static string? ReadETag(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("ETag", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();

        return raw.StartsWith("W/", StringComparison.Ordinal) ? null : raw;
    }

    private static string? MediaTypeOnly(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);

        return separator < 0 ? contentType.Trim() : contentType[..separator].Trim();
    }

    private static bool IsRedirect(HttpStatusCode status) => status
        is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private async Task<string> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                _logger.LogWarning(
                    "A resposta do servidor de agenda passou de {Limite} bytes e foi truncada.",
                    MaxResponseBytes);

                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
