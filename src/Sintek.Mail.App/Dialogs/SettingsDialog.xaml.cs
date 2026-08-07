using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.App.Dialogs;

/// <summary>Tela de contas e Diretórios de Domínio.</summary>
/// <remarks>
/// É também o ponto de partida do assistente de conta e do editor de diretórios. Como o
/// WinUI só permite um <c>ContentDialog</c> aberto por vez, cada abertura fecha esta tela e
/// a reabre depois — daí o vaivém explícito em vez de diálogos aninhados.
/// </remarks>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(AccountsViewModel viewModel, MaintenanceViewModel maintenance)
    {
        ViewModel = viewModel;
        Maintenance = maintenance;

        InitializeComponent();

        _ = ViewModel.LoadAsync();
    }

    /// <summary>ViewModel da lista de contas.</summary>
    public AccountsViewModel ViewModel { get; }

    /// <summary>ViewModel da manutenção do cache local.</summary>
    public MaintenanceViewModel Maintenance { get; }

    private async void OnConfirmCleanupClick(object sender, RoutedEventArgs e)
        => await Maintenance.ConfirmAsync().ConfigureAwait(true);

    /// <summary>Qual tela deve ser aberta depois que esta fechar.</summary>
    public SettingsFollowUp FollowUp { get; private set; }

    private void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        FollowUp = SettingsFollowUp.AccountSetup;
        Hide();
    }

    private void OnNewDirectoryClick(object sender, RoutedEventArgs e)
    {
        FollowUp = SettingsFollowUp.DomainDirectory;
        Hide();
    }

    private void OnRulesClick(object sender, RoutedEventArgs e)
    {
        FollowUp = SettingsFollowUp.Rules;
        Hide();
    }

    private void OnOrganizationClick(object sender, RoutedEventArgs e)
    {
        FollowUp = SettingsFollowUp.Organization;
        Hide();
    }

    private async void OnToggleAccountClick(object sender, RoutedEventArgs e)
        => await ViewModel.ToggleSelectedAccountAsync().ConfigureAwait(true);

    private async void OnSaveSyncIntervalClick(object sender, RoutedEventArgs e)
        => await ViewModel.SaveSyncIntervalAsync().ConfigureAwait(true);

    private async void OnPrepareRemovalClick(object sender, RoutedEventArgs e)
        => await ViewModel.PrepareRemovalAsync().ConfigureAwait(true);

    private async void OnConfirmRemovalClick(object sender, RoutedEventArgs e)
        => await ViewModel.ConfirmRemovalAsync().ConfigureAwait(true);

    /// <summary>Cria o diálogo com as dependências do contêiner.</summary>
    public static SettingsDialog Create(XamlRoot xamlRoot)
    {
        // O escopo vive o tempo do diálogo: nasce aqui e é descartado no Closed,
        // levando junto o DbContext e tudo que ele rastreou.
        var scope = App.CreateScope();

        return new SettingsDialog(
            scope.ServiceProvider.GetRequiredService<AccountsViewModel>(),
            scope.ServiceProvider.GetRequiredService<MaintenanceViewModel>())
        {
            XamlRoot = xamlRoot,
        }.WithScope(scope);
    }
}

/// <summary>Tela a abrir depois de a de configurações fechar.</summary>
public enum SettingsFollowUp
{
    /// <summary>Nenhuma: o usuário apenas fechou.</summary>
    None,

    /// <summary>Assistente de configuração de conta.</summary>
    AccountSetup,

    /// <summary>Editor de Diretório de Domínio.</summary>
    DomainDirectory,

    /// <summary>Gestão de regras automáticas.</summary>
    Rules,

    /// <summary>Categorias, modelos e listas de remetentes.</summary>
    Organization,
}
