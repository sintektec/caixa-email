using System.Net;
using Microsoft.Extensions.Logging;

namespace Sintek.Mail.Infrastructure.Mail.Autodiscover;

/// <summary>Busca documentos de autoconfiguração por HTTPS.</summary>
/// <remarks>
/// <para>
/// Duas defesas contra um servidor hostil. A primeira é o teto de leitura: sem ele, um
/// host que respondesse um fluxo infinito consumiria a memória do processo — e o endereço
/// consultado é escolhido pelo domínio do e-mail que o usuário digitou, então não é um host
/// de confiança.
/// </para>
/// <para>
/// A segunda é exigir HTTPS. O formato do Thunderbird admite HTTP em claro no arquivo do
/// próprio domínio; aceitar isso permitiria a quem estivesse no caminho da rede devolver
/// uma configuração apontando para o servidor dele, e o usuário digitaria a senha lá.
/// </para>
/// </remarks>
public sealed class AutoconfigFetcher
{
    /// <summary>Teto de leitura do documento.</summary>
    /// <remarks>
    /// O maior registro real do ISPDB não chega a 8 KiB; 256 KiB dá margem larga e ainda
    /// assim é irrisório diante da memória do processo.
    /// </remarks>
    private const int MaxDocumentBytes = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AutoconfigFetcher> _logger;

    public AutoconfigFetcher(HttpClient httpClient, ILogger<AutoconfigFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Baixa o documento, ou devolve <see langword="null"/> em qualquer falha — endereço
    /// inexistente, tempo esgotado, resposta grande demais ou conteúdo inválido.
    /// </summary>
    /// <remarks>
    /// Falhar em silêncio é deliberado: cada fonte é uma tentativa entre várias, e a
    /// ausência de uma não é erro que interesse ao usuário. O que interessa a ele é o
    /// resultado final da descoberta.
    /// </remarks>
    public async Task<string?> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A autoconfiguração só é buscada por HTTPS.", nameof(uri));
        }

        try
        {
            using var response = await _httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                _logger.LogDebug("Autoconfiguração em {Uri} excede o tamanho aceito.", uri);
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[MaxDocumentBytes];
            var total = 0;

            while (total < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                }

                total += read;
            }

            // O teto foi atingido sem o fim do fluxo: o documento é maior do que o aceito.
            _logger.LogDebug("Autoconfiguração em {Uri} excede o tamanho aceito.", uri);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Autoconfiguração em {Uri} indisponível.", uri);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Autoconfiguração em {Uri} esgotou o tempo.", uri);
            return null;
        }
    }
}
