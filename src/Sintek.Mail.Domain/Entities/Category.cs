using Sintek.Mail.Domain.Common;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Categoria colorida aplicável a mensagens, no modelo das categorias do Outlook.
/// </summary>
public sealed class Category : Entity
{
    private Category(Guid id, string name, string colorHex, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
        ColorHex = colorHex;
    }

    private Category()
    {
    }

    /// <summary>Nome da categoria.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Cor no formato <c>#RRGGBB</c>.</summary>
    public string ColorHex { get; private set; } = "#0078D4";

    /// <summary>
    /// Conta à qual a categoria pertence. Nulo significa categoria global, visível em
    /// todas as contas.
    /// </summary>
    public Guid? AccountId { get; private set; }

    /// <summary>Tecla de atalho opcional (1 a 9), como no Outlook.</summary>
    public int? Shortcut { get; private set; }

    /// <summary>Posição manual na lista de categorias.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Cria uma categoria.</summary>
    public static Category Create(
        string name,
        string colorHex,
        DateTimeOffset createdAt,
        Guid? accountId = null,
        int? shortcut = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Category(id ?? Guid.CreateVersion7(), name.Trim(), NormalizeColor(colorHex), createdAt)
        {
            AccountId = accountId,
            Shortcut = shortcut,
        };
    }

    /// <summary>Atualiza nome, cor e atalho.</summary>
    public void Update(string name, string colorHex, int? shortcut, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        ColorHex = NormalizeColor(colorHex);
        Shortcut = shortcut;
        Touch(now);
    }

    /// <summary>Define a posição manual.</summary>
    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        Touch(now);
    }

    /// <summary>
    /// Normaliza a cor para <c>#RRGGBB</c> maiúsculo, recusando valores inválidos.
    /// </summary>
    private static string NormalizeColor(string? colorHex)
    {
        const string fallback = "#0078D4";

        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return fallback;
        }

        var value = colorHex.Trim();
        if (!value.StartsWith('#'))
        {
            value = '#' + value;
        }

        if (value.Length != 7)
        {
            return fallback;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return fallback;
            }
        }

        return value.ToUpperInvariant();
    }
}

/// <summary>
/// Associação entre uma mensagem e uma categoria.
/// </summary>
public sealed class MessageCategory
{
    private MessageCategory(Guid messageId, Guid categoryId, DateTimeOffset appliedAt)
    {
        MessageId = messageId;
        CategoryId = categoryId;
        AppliedAt = appliedAt;
    }

    private MessageCategory()
    {
    }

    /// <summary>Mensagem categorizada.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Mensagem categorizada.</summary>
    public Message? Message { get; private set; }

    /// <summary>Categoria aplicada.</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>Categoria aplicada.</summary>
    public Category? Category { get; private set; }

    /// <summary>Quando a categoria foi aplicada.</summary>
    public DateTimeOffset AppliedAt { get; private set; }

    /// <summary>Cria a associação.</summary>
    public static MessageCategory Create(Guid messageId, Guid categoryId, DateTimeOffset appliedAt)
        => new(messageId, categoryId, appliedAt);
}
