using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma categoria na lista de gestão.</summary>
public sealed record CategoryListItemViewModel(Guid Id, string Name, string ColorHex, int? Shortcut);

/// <summary>Um modelo de mensagem na lista de gestão.</summary>
public sealed record TemplateListItemViewModel(Guid Id, string Name, string Subject);

/// <summary>Uma entrada das listas de remetentes.</summary>
public sealed record SenderListItemViewModel(Guid Id, string Target, SenderReputationKind Kind);

/// <summary>
/// ViewModel da organização: categorias coloridas, modelos de mensagem e as listas de
/// remetentes bloqueados e confiáveis.
/// </summary>
public sealed partial class OrganizationViewModel : ObservableObject
{
    private readonly ManageCategoriesHandler _categories;
    private readonly ManageTemplatesHandler _templates;
    private readonly ManageSenderReputationHandler _reputation;

    public OrganizationViewModel(
        ManageCategoriesHandler categories,
        ManageTemplatesHandler templates,
        ManageSenderReputationHandler reputation)
    {
        _categories = categories;
        _templates = templates;
        _reputation = reputation;
    }

    // ----- Categorias ------------------------------------------------------------------

    /// <summary>Categorias existentes.</summary>
    public ObservableCollection<CategoryListItemViewModel> Categories { get; } = [];

    /// <summary>Categoria em edição; nulo cria uma nova.</summary>
    [ObservableProperty]
    private Guid? _editingCategoryId;

    /// <summary>Nome da categoria em edição.</summary>
    [ObservableProperty]
    private string _categoryName = string.Empty;

    /// <summary>Cor da categoria em edição, em <c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    private string _categoryColor = "#0078D4";

    /// <summary>Atalho numérico (1 a 9) da categoria, como double para o NumberBox.</summary>
    [ObservableProperty]
    private double _categoryShortcut = double.NaN;

    // ----- Modelos ---------------------------------------------------------------------

    /// <summary>Modelos existentes.</summary>
    public ObservableCollection<TemplateListItemViewModel> Templates { get; } = [];

    /// <summary>Modelo em edição; nulo cria um novo.</summary>
    [ObservableProperty]
    private Guid? _editingTemplateId;

    /// <summary>Nome do modelo em edição.</summary>
    [ObservableProperty]
    private string _templateName = string.Empty;

    /// <summary>Assunto pré-preenchido do modelo.</summary>
    [ObservableProperty]
    private string _templateSubject = string.Empty;

    /// <summary>Corpo do modelo, em HTML.</summary>
    [ObservableProperty]
    private string _templateBody = string.Empty;

    // ----- Listas de remetentes --------------------------------------------------------

    /// <summary>Remetentes bloqueados.</summary>
    public ObservableCollection<SenderListItemViewModel> BlockedSenders { get; } = [];

    /// <summary>Remetentes confiáveis.</summary>
    public ObservableCollection<SenderListItemViewModel> TrustedSenders { get; } = [];

    /// <summary>Novo alvo digitado para bloquear.</summary>
    [ObservableProperty]
    private string _newBlockedTarget = string.Empty;

    /// <summary>Novo alvo digitado para confiar.</summary>
    [ObservableProperty]
    private string _newTrustedTarget = string.Empty;

