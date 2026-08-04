using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Regra automática aplicada às mensagens que chegam.
/// </summary>
/// <remarks>
/// As regras são avaliadas em ordem crescente de <see cref="Priority"/>. Uma regra com
/// a ação <see cref="RuleActionType.StopProcessing"/> — ou com
/// <see cref="StopProcessing"/> ligado — encerra a cadeia, como no Outlook.
/// </remarks>
public sealed class Rule : Entity
{
    private readonly List<RuleCondition> _conditions = [];
    private readonly List<RuleAction> _actions = [];

    private Rule(Guid id, string name, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
    }

    private Rule()
    {
    }

    /// <summary>Nome da regra, exibido na lista de regras.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Conta à qual a regra se aplica. Nulo significa todas as contas.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>
    /// Diretório de Domínio ao qual a regra se aplica. Nulo significa todos.
    /// </summary>
    public Guid? DomainDirectoryId { get; private set; }

    /// <summary>Ordem de avaliação. Menor valor é avaliado primeiro.</summary>
    public int Priority { get; private set; }

    /// <summary>Como combinar as condições.</summary>
    public RuleMatchType MatchType { get; private set; } = RuleMatchType.All;

    /// <summary>Se a regra está ativa.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Se as regras seguintes devem ser ignoradas quando esta for satisfeita.</summary>
    public bool StopProcessing { get; private set; }

    /// <summary>Condições que precisam ser satisfeitas.</summary>
    public IReadOnlyCollection<RuleCondition> Conditions => _conditions;

    /// <summary>Ações executadas quando a regra é satisfeita.</summary>
    public IReadOnlyCollection<RuleAction> Actions => _actions;

    /// <summary>Cria uma regra.</summary>
    public static Rule Create(
        string name,
        DateTimeOffset createdAt,
        Guid? accountId = null,
        Guid? domainDirectoryId = null,
        int priority = 0,
        RuleMatchType matchType = RuleMatchType.All,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Rule(id ?? Guid.CreateVersion7(), name.Trim(), createdAt)
        {
            AccountId = accountId,
            DomainDirectoryId = domainDirectoryId,
            Priority = priority,
            MatchType = matchType,
        };
    }

    /// <summary>Adiciona uma condição.</summary>
    public RuleCondition AddCondition(
        RuleField field,
        RuleOperator @operator,
        string? value,
        DateTimeOffset now,
        bool isCaseSensitive = false)
    {
        var condition = RuleCondition.Create(Id, field, @operator, value, now, isCaseSensitive);
        _conditions.Add(condition);
        Touch(now);
        return condition;
    }

    /// <summary>Adiciona uma ação.</summary>
    public RuleAction AddAction(
        RuleActionType actionType,
        DateTimeOffset now,
        Guid? targetFolderId = null,
        Guid? targetCategoryId = null,
        string? value = null)
    {
        var action = RuleAction.Create(Id, actionType, now, targetFolderId, targetCategoryId, value);
        _actions.Add(action);

        if (actionType == RuleActionType.StopProcessing)
        {
            StopProcessing = true;
        }

        Touch(now);
        return action;
    }

    /// <summary>Atualiza os metadados da regra.</summary>
    public void Update(string name, int priority, RuleMatchType matchType, bool stopProcessing, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        Priority = priority;
        MatchType = matchType;
        StopProcessing = stopProcessing;
        Touch(now);
    }

    /// <summary>Ativa ou desativa a regra.</summary>
    public void SetEnabled(bool isEnabled, DateTimeOffset now)
    {
        IsEnabled = isEnabled;
        Touch(now);
    }

    /// <summary>Remove todas as condições e ações, para reconstruir a regra.</summary>
    public void ClearDefinition(DateTimeOffset now)
    {
        _conditions.Clear();
        _actions.Clear();
        StopProcessing = false;
        Touch(now);
    }
}

/// <summary>Uma condição de uma <see cref="Rule"/>.</summary>
public sealed class RuleCondition : Entity
{
    private RuleCondition(
        Guid id,
        Guid ruleId,
        RuleField field,
        RuleOperator @operator,
        string? value,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        RuleId = ruleId;
        Field = field;
        Operator = @operator;
        Value = value;
    }

    private RuleCondition()
    {
    }

    /// <summary>Regra dona da condição.</summary>
    public Guid RuleId { get; private set; }

    /// <summary>Regra dona da condição.</summary>
    public Rule? Rule { get; private set; }

    /// <summary>Campo avaliado.</summary>
    public RuleField Field { get; private set; }

    /// <summary>Operador de comparação.</summary>
    public RuleOperator Operator { get; private set; }

    /// <summary>Valor comparado. Nulo para operadores booleanos.</summary>
    public string? Value { get; private set; }

    /// <summary>Se a comparação diferencia maiúsculas de minúsculas.</summary>
    public bool IsCaseSensitive { get; private set; }

    internal static RuleCondition Create(
        Guid ruleId,
        RuleField field,
        RuleOperator @operator,
        string? value,
        DateTimeOffset createdAt,
        bool isCaseSensitive)
        => new(Guid.CreateVersion7(), ruleId, field, @operator, value, createdAt)
        {
            IsCaseSensitive = isCaseSensitive,
        };
}

/// <summary>Uma ação de uma <see cref="Rule"/>.</summary>
public sealed class RuleAction : Entity
{
    private RuleAction(Guid id, Guid ruleId, RuleActionType actionType, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        RuleId = ruleId;
        ActionType = actionType;
    }

    private RuleAction()
    {
    }

    /// <summary>Regra dona da ação.</summary>
    public Guid RuleId { get; private set; }

    /// <summary>Regra dona da ação.</summary>
    public Rule? Rule { get; private set; }

    /// <summary>O que fazer.</summary>
    public RuleActionType ActionType { get; private set; }

    /// <summary>Pasta de destino, para ações de mover e copiar.</summary>
    public Guid? TargetFolderId { get; private set; }

    /// <summary>Categoria aplicada, para a ação de categorizar.</summary>
    public Guid? TargetCategoryId { get; private set; }

    /// <summary>Parâmetro livre — o endereço de destino, no caso de encaminhamento.</summary>
    public string? Value { get; private set; }

    internal static RuleAction Create(
        Guid ruleId,
        RuleActionType actionType,
        DateTimeOffset createdAt,
        Guid? targetFolderId,
        Guid? targetCategoryId,
        string? value)
        => new(Guid.CreateVersion7(), ruleId, actionType, createdAt)
        {
            TargetFolderId = targetFolderId,
            TargetCategoryId = targetCategoryId,
            Value = value,
        };
}
