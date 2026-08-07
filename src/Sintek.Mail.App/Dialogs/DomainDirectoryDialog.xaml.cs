using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Criação e edição de um Diretório de Domínio.</summary>
public sealed partial class DomainDirectoryDialog : ContentDialog
{
    public DomainDirectoryDialog(DomainDirectoryEditorViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel do formulário.</summary>
    public DomainDirectoryEditorViewModel ViewModel { get; }

    /// <summary>Se o diretório foi removido nesta sessão do diálogo.</summary>
    public bool WasRemoved { get; private set; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            await ViewModel.SaveAsync().ConfigureAwait(true);

            // Mantém o diálogo aberto quando a gravação foi recusada: fechá-lo faria o
            // usuário perder o formulário junto com a explicação do que deu errado.
            args.Cancel = ViewModel.StatusMessage is not null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnAddAliasClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddAlias(AliasBox.Text);
        AliasBox.Text = string.Empty;
    }

    private void OnRemoveAliasClick(object sender, RoutedEventArgs e)
    {
        if (AliasList.SelectedItem is string alias)
        {
            ViewModel.RemoveAlias(alias);
        }
    }

    private async void OnConfirmDomainChangeClick(object sender, RoutedEventArgs e)
        => await ViewModel.ConfirmDomainChangeAsync().ConfigureAwait(true);

    private async void OnPrepareRemovalClick(object sender, RoutedEventArgs e)
        => await ViewModel.PrepareRemovalAsync().ConfigureAwait(true);

    private async void OnConfirmRemovalClick(object sender, RoutedEventArgs e)
    {
        WasRemoved = await ViewModel.ConfirmRemovalAsync().ConfigureAwait(true);

        if (WasRemoved)
        {
            Hide();
        }
    }

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static DomainDirectoryDialog Create(XamlRoot xamlRoot)
    {
        // O escopo vive o tempo do diálogo: nasce aqui e é descartado no Closed,
        // levando junto o DbContext e tudo que ele rastreou.
        var scope = App.CreateScope();

        return new DomainDirectoryDialog(
            scope.ServiceProvider.GetRequiredService<DomainDirectoryEditorViewModel>())
        {
            XamlRoot = xamlRoot,
        }.WithScope(scope);
    }
}
