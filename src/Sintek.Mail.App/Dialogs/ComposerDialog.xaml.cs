using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Presentation.ViewModels;
using Windows.Storage.Pickers;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Compositor de mensagens, com editor rico e rascunho automático.</summary>
/// <remarks>
/// O code-behind traduz cliques em chamadas ao ViewModel e cuida do que só o WinUI faz —
/// seletor de arquivos, o ciclo do diálogo e o WebView2 do editor. Regras de envio,
/// validação de endereço, o aviso de anexo esquecido e a política do rascunho automático
/// vivem em <see cref="ComposerViewModel"/>, coberto por testes no job Linux.
/// </remarks>
public sealed partial class ComposerDialog : ContentDialog
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoSaveTimer;
    private bool _editorReady;

    public ComposerDialog(ComposerViewModel viewModel, AssistantViewModel assistant)
    {
        ViewModel = viewModel;
        Assistant = assistant;

        InitializeComponent();
    }

    /// <summary>ViewModel do compositor.</summary>
    public ComposerViewModel ViewModel { get; }

    /// <summary>ViewModel dos recursos de IA.</summary>
    public AssistantViewModel Assistant { get; }

    /// <summary>Prepara o compositor antes de exibir.</summary>
    public async Task InitializeAsync(
        Guid accountId, DraftKind kind = DraftKind.New, Guid? sourceMessageId = null)
    {
        await ViewModel.InitializeAsync(accountId, kind, sourceMessageId).ConfigureAwait(true);
        await Assistant.InitializeAsync(accountId).ConfigureAwait(true);
    }

    /// <summary>Reescreve o texto do editor com o assistente.</summary>
    /// <remarks>
    /// O que está no editor entra antes: reescrever o estado antigo devolveria ao usuário
    /// um texto que ele já tinha mudado.
    /// </remarks>
    private async void OnRewriteClick(object sender, RoutedEventArgs e)
    {
        await SyncEditorToViewModelAsync().ConfigureAwait(true);

        var rewritten = await Assistant.RewriteAsync(ViewModel.BodyText).ConfigureAwait(true);

        if (rewritten is null || !_editorReady)
        {
            return;
        }

        ViewModel.BodyHtml = System.Net.WebUtility.HtmlEncode(rewritten)
            .Replace("\n", "<br>", StringComparison.Ordinal);
        ViewModel.BodyText = rewritten;

        EditorView.CoreWebView2.NavigateToString(BuildEditorDocument());
    }

    /// <summary>
    /// Monta o editor e liga o rascunho automático quando o diálogo abre.
    /// </summary>
    /// <remarks>
    /// O WebView2 só pode ser inicializado com o diálogo na árvore visual; fazê-lo em
    /// <see cref="InitializeAsync"/> falharia em silêncio.
    /// </remarks>
    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        await InitializeEditorAsync().ConfigureAwait(true);

        // O temporizador só dispara a verificação; quem decide gravar — e quando — é o
        // ViewModel, a partir do período de silêncio da digitação.
        _autoSaveTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromSeconds(2);
        _autoSaveTimer.Tick += async (_, _) =>
        {
            await SyncEditorToViewModelAsync().ConfigureAwait(true);
            await ViewModel.AutoSaveTickAsync().ConfigureAwait(true);
        };
        _autoSaveTimer.Start();
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;
    }

    private void OnToTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => _ = UpdateSuggestionsAsync(RecipientField.To, args);

    private void OnCcTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => _ = UpdateSuggestionsAsync(RecipientField.Cc, args);

    private void OnBccTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => _ = UpdateSuggestionsAsync(RecipientField.Bcc, args);

    private void OnToQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => ApplySuggestion(sender, RecipientField.To, args);

    private void OnCcQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => ApplySuggestion(sender, RecipientField.Cc, args);

    private void OnBccQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => ApplySuggestion(sender, RecipientField.Bcc, args);

    /// <summary>
    /// Atualiza as sugestões enquanto o usuário digita.
    /// </summary>
    /// <remarks>
    /// Só reage a digitação de verdade: o evento também dispara quando o próprio código
    /// atribui o texto — ao escolher uma sugestão, por exemplo —, e sem esta guarda a
    /// lista reabriria logo depois de ser fechada.
    /// </remarks>
    private async Task UpdateSuggestionsAsync(
        RecipientField field, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        await ViewModel.UpdateRecipientSuggestionsAsync(field).ConfigureAwait(true);
    }

    /// <summary>
    /// Insere a sugestão escolhida no campo.
    /// </summary>
    /// <remarks>
    /// A troca é feita pelo ViewModel, que substitui só o trecho em digitação e preserva os
    /// endereços já presentes. O texto da caixa é reatribuído depois porque o
    /// <c>AutoSuggestBox</c> escreve o item escolhido por conta própria antes deste evento,
    /// apagando os demais destinatários.
    /// </remarks>
    private void ApplySuggestion(
        AutoSuggestBox sender, RecipientField field, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is not RecipientSuggestionItem suggestion)
        {
            return;
        }

        ViewModel.ApplyRecipientSuggestion(field, suggestion);

        sender.Text = field switch
        {
            RecipientField.To => ViewModel.To,
            RecipientField.Cc => ViewModel.Cc,
            _ => ViewModel.Bcc,
        };
    }

    /// <summary>
    /// Prepara o WebView2 como editor: documento local editável, navegação bloqueada.
    /// </summary>
    /// <remarks>
    /// O conteúdo inicial é nosso — texto do próprio usuário ou citação que já passou pelo
    /// sanitizador no <c>DraftComposer</c>. A CSP barra qualquer recurso externo; scripts
    /// de página não existem no documento, e os comandos de formatação entram por
    /// <c>ExecuteScriptAsync</c>, que não depende deles.
    /// </remarks>
    private async Task InitializeEditorAsync()
    {
        await EditorView.EnsureCoreWebView2Async();

        var core = EditorView.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;

        core.NavigationStarting += (_, navArgs) =>
        {
            // NavigateToString usa about:blank; qualquer outra navegação veio de conteúdo
            // colado no editor e é bloqueada, como no painel de leitura.
            if (!navArgs.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                navArgs.Cancel = true;
            }
        };

        core.NavigateToString(BuildEditorDocument());
        _editorReady = true;
    }

    /// <summary>Documento editável com o corpo inicial do rascunho.</summary>
    private string BuildEditorDocument()
    {
        // Resposta e encaminhamento chegam com HTML citado; mensagem nova, só com o texto
        // da assinatura. O texto vira HTML escapado — nunca interpretado.
        var initial = ViewModel.BodyHtml.Length > 0
            ? ViewModel.BodyHtml
            : System.Net.WebUtility.HtmlEncode(ViewModel.BodyText).Replace("\n", "<br>", StringComparison.Ordinal);

        return $$"""
            <!DOCTYPE html>
            <html lang="pt-BR">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; img-src cid: data:; style-src 'unsafe-inline'">
            <style>
              body { font-family: 'Segoe UI Variable', 'Segoe UI', sans-serif; font-size: 14px; margin: 12px; }
              blockquote { border-left: 3px solid #c8c8c8; margin-left: 0; padding-left: 12px; }
            </style>
            </head>
            <body contenteditable="true">{{initial}}</body>
            </html>
            """;
    }

    /// <summary>
    /// Copia o conteúdo do editor para o ViewModel — chamado antes de qualquer gravação.
    /// </summary>
    private async Task SyncEditorToViewModelAsync()
    {
        if (!_editorReady)
        {
            return;
        }

        var html = await EditorView.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
        var text = await EditorView.CoreWebView2.ExecuteScriptAsync("document.body.innerText");

        // ExecuteScriptAsync devolve o resultado como JSON; o valor real vem de dentro.
        ViewModel.BodyHtml = JsonSerializer.Deserialize<string>(html) ?? string.Empty;
        ViewModel.BodyText = JsonSerializer.Deserialize<string>(text) ?? string.Empty;
    }

    private async Task ExecuteFormatCommandAsync(string command)
    {
        if (_editorReady)
        {
            await EditorView.CoreWebView2
                .ExecuteScriptAsync($"document.execCommand('{command}'); document.body.focus();");
        }
    }

    private async void OnFormatBoldClick(object sender, RoutedEventArgs e)
        => await ExecuteFormatCommandAsync("bold").ConfigureAwait(true);

    private async void OnFormatItalicClick(object sender, RoutedEventArgs e)
        => await ExecuteFormatCommandAsync("italic").ConfigureAwait(true);

    private async void OnFormatUnderlineClick(object sender, RoutedEventArgs e)
        => await ExecuteFormatCommandAsync("underline").ConfigureAwait(true);

    private async void OnFormatListClick(object sender, RoutedEventArgs e)
        => await ExecuteFormatCommandAsync("insertUnorderedList").ConfigureAwait(true);

    private async void OnFormatClearClick(object sender, RoutedEventArgs e)
        => await ExecuteFormatCommandAsync("removeFormat").ConfigureAwait(true);

    private async void OnSendClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();

        try
        {
            await SyncEditorToViewModelAsync().ConfigureAwait(true);
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
            await SyncEditorToViewModelAsync().ConfigureAwait(true);
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
        // MainWindow é singleton; resolvê-la de um escopo curto devolve a mesma instância.
        // O descarte é assíncrono porque o MailKitImapClient do contêiner só implementa
        // IAsyncDisposable, e um `using` comum lançaria se ele viesse a ser resolvido aqui.
        await using var scope = App.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<MainWindow>();
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

    /// <summary>Aplica o modelo escolhido e recarrega o editor com o novo corpo.</summary>
    private async void OnApplyTemplateClick(object sender, RoutedEventArgs e)
    {
        if (TemplatePicker.SelectedItem is not ScopeFilterOption { Value: { } templateId })
        {
            return;
        }

        // O que já foi digitado precisa entrar antes do modelo ser aplicado sobre ele.
        await SyncEditorToViewModelAsync().ConfigureAwait(true);

        if (await ViewModel.ApplyTemplateAsync(templateId).ConfigureAwait(true) && _editorReady)
        {
            EditorView.CoreWebView2.NavigateToString(BuildEditorDocument());
        }
    }

    /// <summary>Cria o compositor com as dependências do contêiner.</summary>
    public static ComposerDialog Create(XamlRoot xamlRoot)
    {
        // O escopo vive o tempo do diálogo: nasce aqui e é descartado no Closed,
        // levando junto o DbContext e tudo que ele rastreou.
        var scope = App.CreateScope();

        return new ComposerDialog(
            scope.ServiceProvider.GetRequiredService<ComposerViewModel>(),
            scope.ServiceProvider.GetRequiredService<AssistantViewModel>())
        {
            XamlRoot = xamlRoot,
        }.WithScope(scope);
    }
}
