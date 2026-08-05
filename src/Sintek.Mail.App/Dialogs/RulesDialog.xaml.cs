using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Gestão de regras automáticas: lista e editor.</summary>
public sealed partial class RulesDialog : ContentDialog
{
    public RulesDialog(RulesViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel das regras.</summary>
    public RulesViewModel ViewModel { get; }

    /// <summary>Carrega regras, pastas e categorias antes de exibir.</summary>
    public Task InitializeAsync() => ViewModel.InitializeAsync();

    private async void OnSaveRuleClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Salvar não fecha: quem grava uma regra costuma criar a próxima em seguida.
        args.Cancel = true;
        var deferral = args.GetDeferral();

        try
        {
            await ViewModel.SaveRuleAsync().ConfigureAwait(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnNewRuleClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ViewModel.StartNewRule();
    }

    private async void OnEditRuleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleListItemViewModel item)
        {
            await ViewModel.EditRuleAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleListItemViewModel item)
        {
            await ViewModel.DeleteRuleAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: RuleListItemViewModel item } toggle
            && toggle.IsOn != item.IsEnabled)
        {
            await ViewModel.ToggleRuleAsync(item.Id, toggle.IsOn).ConfigureAwait(true);
        }
    }

    private void OnRemoveConditionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleConditionEditorViewModel condition)
        {
            ViewModel.RemoveCondition(condition);
        }
    }

    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleActionEditorViewModel action)
        {
            ViewModel.RemoveAction(action);
        }
    }

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static RulesDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<RulesViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
