using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sintek.Mail.Application.Abstractions.Assistant;

namespace Sintek.Mail.Infrastructure.Assistant;

/// <summary>
/// Fala com uma API de conclusão de chat no formato OpenAI — o mesmo contrato que Ollama,
/// LM Studio, llama.cpp e os serviços em nuvem expõem.
/// </summary>
/// <remarks>
/// Um único cliente serve local e nuvem porque o protocolo é o mesmo; o que muda é o
/// endereço, o modelo e a existência de credencial. Duplicar isso em duas classes faria as
/// duas divergirem na primeira correção de bug.
/// </remarks>
internal sealed class ChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;

    public ChatCompletionClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>Envia o pedido e devolve o texto gerado.</summary>
    public async Task<AssistantResponse> CompleteAsync(
        string endpoint,
        string model,
        string? apiKey,
        AssistantRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new ChatRequest(
                    model,
                    [
                        new ChatMessage("system", SystemPromptFor(request.Task)),
                        new ChatMessage("user", BuildUserPrompt(request)),
                    ],
                    Stream: false),
                options: SerializerOptions),
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", apiKey);
        }

        try
        {
            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // O corpo da resposta de erro pode ecoar o prompt; fica de fora da mensagem
                // exibida e do log pelo mesmo motivo que a auditoria não guarda conteúdo.
                return AssistantResponse.Failure(
                    $"O assistente respondeu com o código {(int)response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ChatResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            var text = payload?.Choices?.FirstOrDefault()?.Message?.Content;

            return string.IsNullOrWhiteSpace(text)
                ? AssistantResponse.Failure("O assistente devolveu uma resposta vazia.")
                : AssistantResponse.Success(text.Trim());
        }
        catch (HttpRequestException)
        {
            return AssistantResponse.Failure(
                "Não foi possível falar com o assistente. Verifique se ele está em execução.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AssistantResponse.Failure("O assistente demorou demais para responder.");
        }
    }

    /// <summary>Verifica se o endereço responde, sem gastar uma geração.</summary>
    public async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            // Uma requisição à raiz do host basta: qualquer resposta HTTP prova que há
            // alguém escutando. O código não importa — 404 na raiz é resposta.
            using var probe = new HttpRequestMessage(
                HttpMethod.Head, new Uri(uri.GetLeftPart(UriPartial.Authority)));

            using var response = await _httpClient.SendAsync(probe, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Instrução de sistema por tarefa.
    /// </summary>
    /// <remarks>
    /// Em português e explícita sobre não inventar: o resumo de um e-mail que acrescenta
    /// um prazo que não estava lá é pior que resumo nenhum.
    /// </remarks>
    private static string SystemPromptFor(AssistantTask task) => task switch
    {
        AssistantTask.Summarize =>
            "Você resume mensagens de e-mail em português do Brasil. Produza um resumo curto, " +
            "em tópicos, cobrindo o assunto, o que foi pedido e os prazos citados. Use apenas o " +
            "que está no texto; não invente informação que não esteja lá.",

        AssistantTask.SuggestReply =>
            "Você redige respostas de e-mail em português do Brasil, no tom profissional e " +
            "cordial usado no ambiente corporativo brasileiro. Responda apenas com o corpo da " +
            "mensagem, sem assunto e sem assinatura.",

        AssistantTask.Rewrite =>
            "Você reescreve textos de e-mail em português do Brasil, mantendo o sentido e " +
            "melhorando clareza e correção. Responda apenas com o texto reescrito.",

        _ => "Você é um assistente de e-mail e responde em português do Brasil.",
    };

    private static string BuildUserPrompt(AssistantRequest request)
        => string.IsNullOrWhiteSpace(request.Instruction)
            ? request.Content
            : $"{request.Instruction}\n\n---\n\n{request.Content}";

    private sealed record ChatRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        bool Stream);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
