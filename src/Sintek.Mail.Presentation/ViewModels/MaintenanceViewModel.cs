using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.UseCases.Maintenance;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Opção de idade mínima do conteúdo a descartar.</summary>
/// <param name="Value">Intervalo; zero alcança tudo.</param>
/// <param name="Label">Texto apresentado ao usuário.</param>
public sealed record CacheAgeOption(TimeSpan Value, string Label);

/// <summary>
/// ViewModel da limpeza de cache: mede primeiro, apaga depois de confirmado.
/// </summary>
/// <remarks>
/// Mesmo desenho de duas etapas da remoção de conta, de diretório e de pasta — o usuário
/// vê o tamanho do estrago antes de autorizar. Operação destrutiva sem número na frente é
/// pedir confiança cega.
/// </remarks>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly CacheMaintenanceHandler _maintenance;

    public MaintenanceViewModel(CacheMaintenanceHandler maintenance)
    {
        _maintenance = maintenance;
        SelectedAge = AgeOptions[1];
    }

    /// <summary>Idades oferecidas.</summary>
    public IReadOnlyList<CacheAgeOption> AgeOptions { get; } =
    [
        new(TimeSpan.Zero, "Todo o conteúdo baixado"),
        new(TimeSpan.FromDays(30), "Baixado há mais de 30 dias"),
        new(TimeSpan.FromDays(90), "Baixado há mais de 90 dias"),
        new(TimeSpan.FromDays(365), "Baixado há mais de um ano"),
    ];

    /// <summary>Idade escolhida.</summary>
    [ObservableProperty]
    private CacheAgeOption _selectedAge;

    /// <summary>Resumo do que será descartado.</summary>
    [ObservableProperty]
    private string _impactSummary = string.Empty;

    /// <summary>Se a confirmação está pendente.</summary>
    [ObservableProperty]
    private bool _hasPendingCleanup;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Mede o que a limpeza descartaria, sem alterar nada.</summary>
    [RelayCommand]
    public async Task AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            var impact = await _maintenance.AnalyzeAsync(SelectedAge.Value, cancellationToken)
                .ConfigureAwait(true);

            ImpactSummary = impact.Summary;
            HasPendingCleanup = impact.HasAnything;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Executa a limpeza depois de confirmada.</summary>
    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPendingCleanup)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            var impact = await _maintenance.CleanAsync(SelectedAge.Value, cancellationToken)
                .ConfigureAwait(true);

            ImpactSummary =
                $"Limpeza concluída: {impact.BodyCount} corpo(s) e {impact.AttachmentCount} anexo(s) " +
                "descartados. O conteúdo volta a ser baixado quando você abrir as mensagens.";
            HasPendingCleanup = false;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
