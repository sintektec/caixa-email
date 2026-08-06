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

    /// <summary>
    /// Formato do arrasto de reordenação, deliberadamente distinto do de mensagem.
    /// </summary>
    /// <remarks>
    /// É o que impede os dois gestos de se confundirem na mesma árvore: uma pasta aceita
    /// mensagem e recusa diretório, e um diretório faz o inverso. Um formato só, com o tipo
    /// decidido no destino, deixaria "soltar mensagem sobre diretório" virar reordenação.
    /// </remarks>
    private const string NavigationDragFormat = "Sintek.Mail/navigation-node";

    public MainWindow(
        ShellViewModel shell,
        MessageListViewModel messageList,
        ReadingPaneViewModel reading,
        SearchViewModel search,
        AssistantViewModel assistant)
    {
        Shell = shell;
        MessageList = messageList;
        Reading = reading;
        Search = search;
        Assistant = assistant;

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

    /// <summary>ViewModel da pesquisa.</summary>
    public SearchViewModel Search { get; }

    /// <summary>ViewModel dos recursos de IA.</summary>
    public AssistantViewModel Assistant { get; }

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

        // O resto do conjunto do Outlook, que é o vocabulário que o usuário corporativo
        // já tem nos dedos. Ctrl+F é encaminhar, não localizar: no Outlook é assim, e
        // trocar isso é o tipo de "melhoria" que faz encaminhar sem querer.
        AddShortcut(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control,
            () => _ = OpenComposerAsync(DraftKind.Forward, Reading.MessageId));

        AddShortcut(Windows.System.VirtualKey.M, Windows.System.VirtualKeyModifiers.Control
            | Windows.System.VirtualKeyModifiers.Shift,
            () => _ = Shell.SyncNowAsync());

        AddShortcut(Windows.System.VirtualKey.Q, Windows.System.VirtualKeyModifiers.Control,
            () => _ = SetSelectedReadAsync(isRead: true));

        AddShortcut(Windows.System.VirtualKey.U, Windows.System.VirtualKeyModifiers.Control,
            () => _ = SetSelectedReadAsync(isRead: false));

        AddShortcut(Windows.System.VirtualKey.Delete, Windows.System.VirtualKeyModifiers.None,
            () => _ = DeleteSelectedAsync());

        AddShortcut(Windows.System.VirtualKey.J, Windows.System.VirtualKeyModifiers.Control,
            () => _ = MarkSelectedAsSpamAsync());

        // Ctrl+Shift+A abre a lista de atalhos: descobri-los precisa ser possível sem
        // consultar documentação.
        AddShortcut(Windows.System.VirtualKey.A, Windows.System.VirtualKeyModifiers.Control
            | Windows.System.VirtualKeyModifiers.Shift,
            () => _ = ShowShortcutsAsync());
    }

    /// <summary>Marca a mensagem selecionada como lida ou não lida.</summary>
    private async Task SetSelectedReadAsync(bool isRead)
    {
        if (MessageList.SelectedMessage is { } item)
        {
            await Shell.SetMessageReadAsync(item.MessageId, isRead).ConfigureAwait(true);
            item.IsRead = isRead;
        }
    }

    /// <summary>Move a mensagem selecionada para a lixeira.</summary>
    private async Task DeleteSelectedAsync()
    {
        if (MessageList.SelectedMessage is not { } item)
        {
            return;
        }

        await Shell.DeleteMessageAsync(item.MessageId).ConfigureAwait(true);

        if (MessageList.FolderId is { } folderId)
        {
            await MessageList.LoadFolderAsync(folderId).ConfigureAwait(true);
        }
    }

    private async Task MarkSelectedAsSpamAsync()
    {
        if (MessageList.SelectedMessage is not { } item)
        {
            return;
        }

        await Shell.MarkAsSpamAsync(item.MessageId, isSpam: true).ConfigureAwait(true);

        if (MessageList.FolderId is { } folderId)
        {
            await MessageList.LoadFolderAsync(folderId).ConfigureAwait(true);
        }
    }

    /// <summary>Lista os atalhos disponíveis.</summary>
    private async Task ShowShortcutsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Atalhos de teclado",
            CloseButtonText = "Fechar",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = """
                    Ctrl+N — Nova mensagem
                    Ctrl+R — Responder
                    Ctrl+Shift+R — Responder a todos
                    Ctrl+F — Encaminhar
                    Ctrl+E — Pesquisar
                    Ctrl+Q — Marcar como lida
                    Ctrl+U — Marcar como não lida
                    Ctrl+J — Marcar como spam
                    Delete — Mover para a lixeira
                    Ctrl+Shift+M — Sincronizar agora
                    F5 — Recarregar a árvore
                    Ctrl+Shift+A — Esta lista
                    """,
            },
        };

        await dialog.ShowAsync();
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
        else if (node.Kind == NavigationNodeKind.SavedSearch)
        {
            var ids = await Search.ExecuteSavedSearchAsync(node.EntityId).ConfigureAwait(true);

            if (ids is null)
            {
                Shell.StatusMessage = Search.StatusMessage;
                return;
            }

            await MessageList.ShowSearchResultsAsync(Search.ResultsDescription, ids).ConfigureAwait(true);
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

        if (Shell.SelectedNode?.AccountId is { } accountId)
        {
            await Assistant.InitializeAsync(accountId, MessageList.SelectedMessage.MessageId)
                .ConfigureAwait(true);
        }
    }

    /// <summary>Leva o usuário ao diretório onde o consentimento de IA é decidido.</summary>
    private async void OnOpenDirectorySettingsClick(object sender, RoutedEventArgs e)
    {
        await DomainDirectoryDialog.Create(RootGrid.XamlRoot).ShowAsync();
        await Shell.LoadNavigationAsync().ConfigureAwait(true);
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

    /// <summary>
    /// Inicia o arrasto de um Diretório de Domínio ou de uma conta, para reordenar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reordenação é feita à mão, e não pelo <c>CanReorderItems</c> da árvore: aquele
    /// mecanismo move o nó na coleção sem avisar ninguém, o que gravaria a nova ordem só na
    /// tela e a perderia no próximo carregamento. Com ele desligado, o gesto vira um arrasto
    /// comum, com formato próprio, e quem grava é o caso de uso.
    /// </para>
    /// <para>
    /// O formato é <b>outro</b>, diferente do de mensagem, e é isso que impede os dois gestos
    /// de se confundirem: uma pasta continua aceitando mensagem e recusando diretório, e a
    /// recíproca vale.
    /// </para>
    /// </remarks>
    private void OnNavigationDragStarting(object sender, TreeViewDragItemsStartingEventArgs e)
    {
        var node = e.Items.OfType<NavigationNode>().FirstOrDefault();

        if (node is null
            || node.Kind is not (NavigationNodeKind.DomainDirectory or NavigationNodeKind.Account))
        {
            // Pasta, seção e pesquisa salva não têm posição manual: as pastas seguem a ordem
            // do servidor, e as demais não são itens do usuário.
            e.Cancel = true;
            return;
        }

        e.Data.SetData(NavigationDragFormat, node.EntityId.ToString());
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(MessageDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            return;
        }

        // Um item só se reordena entre os próprios irmãos: diretório sobre diretório, conta
        // sobre conta. Aceitar o resto sugeriria uma movimentação que o caso de uso recusaria
        // depois, com o item já solto no lugar errado.
        if (e.DataView.Contains(NavigationDragFormat)
            && (sender as FrameworkElement)?.DataContext is NavigationNode
            {
                Kind: NavigationNodeKind.DomainDirectory or NavigationNodeKind.Account
            })
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.None;
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
        if (e.DataView.Contains(NavigationDragFormat))
        {
            await ReorderNavigationAsync(sender, e).ConfigureAwait(true);
            return;
        }

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
    /// Conclui a reordenação: o item arrastado assume a posição daquele sobre o qual foi
    /// solto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O <c>deferral</c> é obtido antes de qualquer <c>await</c> e concluído no
    /// <c>finally</c>. Sem ele, o WinUI encerra a operação de arrasto no primeiro ponto de
    /// suspensão e o cursor fica preso — que é o travamento que se vê na tela.
    /// </para>
    /// <para>
    /// A posição de destino sai do índice do alvo <b>entre os irmãos dele</b>, não de uma
    /// coordenada de tela: é o mesmo número que o ViewModel usa para reordenar a coleção, e
    /// tirá-lo daqui evita que a interface e o caso de uso contem posições de jeitos
    /// diferentes.
    /// </para>
    /// </remarks>
    private async Task ReorderNavigationAsync(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            if ((sender as FrameworkElement)?.DataContext is not NavigationNode target)
            {
                return;
            }

            var raw = await e.DataView.GetDataAsync(NavigationDragFormat) as string;

            if (!Guid.TryParse(raw, out var movedId) || movedId == target.EntityId)
            {
                return;
            }

            var siblings = FindSiblingCollection(target);

            if (siblings is null)
            {
                return;
            }

            await Shell.ReorderNodeAsync(movedId, siblings.IndexOf(target)).ConfigureAwait(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Devolve a coleção que contém o nó alvo, ou nada se ele não for reordenável.</summary>
    private IList<NavigationNode>? FindSiblingCollection(NavigationNode target)
    {
        foreach (var root in Shell.NavigationRoots)
        {
            if (root.Children.Contains(target))
            {
                return root.Children;
            }

            foreach (var directory in root.Children)
            {
                if (directory.Children.Contains(target))
                {
                    return directory.Children;
                }
            }
        }

        return null;
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

                case SettingsFollowUp.Rules:
                    var rules = RulesDialog.Create(RootGrid.XamlRoot);
                    await rules.InitializeAsync().ConfigureAwait(true);
                    await rules.ShowAsync();
                    next = SettingsFollowUp.None;
                    break;

                case SettingsFollowUp.Organization:
                    var organization = OrganizationDialog.Create(RootGrid.XamlRoot);
                    await organization.InitializeAsync().ConfigureAwait(true);
                    await organization.ShowAsync();
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

    /// <summary>
    /// Abre a agenda da conta selecionada.
    /// </summary>
    /// <remarks>
    /// A agenda pertence à conta, como o catálogo: sem conta escolhida não há qual abrir.
    /// </remarks>
    private async void OnCalendarClick(object sender, RoutedEventArgs e)
    {
        var accountId = Shell.SelectedNode?.AccountId;

        if (accountId is null)
        {
            Shell.StatusMessage = "Selecione uma conta ou pasta para abrir a agenda.";
            return;
        }

        var calendar = CalendarDialog.Create(RootGrid.XamlRoot);
        await calendar.InitializeAsync(accountId.Value).ConfigureAwait(true);
        await calendar.ShowAsync();
    }

    /// <summary>
    /// Abre o catálogo de contatos da conta selecionada.
    /// </summary>
    /// <remarks>
    /// Exige uma conta escolhida porque o catálogo e o histórico pertencem à conta: sem
    /// ela, não há qual lista abrir.
    /// </remarks>
    private async void OnContactsClick(object sender, RoutedEventArgs e)
    {
        var accountId = Shell.SelectedNode?.AccountId;

        if (accountId is null)
        {
            Shell.StatusMessage = "Selecione uma conta ou pasta para abrir os contatos.";
            return;
        }

        var contacts = ContactsDialog.Create(RootGrid.XamlRoot);
        await contacts.InitializeAsync(accountId.Value).ConfigureAwait(true);
        await contacts.ShowAsync();
    }

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

    /// <summary>
    /// Preenche o submenu "Categorizar" com as categorias existentes no momento em que o
    /// menu abre — a lista vive no banco e muda em tempo de execução.
    /// </summary>
    private async void OnMessageContextFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        var subItem = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault();

        if (subItem?.Tag is not MessageListItemViewModel item)
        {
            return;
        }

        subItem.Items.Clear();

        var handler = App.Services
            .GetRequiredService<Sintek.Mail.Application.UseCases.Organization.ManageCategoriesHandler>();

        foreach (var category in await handler.ListAsync(null).ConfigureAwait(true))
        {
            var menuItem = new MenuFlyoutItem { Text = category.Name };
            var categoryId = category.Id;

            menuItem.Click += async (_, _) =>
            {
                await handler.AssignAsync(item.MessageId, categoryId).ConfigureAwait(true);

                if (MessageList.FolderId is { } folderId)
                {
                    await MessageList.LoadFolderAsync(folderId).ConfigureAwait(true);
                }
            };

            subItem.Items.Add(menuItem);
        }

        if (subItem.Items.Count == 0)
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = "Nenhuma categoria cadastrada",
                IsEnabled = false,
            });
        }
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

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        Search.SearchText = args.QueryText;
        await RunSearchAsync().ConfigureAwait(true);
    }

    private async void OnApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        var found = await RunSearchAsync().ConfigureAwait(true);

        // O flyout permanece aberto quando a pesquisa foi recusada: é nele que está o
        // aviso explicando o motivo.
        if (found)
        {
            FiltersFlyout.Hide();
        }
    }

    /// <summary>Executa a pesquisa e apresenta os resultados no painel central.</summary>
    private async Task<bool> RunSearchAsync()
    {
        var ids = await Search.ExecuteAsync().ConfigureAwait(true);

        if (ids is null)
        {
            Shell.StatusMessage = Search.StatusMessage;
            return false;
        }

        await MessageList.ShowSearchResultsAsync(Search.ResultsDescription, ids).ConfigureAwait(true);
        return true;
    }

    private void OnFiltersFlyoutOpening(object sender, object e)
        => _ = Search.InitializeAsync();

    // A barra lateral espelha as pesquisas salvas; salvar ou excluir uma no flyout precisa
    // se refletir lá assim que ele fechar.
    private void OnFiltersFlyoutClosed(object sender, object e)
        => _ = Shell.LoadNavigationAsync();

    private async void OnApplySavedSearchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedSearchItemViewModel item)
        {
            return;
        }

        Search.ApplySavedSearch(item);

        if (await RunSearchAsync().ConfigureAwait(true))
        {
            FiltersFlyout.Hide();
        }
    }

    private async void OnDeleteSavedSearchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedSearchItemViewModel item)
        {
            return;
        }

        await Search.DeleteSavedSearchAsync(item).ConfigureAwait(true);
    }

    /// <summary>Sai do modo de pesquisa, voltando à pasta selecionada na árvore.</summary>
    private async void OnCloseSearchClick(object sender, RoutedEventArgs e)
    {
        MessageList.IsSearchResults = false;

        if (Shell.SelectedNode is { Kind: NavigationNodeKind.Folder } node)
        {
            await MessageList.LoadFolderAsync(node.EntityId).ConfigureAwait(true);
        }
        else
        {
            MessageList.Messages.Clear();
            MessageList.FolderName = string.Empty;
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