    /// <summary>Aviso exibido ao usuário.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há aviso a exibir.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Carrega as três listas.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        await RefreshSendersAsync(cancellationToken).ConfigureAwait(true);
    }

    // ----- Categorias ------------------------------------------------------------------

    /// <summary>Grava a categoria em edição.</summary>
    [RelayCommand]
    public async Task SaveCategoryAsync(CancellationToken cancellationToken = default)
    {
        var shortcut = double.IsNaN(CategoryShortcut) ? (int?)null : (int)CategoryShortcut;

        var result = await _categories
            .SaveAsync(EditingCategoryId, CategoryName, CategoryColor, shortcut, null, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = null;
        ClearCategoryEditor();
        await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Carrega uma categoria no editor.</summary>
    public void EditCategory(CategoryListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        EditingCategoryId = item.Id;
        CategoryName = item.Name;
        CategoryColor = item.ColorHex;
        CategoryShortcut = item.Shortcut ?? double.NaN;
    }

    /// <summary>Exclui uma categoria.</summary>
    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await _categories.DeleteAsync(categoryId, cancellationToken).ConfigureAwait(true);

        if (EditingCategoryId == categoryId)
        {
            ClearCategoryEditor();
        }

        await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Limpa o editor de categoria.</summary>
    [RelayCommand]
    public void ClearCategoryEditor()
    {
        EditingCategoryId = null;
        CategoryName = string.Empty;
        CategoryColor = "#0078D4";
        CategoryShortcut = double.NaN;
    }

    // ----- Modelos ---------------------------------------------------------------------

    /// <summary>Grava o modelo em edição.</summary>
    [RelayCommand]
    public async Task SaveTemplateAsync(CancellationToken cancellationToken = default)
    {
        var result = await _templates
            .SaveAsync(EditingTemplateId, TemplateName, TemplateSubject, TemplateBody, null, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = null;
        ClearTemplateEditor();
        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Carrega um modelo no editor.</summary>
    public async Task EditTemplateAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await _templates.GetAsync(templateId, cancellationToken).ConfigureAwait(true);

        if (template is null)
        {
            StatusMessage = "O modelo não existe mais.";
            return;
        }

        EditingTemplateId = template.Id;
        TemplateName = template.Name;
        TemplateSubject = template.Subject;
        TemplateBody = template.HtmlBody;
    }

    /// <summary>Exclui um modelo.</summary>
    public async Task DeleteTemplateAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        await _templates.DeleteAsync(templateId, cancellationToken).ConfigureAwait(true);

        if (EditingTemplateId == templateId)
        {
            ClearTemplateEditor();
        }

        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Limpa o editor de modelo.</summary>
    [RelayCommand]
    public void ClearTemplateEditor()
    {
        EditingTemplateId = null;
        TemplateName = string.Empty;
        TemplateSubject = string.Empty;
        TemplateBody = string.Empty;
    }

    // ----- Listas de remetentes --------------------------------------------------------

    /// <summary>Bloqueia o alvo digitado.</summary>
    [RelayCommand]
    public Task AddBlockedSenderAsync(CancellationToken cancellationToken = default)
        => AddSenderAsync(SenderReputationKind.Blocked, NewBlockedTarget, cancellationToken);

    /// <summary>Confia no alvo digitado.</summary>
    [RelayCommand]
    public Task AddTrustedSenderAsync(CancellationToken cancellationToken = default)
        => AddSenderAsync(SenderReputationKind.Trusted, NewTrustedTarget, cancellationToken);

    /// <summary>Remove uma entrada das listas.</summary>
    public async Task DeleteSenderAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await _reputation.DeleteAsync(entryId, cancellationToken).ConfigureAwait(true);
        await RefreshSendersAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task AddSenderAsync(
        SenderReputationKind kind, string target, CancellationToken cancellationToken)
    {
        var result = await _reputation.AddAsync(kind, target, null, cancellationToken).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = null;

        if (kind == SenderReputationKind.Blocked)
        {
            NewBlockedTarget = string.Empty;
        }
        else
        {
            NewTrustedTarget = string.Empty;
        }

        await RefreshSendersAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshCategoriesAsync(CancellationToken cancellationToken)
    {
        Categories.Clear();

        foreach (var category in await _categories.ListAsync(null, cancellationToken).ConfigureAwait(true))
        {
            Categories.Add(new CategoryListItemViewModel(
                category.Id, category.Name, category.ColorHex, category.Shortcut));
        }
    }

    private async Task RefreshTemplatesAsync(CancellationToken cancellationToken)
    {
        Templates.Clear();

        foreach (var template in await _templates.ListAsync(null, cancellationToken).ConfigureAwait(true))
        {
            Templates.Add(new TemplateListItemViewModel(template.Id, template.Name, template.Subject));
        }
    }

    private async Task RefreshSendersAsync(CancellationToken cancellationToken)
    {
        BlockedSenders.Clear();
        TrustedSenders.Clear();

        foreach (var entry in await _reputation.ListAsync(null, cancellationToken).ConfigureAwait(true))
        {
            var item = new SenderListItemViewModel(entry.Id, entry.Target, entry.Kind);

            if (entry.Kind == SenderReputationKind.Blocked)
            {
                BlockedSenders.Add(item);
            }
            else
            {
                TrustedSenders.Add(item);
            }
        }
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));
}
