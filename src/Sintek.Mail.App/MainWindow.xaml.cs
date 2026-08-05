using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sintek.Mail.App.Dialogs;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Sintek.Mail.App;

/// <summary>Janela principal: navegação, lista de mensagens e painel de leitura.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Formato do arrastar e soltar interno.
    /// </summary>
    /// <remarks>
    /// Um formato próprio, e não texto puro, impede que um arrasto vindo de fora da
    /// aplicação seja interpretado como movimentação de mensagem.
    /// </remarks>
    private const string MessageDragFormat = "Sintek.Mail/message-ids";

    public MainWindow(
        ShellViewModel shell,
        MessageListViewModel messageList,
        ReadingPaneViewModel reading)
    {
        Shell = shell;
        MessageList = messageList;
        Reading = reading;

        InitializeComponent();

        Shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.StatusMessage))
            {
                Bindings.Update();
            }
        };

        RegisterKeyboardShortcuts();

        _ = Shell.LoadNavigationAsync();
    }

    /// <summary>ViewModel da janela.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>ViewModel do painel central.</summary>
    public MessageListViewModel MessageList { get; }

    /// <summary>ViewModel do painel de leitura.</summary>
    public ReadingPaneViewModel Reading { get; }

    /// <summary>Se há mensagem de status a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(Shell.StatusMessage);

    /// <summary>
    /// Registra os atalhos no padrão dos clientes de e-mail profissionais.
    /// </summary>
    private void RegisterKeyboardShortcuts()
    {
        AddShortcut(Windows.System.VirtualKey.N, Windows.System.VirtualKeyModifiers.Control,
            () => _ = OpenComposerAsync(DraftKind.New, null));

        AddShortcut(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () => _ = OpenComposerAsync(DraftKind.Reply, Reading.MessageId));

        AddShortcut(Windows.System.VirtualKey.R,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () => _ = OpenComposerAsync(DraftKind.ReplyAll, Reading.MessageId));

        AddShortcut(Windows.System.VirtualKey.E, Windows.System.VirtualKeyModifiers.Control,
            () => SearchBox.Focus(FocusState.Programmatic));

        AddShortcut(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None,
            () => _ = Shell.LoadNavigationAsync());
    }

    private void AddShortcut(
        Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };

        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private async void OnNavigationSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is not NavigationNode node)
        {
            return;
        }

        Shell.SelectedNode = node;

        if (node.Kind == NavigationNodeKind.Folder)
        {
            await MessageList.LoadFolderAsync(node.EntityId).ConfigureAwait(true);
        }
    }

    private async void OnMessageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedMessage is null)
        {
            return;
        }

        await Reading.LoadMessageAsync(MessageList.SelectedMessage.MessageId).ConfigureAwait(true);
        await RenderBodyAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Entrega o HTML já higienizado ao WebView2 com o navegador travado.
    /// </summary>
    /// <remarks>
    /// Esta é a <b>segunda</b> camada de defesa. O corpo já passou pelo sanitizador; aqui
    /// scripts ficam desativados, as DevTools também, e qualquer tentativa de navegação é
    /// cancelada. Uma URL clicada abre no navegador do sistema, nunca dentro do painel —
    /// que é o que impede uma página hostil de rodar no contexto da aplicação.
    /// </remarks>
    private async Task RenderBodyAsync()
    {
        await MessageBodyView.EnsureCoreWebView2Async();

        var core = MessageBodyView.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;

        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationStarting += OnNavigationStarting;

        core.NavigateToString(WrapInDocument(Reading.SanitizedHtml));
    }

    private void OnNavigationStarting(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs args)
    {
        // NavigateToString usa 'about:blank' como URI; qualquer outra navegação partiu do
        // conteúdo da mensagem e precisa ser bloqueada.
        if (args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>
    /// Envolve o corpo em um documento com CSP restritiva.
    /// </summary>
    /// <remarks>
    /// A CSP é a terceira barreira: mesmo que algo escape do sanitizador, o navegador
    /// recusa executar script e carregar recurso externo.
    /// </remarks>
    /// <remarks>
    /// O literal usa <c>$$</c> porque o corpo é CSS, cheio de chaves. Em uma raw string
    /// interpolada, a quantidade de <c>$</c> define quantas chaves abrem uma interpolação —
    /// não existe escape por duplicação. Com <c>$$</c>, a chave simples do CSS é literal e a
    /// interpolação passa a exigir <c>{{ }}</c>.
    /// </remarks>
    private static string WrapInDocument(string sanitizedHtml) =>
        $$"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; img-src cid: data:; style-src 'unsafe-inline'; font-src 'none'; script-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
        <style>
          body { font-family: 'Segoe UI Variable', 'Segoe UI', sans-serif; font-size: 14px; margin: 16px; }
          img { max-width: 100%; height: auto; }
          blockquote { border-left: 3px solid #c8c8c8; margin-left: 0; padding-left: 12px; }
        </style>
        </head>
        <body>{{sanitizedHtml}}</body>
        </html>
        """;

    private void OnMessageDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        var ids = e.Items.OfType<MessageListItemViewModel>().Select(m => m.MessageId).ToList();
        if (ids.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetData(MessageDragFormat, string.Join(';', ids));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(MessageDragFormat)
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
    }

    /// <summary>
    /// Conclui o arrastar e soltar de mensagens para uma pasta.
    /// </summary>
    /// <remarks>
    /// A regra de Diretório de Domínio NÃO é avaliada aqui: o handler delega ao caso de
    /// uso, que é o único lugar onde ela vive. Reimplementá-la na interface abriria a
    /// porta para as duas versões divergirem e o arrasto permitir o que o domínio proíbe.
    /// </remarks>
    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(MessageDragFormat))
        {
            return;
        }

        var deferral = e.GetDeferral();

        try
        {
            if ((sender as FrameworkElement)?.DataContext is not NavigationNode { Kind: NavigationNodeKind.Folder } target)
            {
                return;
            }

            var raw = await e.DataView.GetDataAsync(MessageDragFormat) as string;
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            foreach (var token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(token, out var messageId))
                {
                    await Shell.MoveMessageAsync(messageId, target.EntityId).ConfigureAwait(true);
                }
            }

            if (MessageList.FolderId is { } folderId)
            {
                await MessageList.LoadFolderAsync(folderId).ConfigureAwait(true);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Abre a tela de contas e diretórios, encadeando o assistente quando ele é pedido de
    /// lá.
    /// </summary>
    /// <remarks>
    /// O WinUI só mantém um <c>ContentDialog</c> aberto por vez. O encadeamento é feito com
    /// um laço porque as telas se chamam nos dois sentidos: o assistente pode pedir a
    /// criação de um diretório, e a tela de configurações pode abrir o assistente. Aninhar
    /// diálogos, além de proibido, faria o segundo simplesmente não aparecer.
    /// </remarks>
    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var next = SettingsFollowUp.None;

        do
        {
            switch (next)
            {
                case SettingsFollowUp.AccountSetup:
                    next = await ShowAccountSetupAsync().ConfigureAwait(true);
                    break;

                case SettingsFollowUp.DomainDirectory:
                    await DomainDirectoryDialog.Create(RootGrid.XamlRoot).ShowAsync();
                    next = SettingsFollowUp.None;
                    break;

                default:
                    var settings = SettingsDialog.Create(RootGrid.XamlRoot);
                    await settings.ShowAsync();
                    next = settings.FollowUp;
                    break;
            }
        }
        while (next != SettingsFollowUp.None);

        await Shell.LoadNavigationAsync().ConfigureAwait(true);
    }

    private async Task<SettingsFollowUp> ShowAccountSetupAsync()
    {
        var wizard = AccountSetupDialog.Create(RootGrid.XamlRoot);
        await wizard.ShowAsync();

        // Pedir a criação de um diretório no meio do assistente leva ao editor e, dele, de
        // volta à tela de configurações — de onde o assistente pode recomeçar já com o
        // diretório existindo.
        return wizard.RequestedDirectoryCreation
            ? SettingsFollowUp.DomainDirectory
            : SettingsFollowUp.None;
    }

    private async void OnNewMessageClick(object sender, RoutedEventArgs e)
        => await OpenComposerAsync(DraftKind.New, null).ConfigureAwait(true);

    private async void OnReplyClick(object sender, RoutedEventArgs e)
        => await OpenComposerAsync(DraftKind.Reply, Reading.MessageId).ConfigureAwait(true);

    private async void OnReplyAllClick(object sender, RoutedEventArgs e)
        => await OpenComposerAsync(DraftKind.ReplyAll, Reading.MessageId).ConfigureAwait(true);

    private async void OnForwardClick(object sender, RoutedEventArgs e)
        => await OpenComposerAsync(DraftKind.Forward, Reading.MessageId).ConfigureAwait(true);

    /// <summary>
    /// Abre o compositor para a conta do nó selecionado.
    /// </summary>
    /// <remarks>
    /// Resposta e encaminhamento exigem uma mensagem aberta; mensagem nova exige ao menos
    /// saber por qual conta enviar, que vem da seleção na árvore.
    /// </remarks>
    private async Task OpenComposerAsync(DraftKind kind, Guid? sourceMessageId)
    {
        if (kind != DraftKind.New && sourceMessageId is null)
        {
            return;
        }

        var accountId = Shell.SelectedNode?.AccountId;

        if (accountId is null)
        {
            Shell.StatusMessage = "Selecione uma conta ou pasta antes de escrever.";
            return;
        }

        var composer = ComposerDialog.Create(RootGrid.XamlRoot);
        await composer.InitializeAsync(accountId.Value, kind, sourceMessageId).ConfigureAwait(true);
        await composer.ShowAsync();

        await Shell.RefreshPendingCountAsync().ConfigureAwait(true);
    }

    /// <summary>Baixa o anexo, se preciso, e o abre com o aplicativo padrão.</summary>
    private async void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AttachmentViewModel attachment)
        {
            return;
        }

        var path = await Reading.DownloadAttachmentAsync(attachment.AttachmentId).ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        _ = await Windows.System.Launcher.LaunchFileAsync(file);
    }

    private async void OnNewSubfolderClick(object sender, RoutedEventArgs e)
        => await OpenFolderDialogAsync(sender, edit: false).ConfigureAwait(true);

    private async void OnEditFolderClick(object sender, RoutedEventArgs e)
        => await OpenFolderDialogAsync(sender, edit: true).ConfigureAwait(true);

    private async Task OpenFolderDialogAsync(object sender, bool edit)
    {
        if ((sender as FrameworkElement)?.Tag is not NavigationNode node || node.AccountId is not { } accountId)
        {
            return;
        }

        if (edit && node.Kind != NavigationNodeKind.Folder)
        {
            return;
        }

        var dialog = FolderDialog.Create(RootGrid.XamlRoot);

        await dialog.InitializeAsync(
            accountId,
            edit ? node.EntityId : null,
            edit ? null : (node.Kind == NavigationNodeKind.Folder ? node.EntityId : null))
            .ConfigureAwait(true);

        await dialog.ShowAsync();
        await Shell.LoadNavigationAsync().ConfigureAwait(true);
    }

    private async void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NavigationNode { Kind: NavigationNodeKind.Folder } node)
        {
            return;
        }

        var actions = App.Services.GetRequiredService<FolderActionsViewModel>();
        await actions.ToggleFavoriteAsync(node.EntityId, !node.IsFavorite).ConfigureAwait(true);
        await Shell.LoadNavigationAsync().ConfigureAwait(true);
    }

    private async void OnMarkAsSpamClick(object sender, RoutedEventArgs e)
        => await MarkSpamAsync(sender, isSpam: true).ConfigureAwait(true);

    private async void OnMarkAsNotSpamClick(object sender, RoutedEventArgs e)
        => await MarkSpamAsync(sender, isSpam: false).ConfigureAwait(true);

    private async Task MarkSpamAsync(object sender, bool isSpam)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageListItemViewModel item)
        {
            return;
        }

        await Shell.MarkAsSpamAsync(item.MessageId, isSpam).ConfigureAwait(true);

        if (MessageList.FolderId is { } folderId)
        {
            await MessageList.LoadFolderAsync(folderId).ConfigureAwait(true);
        }
    }

    private async void OnOutboxClick(object sender, RoutedEventArgs e)
    {
        await OutboxDialog.Create(RootGrid.XamlRoot).ShowAsync();
        await Shell.RefreshPendingCountAsync().ConfigureAwait(true);
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        if (RootGrid is FrameworkElement root)
        {
            root.RequestedTheme = root.RequestedTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
        }
    }
}
