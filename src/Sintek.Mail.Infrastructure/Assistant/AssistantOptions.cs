namespace Sintek.Mail.Infrastructure.Assistant;

/// <summary>Configuração da assistência por IA.</summary>
/// <remarks>
/// Nenhuma chave de API mora aqui. O provedor em nuvem lê a dele do
/// <c>ICredentialStore</c>, pela mesma regra que vale para senha de conta: segredo não
/// entra em arquivo de configuração nem no banco.
/// </remarks>
public sealed class AssistantOptions
{
    /// <summary>Nome da seção em arquivo de configuração.</summary>
    public const string SectionName = "Assistant";

    /// <summary>Modelo local.</summary>
    public LocalAssistantOptions Local { get; set; } = new();

    /// <summary>Serviço em nuvem.</summary>
    public CloudAssistantOptions Cloud { get; set; } = new();
}

/// <summary>
/// Modelo local, servido por um runtime na própria máquina.
/// </summary>
/// <remarks>
/// A integração é por HTTP com a API no formato OpenAI, que Ollama, LM Studio e
/// llama.cpp expõem igual. Ganha-se compatibilidade com o que o usuário já tenha
/// instalado, sem embutir um runtime nativo de centenas de megabytes no instalador.
/// </remarks>
public sealed class LocalAssistantOptions
{
    /// <summary>Endereço do runtime local. Vazio desliga o provedor.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:11434/v1/chat/completions";

    /// <summary>Nome do modelo a usar.</summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>Tempo máximo de espera, em segundos.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Serviço de IA em nuvem, opcional e desligado por padrão.</summary>
public sealed class CloudAssistantOptions
{
    /// <summary>Endereço da API, no formato OpenAI.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Nome do modelo.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Nome exibido do serviço, para a auditoria e a interface.</summary>
    public string DisplayName { get; set; } = "Serviço de IA em nuvem";

    /// <summary>
    /// Chave sob a qual a credencial está guardada no cofre do sistema.
    /// </summary>
    /// <remarks>
    /// É o identificador, não o segredo: o valor sai do <c>ICredentialStore</c> em tempo
    /// de execução, como acontece com senha de conta e chave do banco.
    /// </remarks>
    public string CredentialKey { get; set; } = "sintek-mail/assistant/cloud";

    /// <summary>Tempo máximo de espera, em segundos.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
