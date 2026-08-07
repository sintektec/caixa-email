using Sintek.Mail.Domain.Common;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Conversa: agrupa as mensagens de uma mesma troca.
/// </summary>
public sealed class MessageThread : Entity
{
    private MessageThread(Guid id, string subjectNormalized, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        SubjectNormalized = subjectNormalized;
        LastMessageAt = createdAt;
    }

    private MessageThread()
    {
    }

    /// <summary>Assunto normalizado que identifica a conversa.</summary>
    public string SubjectNormalized { get; private set; } = string.Empty;

    /// <summary>Conta dona da conversa.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Quantas mensagens a conversa tem.</summary>
    public int MessageCount { get; private set; }

    /// <summary>Instante da mensagem mais recente, usado para ordenar a listagem.</summary>
    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>Cria uma conversa.</summary>
    public static MessageThread Create(
        Guid accountId,
        string subjectNormalized,
        DateTimeOffset createdAt,
        Guid? id = null)
        => new(id ?? Guid.CreateVersion7(), subjectNormalized ?? string.Empty, createdAt)
        {
            AccountId = accountId,
        };

    /// <summary>Atualiza os contadores após incluir uma mensagem.</summary>
    public void RegisterMessage(DateTimeOffset messageAt, DateTimeOffset now)
    {
        MessageCount++;
        if (messageAt > LastMessageAt)
        {
            LastMessageAt = messageAt;
        }

        Touch(now);
    }
}

/// <summary>
/// Pesquisa salva pelo usuário, para reutilizar filtros frequentes.
/// </summary>
public sealed class SavedSearch : Entity
{
    private SavedSearch(Guid id, string name, string queryJson, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
        QueryJson = queryJson;
    }

    private SavedSearch()
    {
    }

    /// <summary>Nome exibido na árvore de navegação.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Critérios da pesquisa serializados em JSON.</summary>
    public string QueryJson { get; private set; } = "{}";

    /// <summary>Se aparece fixada na barra lateral.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Posição manual na lista.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Cria uma pesquisa salva.</summary>
    public static SavedSearch Create(
        string name,
        string queryJson,
        DateTimeOffset createdAt,
        bool isPinned = false,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new SavedSearch(id ?? Guid.CreateVersion7(), name.Trim(), queryJson, createdAt)
        {
            IsPinned = isPinned,
        };
    }

    /// <summary>Atualiza nome e critérios.</summary>
    public void Update(string name, string queryJson, bool isPinned, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        QueryJson = queryJson;
        IsPinned = isPinned;
        Touch(now);
    }
}

/// <summary>
/// Assinatura de e-mail vinculada a uma conta.
/// </summary>
public sealed class Signature : Entity
{
    private Signature(Guid id, Guid accountId, string name, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        Name = name;
    }

    private Signature()
    {
    }

    /// <summary>Conta dona da assinatura.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Conta dona da assinatura.</summary>
    public Account? Account { get; private set; }

    /// <summary>Nome da assinatura.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Conteúdo em HTML.</summary>
    public string HtmlContent { get; private set; } = string.Empty;

    /// <summary>Conteúdo em texto puro, para mensagens sem HTML.</summary>
    public string TextContent { get; private set; } = string.Empty;

    /// <summary>Se é a assinatura padrão de mensagens novas.</summary>
    public bool IsDefaultForNew { get; private set; }

    /// <summary>Se é a assinatura padrão de respostas e encaminhamentos.</summary>
    public bool IsDefaultForReply { get; private set; }

    /// <summary>Cria uma assinatura.</summary>
    public static Signature Create(
        Guid accountId,
        string name,
        string htmlContent,
        string textContent,
        DateTimeOffset createdAt,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Signature(id ?? Guid.CreateVersion7(), accountId, name.Trim(), createdAt)
        {
            HtmlContent = htmlContent ?? string.Empty,
            TextContent = textContent ?? string.Empty,
        };
    }

    /// <summary>Atualiza o conteúdo.</summary>
    public void Update(string name, string htmlContent, string textContent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        HtmlContent = htmlContent ?? string.Empty;
        TextContent = textContent ?? string.Empty;
        Touch(now);
    }

    /// <summary>Define os padrões de uso.</summary>
    public void SetDefaults(bool isDefaultForNew, bool isDefaultForReply, DateTimeOffset now)
    {
        IsDefaultForNew = isDefaultForNew;
        IsDefaultForReply = isDefaultForReply;
        Touch(now);
    }
}

/// <summary>
/// Modelo de mensagem reutilizável.
/// </summary>
public sealed class MessageTemplate : Entity
{
    private MessageTemplate(Guid id, string name, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
    }

    private MessageTemplate()
    {
    }

    /// <summary>Nome do modelo.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Assunto pré-preenchido.</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>Corpo em HTML.</summary>
    public string HtmlBody { get; private set; } = string.Empty;

    /// <summary>Conta à qual o modelo pertence. Nulo significa disponível em todas.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>Cria um modelo.</summary>
    public static MessageTemplate Create(
        string name,
        string subject,
        string htmlBody,
        DateTimeOffset createdAt,
        Guid? accountId = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new MessageTemplate(id ?? Guid.CreateVersion7(), name.Trim(), createdAt)
        {
            Subject = subject ?? string.Empty,
            HtmlBody = htmlBody ?? string.Empty,
            AccountId = accountId,
        };
    }

    /// <summary>Atualiza o conteúdo do modelo.</summary>
    public void Update(string name, string subject, string htmlBody, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        Subject = subject ?? string.Empty;
        HtmlBody = htmlBody ?? string.Empty;
        Touch(now);
    }
}

/// <summary>
/// Preferência da aplicação, no formato chave/valor.
/// </summary>
/// <remarks>
/// Guarda apenas configuração de interface e comportamento (tema, política de imagens
/// remotas, ordenação). Nada sensível: segredos vivem no Credential Manager.
/// </remarks>
public sealed class AppSetting : Entity
{
    private AppSetting(Guid id, string key, string? value, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Key = key;
        Value = value;
    }

    private AppSetting()
    {
    }

    /// <summary>Chave da preferência.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>Valor serializado.</summary>
    public string? Value { get; private set; }

    /// <summary>Cria uma preferência.</summary>
    public static AppSetting Create(string key, string? value, DateTimeOffset createdAt, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new AppSetting(id ?? Guid.CreateVersion7(), key.Trim(), value, createdAt);
    }

    /// <summary>Altera o valor.</summary>
    public void SetValue(string? value, DateTimeOffset now)
    {
        Value = value;
        Touch(now);
    }
}
