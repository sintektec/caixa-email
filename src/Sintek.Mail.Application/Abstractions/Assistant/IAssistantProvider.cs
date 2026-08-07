namespace Sintek.Mail.Application.Abstractions.Assistant;

/// <summary>Onde o processamento de IA acontece.</summary>
public enum AssistantLocality
{
    /// <summary>
    /// Modelo executado na própria máquina. Nada trafega, e por isso pode ficar ligado
    /// sem perguntar.
    /// </summary>
    Local = 0,

    /// <summary>
    /// Serviço externo. Exige consentimento do Diretório de Domínio e cada envio é
    /// registrado em auditoria.
    /// </summary>
    Cloud = 1,
}

/// <summary>O que se pede ao assistente.</summary>
public enum AssistantTask
{
    /// <summary>Resumir uma mensagem longa ou uma conversa.</summary>
    Summarize = 0,

    /// <summary>Sugerir uma resposta.</summary>
    SuggestReply = 1,

    /// <summary>Reescrever ou melhorar um texto do compositor.</summary>
    Rewrite = 2,
}

/// <summary>
/// Um pedido ao assistente.
/// </summary>
/// <param name="Task">O que fazer.</param>
/// <param name="Content">Texto a processar — conteúdo de mensagem, quase sempre.</param>
/// <param name="Instruction">Orientação adicional livre, quando houver.</param>
public sealed record AssistantRequest(AssistantTask Task, string Content, string? Instruction = null);

/// <summary>
/// Resposta do assistente.
/// </summary>
/// <param name="Succeeded">Se o provedor conseguiu produzir a resposta.</param>
/// <param name="Text">Texto gerado.</param>
/// <param name="ErrorMessage">Explicação da falha, redigida para o usuário.</param>
public readonly record struct AssistantResponse(bool Succeeded, string Text, string? ErrorMessage)
{
    /// <summary>Resposta bem-sucedida.</summary>
    public static AssistantResponse Success(string text) => new(true, text, null);

    /// <summary>Falha com explicação.</summary>
    public static AssistantResponse Failure(string errorMessage) => new(false, string.Empty, errorMessage);
}

/// <summary>
/// Um provedor de assistência por IA.
/// </summary>
/// <remarks>
/// <para>
/// A abstração existe para que a decisão "onde o conteúdo é processado" seja explícita e
/// verificável: <see cref="Locality"/> é o que o guardião de consentimento consulta antes
/// de deixar qualquer texto passar.
/// </para>
/// <para>
/// Implementações nunca decidem sozinhas se podem rodar. Quem autoriza é
/// <c>AssistantGateway</c>, que consulta o Diretório de Domínio da conta.
/// </para>
/// </remarks>
public interface IAssistantProvider
{
    /// <summary>Identificador estável, usado na auditoria e na configuração.</summary>
    string Id { get; }

    /// <summary>Nome exibido ao usuário.</summary>
    string DisplayName { get; }

    /// <summary>Onde o processamento acontece.</summary>
    AssistantLocality Locality { get; }

    /// <summary>
    /// Se o provedor está pronto para uso — modelo baixado, credencial configurada.
    /// </summary>
    /// <remarks>
    /// Provedor indisponível é estado normal, não erro: o modelo local pode não ter sido
    /// baixado ainda, e o de nuvem pode não ter chave configurada. A interface apresenta
    /// isso como "não configurado", nunca como falha.
    /// </remarks>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Processa o pedido.</summary>
    Task<AssistantResponse> CompleteAsync(
        AssistantRequest request, CancellationToken cancellationToken = default);
}
