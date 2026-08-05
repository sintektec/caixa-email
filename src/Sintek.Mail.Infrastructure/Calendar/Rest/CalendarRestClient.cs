using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Infrastructure.Calendar.Rest;

/// <summary>Uma resposta JSON já lida.</summary>
/// <param name="StatusCode">Código HTTP.</param>
/// <param name="Body">Corpo, como texto.</param>
/// <param name="ETag">ETag do header, verbatim, quando vier.</param>
public readonly record struct RestResponse(HttpStatusCode StatusCode, string Body, string? ETag)
{
    /// <summary>Se o servidor aceitou.</summary>
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    /// <summary>
    /// O corpo interpretado, ou <see langword="null"/> quando não é JSON.
    /// </summary>
    /// <remarks>
    /// Nunca lança, pelo mesmo motivo do <c>DavXml.Parse</c>: um proxy que devolve HTML de
    /// erro, ou um corpo truncado, são rotina — e derrubar a sincronização da conta por causa
    /// de um deles é desproporcional.
    /// </remarks>
    public JsonElement? Json()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Body);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Emite requisições REST autenticadas por OAuth aos provedores de calendário em nuvem.
/// </summary>
/// <remarks>
/// <para>
/// <b>O token é pedido por escopo, e não uma vez só.</b> No Entra ID o token é emitido por
/// recurso: o que abre o IMAP não abre o Graph. Cada chamada declara o escopo de que precisa,
/// e é o provedor de OAuth que decide se isso custa uma renovação silenciosa ou nada.
/// </para>
/// <para>
/// <b>Redirecionamento continua sendo seguido à mão</b>, pelo mesmo motivo do CalDAV: o
/// <see cref="HttpClient"/> descarta o <c>Authorization</c> ao mudar de host, e um token de
/// acesso vazado para o host errado é pior do que uma requisição que falha.
/// </para>
/// </remarks>
public sealed class CalendarRestClient
{
    /// <summary>
    /// Teto de leitura do corpo.
    /// </summary>
    /// <remarks>
    /// Mesma defesa do <c>AutoconfigFetcher</c> e do <c>CalDavTransport</c>. Generoso: uma
    /// página de eventos com corpo e participantes cabe com folga.
    /// </remarks>
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>Quantos redirecionamentos são seguidos antes de desistir.</summary>
    private const int MaxRedirects = 5;

    private readonly HttpClient _httpClient;
    private readonly IOAuthProviderRegistry _oauthProviders;
    private readonly ILogger<CalendarRestClient> _logger;

    public CalendarRestClient(
        HttpClient httpClient,
        IOAuthProviderRegistry oauthProviders,
        ILogger<CalendarRestClient> logger)
    {
        _httpClient = httpClient;
        _oauthProviders = oauthProviders;
        _logger = logger;
    }

    /// <summary>
    /// Monta o header de autenticação da conta para os escopos informados.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> quando a conta não usa OAuth, quando o provedor não está
    /// registrado ou quando o consentimento não vale mais.
    /// </returns>
    public async Task<AuthenticationHeaderValue?> BuildAuthenticationAsync(
        Account account, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        if (account.AuthenticationType != Domain.Enums.AuthenticationType.OAuth2)
        {
            // Graph e Google Calendar recusam qualquer coisa que não seja OAuth 2.0. Senha
            // aqui não é configuração incompleta: é configuração impossível.
            return null;
        }

        var provider = _oauthProviders.Resolve(account.OAuthProvider);

        if (provider is null)
        {
            return null;
        }

        try
        {
            var token = await provider
                .GetAccessTokenAsync(account.EmailAddress.Value, scopes, cancellationToken)
                .ConfigureAwait(false);

            return new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }
        catch (ReauthenticationRequiredException)
        {
            // O usuário nunca consentiu com a agenda, ou o consentimento foi revogado. A
            // conta de e-mail segue funcionando; a agenda espera o consentimento.
            _logger.LogInformation(
                "A conta {AccountId} não tem consentimento válido para o servidor de agenda.",
                account.Id);

            return null;
        }
    }

    /// <summary>Emite uma requisição, seguindo redirecionamentos à mão.</summary>
    public async Task<RestResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        AuthenticationHeaderValue? authentication,
        string? json,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var current = uri;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (current.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"O servidor de agenda redirecionou para um endereço não-HTTPS ({current.Scheme}).");
            }

            using var request = new HttpRequestMessage(method, current);
            request.Headers.Authorization = authentication;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (json is not null)
            {
                // Só o tipo, sem parâmetro: o StringContent lança FormatException quando o
                // media type traz um ';', e a codificação já acrescenta o charset.
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode)
                && response.Headers.Location is { } location
                && hop < MaxRedirects)
            {
                current = new Uri(current, location);
                continue;
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            return new RestResponse(response.StatusCode, body, ReadETag(response));
        }

        throw new InvalidOperationException(
            $"O servidor de agenda excedeu {MaxRedirects} redirecionamentos.");
    }

    /// <summary>
    /// Lê o ETag sem passar pela propriedade tipada.
    /// </summary>
    /// <remarks>
    /// Mesma armadilha do CalDAV: a propriedade tipada lança <see cref="FormatException"/>
    /// em valor fora da norma. O Graph devolve o dele em <c>@odata.etag</c>, no corpo, mas
    /// alguns caminhos também emitem o header.
    /// </remarks>
    private static string? ReadETag(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("ETag", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
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

/// <summary>Leitura defensiva de JSON vindo da rede.</summary>
internal static class JsonReader
{
    /// <summary>Lê uma propriedade de texto, ou <see langword="null"/>.</summary>
    internal static string? Text(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>Lê uma propriedade booleana, com padrão.</summary>
    internal static bool Bool(this JsonElement element, string name, bool fallback = false)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

    /// <summary>Lê um objeto aninhado.</summary>
    internal static JsonElement? Object(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : null;

    /// <summary>Percorre um vetor, ou nada quando a propriedade não é um vetor.</summary>
    internal static IEnumerable<JsonElement> Array(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : [];

    /// <summary>
    /// Lê um instante em ISO 8601.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeStyles.RoundtripKind"/> preserva o deslocamento declarado em vez de
    /// converter para o fuso da máquina — que com <c>InvariantGlobalization</c> ligado é a
    /// única leitura confiável.
    /// </remarks>
    internal static DateTimeOffset? Timestamp(this JsonElement element, string name)
        => element.Text(name) is { } raw
            && DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;
}
