using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.UseCases.Assistant;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>
/// ViewModel dos recursos de IA: resumo, sugestão de resposta e reescrita.
/// </summary>
/// <remarks>
/// A recusa por falta de consentimento é apresentada como estado normal, com a explicação
/// de onde autorizar — e não como erro. O usuário que não autorizou não errou nada; ele
/// escolheu, e a tela precisa refletir isso.
/// </remarks>
public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly AssistantFeaturesHandler _features;

    public AssistantViewModel(AssistantFeaturesHandler features) => _features = features;

    /// <summary>Mensagem sobre a qual os recursos operam.</summary>
    [ObservableProperty]
    private Guid? _messageId;

    /// <summary>Conta em uso, para os recursos que não partem de uma mensagem.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Se a conta tem algum assistente utilizável.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>Texto produzido pelo assistente.</summary>
    [ObservableProperty]
    private string _result = string.Empty;

    /// <summary>Aviso ou explicação de recusa.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Se há resultado a exibir.</summary>
    public bool HasResult => Result.Length > 0;

    /// <summary>Se há aviso a exibir.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>
    /// Se o aviso trata de consentimento — a interface o mostra com ação para abrir as
    /// configurações do diretório, em vez do texto solto.
    /// </summary>
    [ObservableProperty]
    private bool _needsCloudConsent;

    /// <summary>Prepara o painel para uma mensagem.</summary>
    public async Task InitializeAsync(
        Guid accountId, Guid? messageId = null, CancellationToken cancellationToken = default)
    {
        AccountId = accountId;
        MessageId = messageId;
        Result = string.Empty;
        StatusMessage = null;
        NeedsCloudConsent = false;

        IsAvailable = await _features.IsAvailableForAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Resume a mensagem carregada.</summary>
    [RelayCommand]
    public async Task SummarizeAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is not { } messageId)
        {
            return;
        }

        await RunAsync(
            () => _features.SummarizeMessageAsync(messageId, cancellationToken)).ConfigureAwait(true);
    }

    /// <summary>Sugere uma resposta para a mensagem carregada.</summary>
    [RelayCommand]
    public async Task SuggestReplyAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is not { } messageId)
        {
            return;
        }

        await RunAsync(
            () => _features.SuggestReplyAsync(messageId, null, cancellationToken)).ConfigureAwait(true);
    }

    /// <summary>Reescreve um texto do compositor.</summary>
    public async Task<string?> RewriteAsync(
        string text, string? instruction = null, CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId)
        {
            return null;
        }

        await RunAsync(
            () => _features.RewriteAsync(accountId, text, instruction, cancellationToken))
            .ConfigureAwait(true);

        return HasResult ? Result : null;
    }

    private async Task RunAsync(Func<Task<AssistantResult>> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        NeedsCloudConsent = false;

        try
        {
            var result = await operation().ConfigureAwait(true);

            if (result.Succeeded)
            {
                Result = result.Text;
                return;
            }

            Result = string.Empty;
            StatusMessage = result.UserMessage;
            NeedsCloudConsent = result.Refusal == AssistantRefusal.CloudNotConsented;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));
}
