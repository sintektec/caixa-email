using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Catálogo de contatos e histórico de destinatários da conta.</summary>
/// <remarks>
/// O code-behind cuida apenas do que é do WinUI: o seletor de arquivos da importação e da
/// exportação. Ler e escrever vCard é da camada de Aplicação, e a decisão do que a tela
/// mostra é do <see cref="ContactsViewModel"/> — os dois verificados no job Linux.
/// </remarks>
public sealed partial class ContactsDialog : ContentDialog
{
    public ContactsDialog(ContactsViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel dos contatos.</summary>
    public ContactsViewModel ViewModel { get; }

    /// <summary>Carrega catálogo e histórico antes de exibir.</summary>
    public Task InitializeAsync(Guid accountId) => ViewModel.LoadAsync(accountId);

    private async void OnContactSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await ViewModel.EditSelectedAsync().ConfigureAwait(true);

    private async void OnRemoveHistoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecipientHistoryItem item)
        {
            await ViewModel.RemoveHistoryEntryAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".vcf");
        picker.FileTypeFilter.Add(".vcard");

        AttachToWindow(picker);

        var file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return;
        }

        var content = await FileIO.ReadTextAsync(file);
        await ViewModel.ImportAsync(content).ConfigureAwait(true);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var content = await ViewModel.ExportAsync().ConfigureAwait(true);

        if (content.Length == 0)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "contatos",
        };
        picker.FileTypeChoices.Add("vCard", [".vcf"]);

        AttachToWindow(picker);

        if (await picker.PickSaveFileAsync() is { } file)
        {
            await FileIO.WriteTextAsync(file, content);
        }
    }

    /// <summary>
    /// Liga o seletor à janela dona.
    /// </summary>
    /// <remarks>
    /// Em aplicativo desktop o seletor precisa do identificador da janela; sem isto ele
    /// simplesmente não abre, e sem erro nenhum.
    /// </remarks>
    private static void AttachToWindow(object picker)
        => WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static ContactsDialog Create(XamlRoot xamlRoot)
    {
        // O escopo vive o tempo do diálogo: nasce aqui e é descartado no Closed,
        // levando junto o DbContext e tudo que ele rastreou.
        var scope = App.CreateScope();

        return new ContactsDialog(
            scope.ServiceProvider.GetRequiredService<ContactsViewModel>())
        {
            XamlRoot = xamlRoot,
        }.WithScope(scope);
    }
}
