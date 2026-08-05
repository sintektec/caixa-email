using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Criação e edição de pasta, com vínculo a Diretório de Domínio.</summary>
public sealed partial class FolderDialog : ContentDialog
{
    public FolderDialog(FolderActionsViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel das ações de pasta.</summary>
    public FolderActionsViewModel ViewModel { get; }

    /// <summary>Se o diálogo edita uma pasta existente.</summary>
    public bool IsEditing => ViewModel.FolderId is not null;

    /// <summary>Se a pasta foi excluída nesta sessão do diálogo.</summary>
    public bool WasDeleted { get; private set; }

    /// <summary>Prepara o diálogo antes de exibir.</summary>
    public async Task InitializeAsync(Guid accountId, Guid? folderId = null, Guid? parentFolderId = null)
    {
        await ViewModel.InitializeAsync(accountId, folderId, parentFolderId).ConfigureAwait(true);
        Title = folderId is null ? "Nova pasta" : "Editar pasta";
        Bindings.Update();
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            await ViewModel.SaveAsync().ConfigureAwait(true);

            // Mantém aberto quando a gravação foi recusada: fechar levaria junto a
            // explicação do que deu errado.
            args.Cancel = ViewModel.HasStatusMessage;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnPrepareDeleteClick(object sender, RoutedEventArgs e)
        => await ViewModel.PrepareDeleteAsync().ConfigureAwait(true);

    private async void OnConfirmDeleteClick(object sender, RoutedEventArgs e)
    {
        WasDeleted = await ViewModel.ConfirmDeleteAsync().ConfigureAwait(true);

        if (WasDeleted)
        {
            Hide();
        }
    }

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static FolderDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<FolderActionsViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
