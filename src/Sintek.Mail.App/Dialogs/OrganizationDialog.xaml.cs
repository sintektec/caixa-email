using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Gestão de categorias, modelos de mensagem e listas de remetentes.</summary>
public sealed partial class OrganizationDialog : ContentDialog
{
    public OrganizationDialog(OrganizationViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel da organização.</summary>
    public OrganizationViewModel ViewModel { get; }

    /// <summary>Carrega as listas antes de exibir.</summary>
    public Task InitializeAsync() => ViewModel.InitializeAsync();

    private void OnEditCategoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CategoryListItemViewModel item)
        {
            ViewModel.EditCategory(item);
        }
    }

    private async void OnDeleteCategoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CategoryListItemViewModel item)
        {
            await ViewModel.DeleteCategoryAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnEditTemplateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TemplateListItemViewModel item)
        {
            await ViewModel.EditTemplateAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TemplateListItemViewModel item)
        {
            await ViewModel.DeleteTemplateAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnDeleteSenderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SenderListItemViewModel item)
        {
            await ViewModel.DeleteSenderAsync(item.Id).ConfigureAwait(true);
        }
    }

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static OrganizationDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<OrganizationViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
