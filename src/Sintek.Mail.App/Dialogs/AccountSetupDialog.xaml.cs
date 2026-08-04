using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Assistente de configuração de conta.</summary>
/// <remarks>
/// O code-behind só traduz cliques em chamadas ao ViewModel e decide o rótulo do botão
/// principal. Toda a lógica de etapas, validação e mensagem de erro vive em
/// <see cref="AccountSetupViewModel"/>, que é multiplataforma e coberto por testes — este
/// arquivo, por depender do WinUI, só o CI Windows consegue compilar.
/// </remarks>
public sealed partial class AccountSetupDialog : ContentDialog
{
    public AccountSetupDialog(AccountSetupViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();

        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AccountSetupViewModel.Step))
            {
                UpdateButtons();
            }
        };

        UpdateButtons();

        _ = ViewModel.LoadDirectoriesAsync();
    }

    /// <summary>ViewModel do assistente.</summary>
    public AccountSetupViewModel ViewModel { get; }

    /// <summary>
    /// Se o usuário pediu para criar um Diretório de Domínio antes de continuar.
    /// </summary>
    /// <remarks>
    /// O WinUI só permite um <c>ContentDialog</c> aberto por vez. Este fecha, o chamador
    /// abre o editor de diretórios e depois reabre o assistente — este sinalizador é como o
    /// chamador sabe que foi isso, e não um cancelamento.
    /// </remarks>
    public bool RequestedDirectoryCreation { get; private set; }

    /// <summary>Ajusta os botões ao que a etapa corrente permite fazer.</summary>
    private void UpdateButtons()
    {
        PrimaryButtonText = ViewModel.Step switch
        {
            AccountSetupStep.Credentials => "Testar conexão",
            AccountSetupStep.Verification => "Concluir",
            AccountSetupStep.Completed => "Fechar",
            _ => "Continuar",
        };

        IsSecondaryButtonEnabled = ViewModel.Step is not (AccountSetupStep.Address or AccountSetupStep.Completed);
        CloseButtonText = ViewModel.Step == AccountSetupStep.Completed ? string.Empty : "Cancelar";
    }

    /// <summary>
    /// Executa a ação da etapa corrente.
    /// </summary>
    /// <remarks>
    /// O diálogo é mantido aberto em todas as etapas menos a última. Sem o
    /// <c>args.Cancel</c>, o primeiro clique fecharia a janela e o assistente perderia tudo
    /// que já havia sido preenchido.
    /// </remarks>
    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ViewModel.Step == AccountSetupStep.Completed)
        {
            return;
        }

        args.Cancel = true;
        var deferral = args.GetDeferral();

        try
        {
            switch (ViewModel.Step)
            {
                case AccountSetupStep.Address:
                    await ViewModel.ContinueFromAddressAsync().ConfigureAwait(true);
                    break;

                case AccountSetupStep.Servers:
                    ViewModel.ContinueFromServers();
                    break;

                case AccountSetupStep.Credentials:
                    await ViewModel.VerifyAsync().ConfigureAwait(true);
                    break;

                case AccountSetupStep.Verification:
                    await ViewModel.FinishAsync().ConfigureAwait(true);
                    break;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ViewModel.GoBack();
    }

    /// <summary>
    /// Abre o editor de Diretórios de Domínio sem perder o que já foi preenchido.
    /// </summary>
    /// <remarks>
    /// Só um <c>ContentDialog</c> pode estar aberto por vez no WinUI. Por isso este fecha
    /// antes de abrir o outro, e o chamador reabre o assistente depois — daí o
    /// <see cref="ContentDialogResult.Secondary"/> como sinal.
    /// </remarks>
    private void OnCreateDirectoryClick(object sender, RoutedEventArgs e)
    {
        RequestedDirectoryCreation = true;
        Hide();
    }

    /// <summary>Cria o assistente com as dependências do contêiner.</summary>
    public static AccountSetupDialog Create(XamlRoot xamlRoot)
    {
        var dialog = new AccountSetupDialog(App.Services.GetRequiredService<AccountSetupViewModel>())
        {
            XamlRoot = xamlRoot,
        };

        return dialog;
    }
}
