using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Fila de operações aguardando sincronização.</summary>
public sealed partial class OutboxDialog : ContentDialog
{
    public OutboxDialog(OutboxQueueViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();

        _ = ViewModel.LoadAsync();
    }

    /// <summary>ViewModel da fila.</summary>
    public OutboxQueueViewModel ViewModel { get; }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
        => await ViewModel.CancelSelectedAsync().ConfigureAwait(true);

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static OutboxDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<OutboxQueueViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
