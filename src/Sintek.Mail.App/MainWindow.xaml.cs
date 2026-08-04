using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sintek.Mail.App.ViewModels;
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
            () => { /* Nova mensagem */ });

        AddShortcut(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () => { /* Responder */ });

        AddShortcut(Windows.System.VirtualKey.R,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () => { /* Responder a todos */ });

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
    private static string WrapInDocument(string sanitizedHtml) =>
        $"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; img-src cid: data:; style-src 'unsafe-inline'; font-src 'none'; script-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
        <style>
          body {{ font-family: 'Segoe UI Variable', 'Segoe UI', sans-serif; font-size: 14px; margin: 16px; }}
          img {{ max-width: 100%; height: auto; }}
          blockquote {{ border-left: 3px solid #c8c8c8; margin-left: 0; padding-left: 12px; }}
        </style>
        </head>
        <body>{sanitizedHtml}</body>
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
