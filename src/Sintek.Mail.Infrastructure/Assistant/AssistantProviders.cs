using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Infrastructure.Assistant;

/// <summary>
/// Assistente executado na própria máquina, por um runtime local com API no formato
/// OpenAI (Ollama, LM Studio, llama.cpp).
/// </summary>
/// <remarks>
/// É o provedor padrão porque nada trafega: o conteúdo da mensagem não sai do computador,
/// e é isso que lhe dá o direito de funcionar sem consentimento por diretório.
/// </remarks>
public sealed class LocalAssistantProvider : IAssistantProvider
{
    private readonly ChatCompletionClient _client;
    private readonly LocalAssistantOptions _options;

    public LocalAssistantProvider(IHttpClientFactory httpClientFactory, IOptions<AssistantOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Local;

        var httpClient = httpClientFactory.CreateClient(nameof(LocalAssistantProvider));
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
        _client = new ChatCompletionClient(httpClient);
    }

    /// <inheritdoc />
    public string Id => "local";

    /// <inheritdoc />
    public string DisplayName => "Modelo local";

    /// <inheritdoc />
    public AssistantLocality Locality => AssistantLocality.Local;

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(_options.Endpoint)
            ? Task.FromResult(false)
            : _client.IsReachableAsync(_options.Endpoint, cancellationToken);

    /// <inheritdoc />
    public Task<AssistantResponse> CompleteAsync(
        AssistantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(_options.Endpoint)
            ? Task.FromResult(AssistantResponse.Failure("O modelo local não está configurado."))
            : _client.CompleteAsync(_options.Endpoint, _options.Model, null, request, cancellationToken);
    }
}

/// <summary>
/// Assistente em serviço externo. Opcional, desligado por padrão e sujeito ao
/// consentimento do Diretório de Domínio.
/// </summary>
/// <remarks>
/// A chave sai do cofre do sistema a cada chamada, nunca de arquivo de configuração — a
/// mesma regra das senhas de conta. Sem chave, o provedor se declara indisponível, e a
/// interface o apresenta como "não configurado" em vez de falhar na hora do uso.
/// </remarks>
public sealed class CloudAssistantProvider : IAssistantProvider
{
    private readonly ChatCompletionClient _client;
    private readonly CloudAssistantOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<CloudAssistantProvider> _logger;

    public CloudAssistantProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<AssistantOptions> options,
        ICredentialStore credentials,
        ILogger<CloudAssistantProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Cloud;
        _credentials = credentials;
        _logger = logger;

        var httpClient = httpClientFactory.CreateClient(nameof(CloudAssistantProvider));
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
        _client = new ChatCompletionClient(httpClient);
    }

    /// <inheritdoc />
    public string Id => "cloud";

    /// <inheritdoc />
    public string DisplayName => _options.DisplayName;

    /// <inheritdoc />
    public AssistantLocality Locality => AssistantLocality.Cloud;

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.Model))
        {
            return false;
        }

        return await _credentials.ExistsAsync(_options.CredentialKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AssistantResponse> CompleteAsync(
        AssistantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = await _credentials.GetSecretAsync(_options.CredentialKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("O provedor de IA em nuvem não tem credencial configurada.");
            return AssistantResponse.Failure("O serviço de IA em nuvem não está configurado.");
        }

        return await _client
            .CompleteAsync(_options.Endpoint, _options.Model, apiKey, request, cancellationToken)
            .ConfigureAwait(false);
    }
}
