using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Application.UseCases.Rules;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma linha de condição no editor de regra.</summary>
public sealed partial class RuleConditionEditorViewModel : ObservableObject
{
    public RuleConditionEditorViewModel()
    {
        SelectedField = SelectionOptions.RuleFields[0];
        SelectedOperator = SelectionOptions.RuleOperators[0];
    }

    /// <summary>Campos disponíveis.</summary>
    public IReadOnlyList<RuleFieldOption> Fields => SelectionOptions.RuleFields;

    /// <summary>Operadores disponíveis.</summary>
    public IReadOnlyList<RuleOperatorOption> Operators => SelectionOptions.RuleOperators;

    /// <summary>Campo escolhido.</summary>
    [ObservableProperty]
    private RuleFieldOption _selectedField;

    /// <summary>Operador escolhido.</summary>
    [ObservableProperty]
    private RuleOperatorOption _selectedOperator;

    /// <summary>Valor comparado.</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Converte a linha em definição.</summary>
    public RuleConditionDefinition ToDefinition() => new(
        SelectedField.Value,
        SelectedOperator.Value,
        string.IsNullOrWhiteSpace(Value) ? null : Value.Trim());
}

/// <summary>Uma linha de ação no editor de regra.</summary>
public sealed partial class RuleActionEditorViewModel : ObservableObject
{
    public RuleActionEditorViewModel()
        => SelectedAction = SelectionOptions.RuleActions[0];

    /// <summary>Ações disponíveis.</summary>
    public IReadOnlyList<RuleActionOption> Actions => SelectionOptions.RuleActions;

    /// <summary>Ação escolhida.</summary>
    [ObservableProperty]
    private RuleActionOption _selectedAction;

    /// <summary>Pastas da conta, para a ação de mover.</summary>
    public ObservableCollection<ScopeFilterOption> FolderOptions { get; } = [];

    /// <summary>Categorias, para a ação de categorizar.</summary>
    public ObservableCollection<ScopeFilterOption> CategoryOptions { get; } = [];

    /// <summary>Pasta de destino escolhida.</summary>
    [ObservableProperty]
    private ScopeFilterOption? _selectedFolder;

    /// <summary>Categoria escolhida.</summary>
    [ObservableProperty]
    private ScopeFilterOption? _selectedCategory;

    /// <summary>Converte a linha em definição.</summary>
    public RuleActionDefinition ToDefinition() => new(
        SelectedAction.Value,
        TargetFolderId: SelectedFolder?.Value,
        TargetCategoryId: SelectedCategory?.Value);
}

/// <summary>Uma regra na lista de gestão.</summary>
public sealed record RuleListItemViewModel(Guid Id, string Name, int Priority, bool IsEnabled, string Summary);

/// <summary>
/// ViewModel da gestão de regras automáticas: lista, editor e exclusão.
/// </summary>
public sealed partial class RulesViewModel : ObservableObject
{
    private readonly ManageRulesHandler _manageRules;
    private readonly ManageCategoriesHandler _manageCategories;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;

    public RulesViewModel(
        ManageRulesHandler manageRules,
        ManageCategoriesHandler manageCategories,
        IAccountRepository accounts,
        IFolderRepository folders)
    {
        _manageRules = manageRules;
        _manageCategories = manageCategories;
        _accounts = accounts;
        _folders = folders;

        SelectedMatchType = SelectionOptions.RuleMatchTypes[0];
    }

    /// <summary>Regras existentes.</summary>
    public ObservableCollection<RuleListItemViewModel> Rules { get; } = [];

    /// <summary>Condições da regra em edição.</summary>
    public ObservableCollection<RuleConditionEditorViewModel> Conditions { get; } = [];

    /// <summary>Ações da regra em edição.</summary>
    public ObservableCollection<RuleActionEditorViewModel> Actions { get; } = [];

    /// <summary>Modos de combinação.</summary>
    public IReadOnlyList<RuleMatchTypeOption> MatchTypes => SelectionOptions.RuleMatchTypes;

    /// <summary>Regra em edição; nulo quando o editor cria uma nova.</summary>
    [ObservableProperty]
    private Guid? _editingRuleId;

    /// <summary>Nome da regra em edição.</summary>
    [ObservableProperty]
    private string _ruleName = string.Empty;

    /// <summary>Prioridade em edição, como texto do NumberBox.</summary>
    [ObservableProperty]
    private double _priorityValue;

    /// <summary>Modo de combinação escolhido.</summary>
    [ObservableProperty]
    private RuleMatchTypeOption _selectedMatchType;

    /// <summary>Se interrompe as regras seguintes.</summary>
    [ObservableProperty]
    private bool _stopProcessing;

    /// <summary>Aviso exibido ao usuário.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há aviso a exibir.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    private readonly List<ScopeFilterOption> _folderOptions = [];
    private readonly List<ScopeFilterOption> _categoryOptions = [];

    /// <summary>Carrega regras, pastas e categorias.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _folderOptions.Clear();
        _categoryOptions.Clear();

        foreach (var account in await _accounts.ListActiveAsync(cancellationToken).ConfigureAwait(true))
        {
            foreach (var folder in await _folders.ListByAccountAsync(account.Id, cancellationToken)
                .ConfigureAwait(true))
            {
                _folderOptions.Add(new ScopeFilterOption(
                    folder.Id, $"{account.EmailAddress.Value} / {folder.DisplayName}"));
            }
        }

        foreach (var category in await _manageCategories.ListAsync(null, cancellationToken)
            .ConfigureAwait(true))
        {
            _categoryOptions.Add(new ScopeFilterOption(category.Id, category.Name));
        }

