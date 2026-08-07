using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Rules;

/// <summary>Uma condição, como o editor de regras a descreve.</summary>
public sealed record RuleConditionDefinition(
    RuleField Field, RuleOperator Operator, string? Value, bool IsCaseSensitive = false);

/// <summary>Uma ação, como o editor de regras a descreve.</summary>
public sealed record RuleActionDefinition(
    RuleActionType ActionType,
    Guid? TargetFolderId = null,
    Guid? TargetCategoryId = null,
    string? Value = null);

/// <summary>A regra inteira, como o editor a envia para gravação.</summary>
public sealed record RuleDefinition
{
    /// <summary>Regra existente a atualizar; nulo cria uma nova.</summary>
    public Guid? RuleId { get; init; }

    /// <summary>Nome exibido.</summary>
    public required string Name { get; init; }

    /// <summary>Conta à qual se aplica. Nulo significa todas.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>Diretório de Domínio ao qual se aplica. Nulo significa todos.</summary>
    public Guid? DomainDirectoryId { get; init; }

    /// <summary>Ordem de avaliação.</summary>
    public int Priority { get; init; }

    /// <summary>Como combinar as condições.</summary>
    public RuleMatchType MatchType { get; init; } = RuleMatchType.All;

    /// <summary>Se interrompe as regras seguintes ao ser satisfeita.</summary>
    public bool StopProcessing { get; init; }

    /// <summary>Condições.</summary>
    public IReadOnlyList<RuleConditionDefinition> Conditions { get; init; } = [];

    /// <summary>Ações.</summary>
    public IReadOnlyList<RuleActionDefinition> Actions { get; init; } = [];
}

/// <summary>Resultado da gravação de uma regra.</summary>
public readonly record struct SaveRuleResult(bool Succeeded, Guid? RuleId, string? ErrorMessage);

/// <summary>
/// Gestão das regras automáticas: listar, gravar, ativar e excluir.
/// </summary>
/// <remarks>
/// A gravação reconstrói condições e ações do zero a partir da definição: manter diffs
/// item a item dobraria o código do editor para preservar identidades que nada referencia.
/// </remarks>
public sealed class ManageRulesHandler
{
    private readonly IRuleRepository _rules;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ManageRulesHandler(IRuleRepository rules, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _rules = rules;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Lista todas as regras, em ordem de prioridade.</summary>
    public Task<IReadOnlyList<Rule>> ListAsync(CancellationToken cancellationToken = default)
        => _rules.ListAsync(cancellationToken);

    /// <summary>Grava uma regra nova ou reconstrói uma existente.</summary>
    public async Task<SaveRuleResult> SaveAsync(
        RuleDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            return new SaveRuleResult(false, null, "Dê um nome à regra.");
        }

        if (definition.Actions.Count == 0)
        {
            return new SaveRuleResult(false, null, "A regra precisa de ao menos uma ação.");
        }

        foreach (var action in definition.Actions)
        {
            var error = ValidateAction(action);
            if (error is not null)
            {
                return new SaveRuleResult(false, null, error);
            }
        }

        var now = _timeProvider.GetUtcNow();
        Rule rule;

        if (definition.RuleId is { } ruleId)
        {
            var existing = await _rules.GetByIdAsync(ruleId, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                return new SaveRuleResult(false, null, "A regra não existe mais.");
            }

            existing.Update(
                definition.Name, definition.Priority, definition.MatchType,
                definition.StopProcessing, now);
            existing.ClearDefinition(now);
            rule = existing;
        }
        else
        {
            rule = Rule.Create(
                definition.Name, now,
                definition.AccountId, definition.DomainDirectoryId,
                definition.Priority, definition.MatchType);
            rule.Update(
                definition.Name, definition.Priority, definition.MatchType,
                definition.StopProcessing, now);
            await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);
        }

        foreach (var condition in definition.Conditions)
        {
            rule.AddCondition(
                condition.Field, condition.Operator, condition.Value, now, condition.IsCaseSensitive);
        }

        foreach (var action in definition.Actions)
        {
            rule.AddAction(
                action.ActionType, now, action.TargetFolderId, action.TargetCategoryId, action.Value);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SaveRuleResult(true, rule.Id, null);
    }

    /// <summary>Ativa ou desativa uma regra.</summary>
    public async Task<bool> SetEnabledAsync(
        Guid ruleId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var rule = await _rules.GetByIdAsync(ruleId, cancellationToken).ConfigureAwait(false);

        if (rule is null)
        {
            return false;
        }

        rule.SetEnabled(isEnabled, _timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Exclui uma regra.</summary>
    public async Task<bool> DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await _rules.GetByIdAsync(ruleId, cancellationToken).ConfigureAwait(false);

        if (rule is null)
        {
            return false;
        }

        _rules.Remove(rule);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string? ValidateAction(RuleActionDefinition action) => action.ActionType switch
    {
        RuleActionType.MoveToFolder or RuleActionType.CopyToFolder when action.TargetFolderId is null
            => "A ação de mover ou copiar precisa de uma pasta de destino.",
        RuleActionType.ApplyCategory when action.TargetCategoryId is null
            => "A ação de categorizar precisa de uma categoria.",
        RuleActionType.Forward when string.IsNullOrWhiteSpace(action.Value)
            => "A ação de encaminhar precisa de um endereço de destino.",
        _ => null,
    };
}
