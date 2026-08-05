using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Presentation.ViewModels;
using Windows.Storage.Pickers;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Compositor de mensagens.</summary>
/// <remarks>
/// O code-behind traduz cliques em chamadas ao ViewModel e cuida do que só o WinUI faz —
/// seletor de arquivos e o ciclo do diálogo. Regras de envio, validação de endereço e o
/// aviso de anexo esquecido vivem em <see cref="ComposerViewModel"/>, coberto por testes no
/// job Linux.
/// </remarks>
public sealed partial class ComposerDialog : ContentDialog
{
    public ComposerDialog(ComposerViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>ViewModel do compositor.</summary>
    public ComposerViewModel ViewModel { get; }

    /// <summary>Prepara o compositor antes de exibir.</summary>
    public Task InitializeAsync(Guid accountId, DraftKind kind = DraftKind.New, Guid? sourceMessageId = null)
        => ViewModel.InitializeAsync(accountId, kind, sourceMessageId);

    private async void OnSendClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();

        try
        {
            await ViewModel.SendAsync().ConfigureAwait(true);

            if (ViewModel.IsCompleted)
            {
                Hide();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnSaveDraftClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Salvar rascunho não fecha: quem salva costuma continuar escrevendo.
        args.Cancel = true;
        var deferral = args.GetDeferral();

        try
        {
            await ViewModel.SaveDraftAsync().ConfigureAwait(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");

        // Em aplicativo desktop o picker precisa da janela dona; sem isto ele simplesmente
        // não abre, sem erro nenhum.
        var window = App.Services.GetRequiredService<MainWindow>();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return;
        }

        var properties = await file.GetBasicPropertiesAsync();

        ViewModel.AddAttachment(
            file.Name,
            file.Path,
            file.ContentType ?? "application/octet-stream",
            (long)properties.Size);
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComposerAttachmentItem item)
        {
            ViewModel.RemoveAttachment(item);
        }
    }

    /// <summary>Cria o compositor com as dependências do contêiner.</summary>
    public static ComposerDialog Create(XamlRoot xamlRoot)
        => new(App.Services.GetRequiredService<ComposerViewModel>())
        {
            XamlRoot = xamlRoot,
        };
}