        await RefreshRulesAsync(cancellationToken).ConfigureAwait(true);
        StartNewRule();
    }

    /// <summary>Prepara o editor para uma regra nova.</summary>
    [RelayCommand]
    public void StartNewRule()
    {
        EditingRuleId = null;
        RuleName = string.Empty;
        PriorityValue = Rules.Count;
        SelectedMatchType = MatchTypes[0];
        StopProcessing = false;
        Conditions.Clear();
        Actions.Clear();
        AddCondition();
        AddAction();
        StatusMessage = null;
    }

    /// <summary>Acrescenta uma linha de condição.</summary>
    [RelayCommand]
    public void AddCondition() => Conditions.Add(new RuleConditionEditorViewModel());

    /// <summary>Acrescenta uma linha de ação.</summary>
    [RelayCommand]
    public void AddAction()
    {
        var action = new RuleActionEditorViewModel();

        foreach (var option in _folderOptions)
        {
            action.FolderOptions.Add(option);
        }

        foreach (var option in _categoryOptions)
        {
            action.CategoryOptions.Add(option);
        }

        Actions.Add(action);
    }

    /// <summary>Remove uma linha de condição.</summary>
    public void RemoveCondition(RuleConditionEditorViewModel condition) => Conditions.Remove(condition);

    /// <summary>Remove uma linha de ação.</summary>
    public void RemoveAction(RuleActionEditorViewModel action) => Actions.Remove(action);

    /// <summary>Grava a regra em edição.</summary>
    [RelayCommand]
    public async Task SaveRuleAsync(CancellationToken cancellationToken = default)
    {
        var definition = new RuleDefinition
        {
            RuleId = EditingRuleId,
            Name = RuleName,
            Priority = (int)PriorityValue,
            MatchType = SelectedMatchType.Value,
            StopProcessing = StopProcessing,
            Conditions = Conditions.Select(c => c.ToDefinition()).ToList(),
            Actions = Actions.Select(a => a.ToDefinition()).ToList(),
        };

        var result = await _manageRules.SaveAsync(definition, cancellationToken).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = null;
        await RefreshRulesAsync(cancellationToken).ConfigureAwait(true);
        StartNewRule();
    }

    /// <summary>Carrega uma regra existente no editor.</summary>
    public async Task EditRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rules = await _manageRules.ListAsync(cancellationToken).ConfigureAwait(true);
        var rule = rules.FirstOrDefault(r => r.Id == ruleId);

        if (rule is null)
        {
            StatusMessage = "A regra não existe mais.";
            return;
        }

        EditingRuleId = rule.Id;
        RuleName = rule.Name;
        PriorityValue = rule.Priority;
        SelectedMatchType = MatchTypes.First(m => m.Value == rule.MatchType);
        StopProcessing = rule.StopProcessing;

        Conditions.Clear();
        foreach (var condition in rule.Conditions)
        {
            var row = new RuleConditionEditorViewModel
            {
                SelectedField = SelectionOptions.RuleFields
                    .FirstOrDefault(f => f.Value == condition.Field) ?? SelectionOptions.RuleFields[0],
                SelectedOperator = SelectionOptions.RuleOperators
                    .FirstOrDefault(o => o.Value == condition.Operator) ?? SelectionOptions.RuleOperators[0],
                Value = condition.Value ?? string.Empty,
            };
            Conditions.Add(row);
        }

        Actions.Clear();
        foreach (var action in rule.Actions)
        {
            var row = new RuleActionEditorViewModel();

            foreach (var option in _folderOptions)
            {
                row.FolderOptions.Add(option);
            }

            foreach (var option in _categoryOptions)
            {
                row.CategoryOptions.Add(option);
            }

            row.SelectedAction = SelectionOptions.RuleActions
                .FirstOrDefault(a => a.Value == action.ActionType) ?? SelectionOptions.RuleActions[0];
            row.SelectedFolder = row.FolderOptions.FirstOrDefault(f => f.Value == action.TargetFolderId);
            row.SelectedCategory = row.CategoryOptions.FirstOrDefault(c => c.Value == action.TargetCategoryId);

            Actions.Add(row);
        }

        StatusMessage = null;
    }

    /// <summary>Exclui uma regra.</summary>
    public async Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        await _manageRules.DeleteAsync(ruleId, cancellationToken).ConfigureAwait(true);
        await RefreshRulesAsync(cancellationToken).ConfigureAwait(true);

        if (EditingRuleId == ruleId)
        {
            StartNewRule();
        }
    }

    /// <summary>Ativa ou desativa uma regra.</summary>
    public async Task ToggleRuleAsync(
        Guid ruleId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await _manageRules.SetEnabledAsync(ruleId, isEnabled, cancellationToken).ConfigureAwait(true);
        await RefreshRulesAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshRulesAsync(CancellationToken cancellationToken)
    {
        Rules.Clear();

        foreach (var rule in await _manageRules.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            Rules.Add(new RuleListItemViewModel(
                rule.Id,
                rule.Name,
                rule.Priority,
                rule.IsEnabled,
                Summarize(rule)));
        }
    }

    /// <summary>Resumo de uma regra para a lista, em português corrente.</summary>
    private static string Summarize(Rule rule)
    {
        var conditions = rule.Conditions.Count switch
        {
            0 => "toda mensagem",
            1 => "1 condição",
            var n => $"{n} condições",
        };

        var actions = rule.Actions.Count == 1 ? "1 ação" : $"{rule.Actions.Count} ações";

        return $"{conditions} → {actions}";
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));
}
