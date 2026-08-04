using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma conta na lista de configurações.</summary>
public sealed partial class AccountListItemViewModel : ObservableObject
{
    /// <summary>Identificador da conta.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Endereço.</summary>
    public required string EmailAddress { get; init; }

    /// <summary>Nome exibido.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Domínio do diretório a que pertence.</summary>
    public required string DomainName { get; init; }

    /// <summary>Servidor IMAP configurado.</summary>
    public required string ImapHost { get; init; }

    /// <summary>Servidor SMTP configurado.</summary>
    public required string SmtpHost { get; init; }

    /// <summary>Como a conta se autentica.</summary>
    public required AuthenticationType AuthenticationType { get; init; }

    /// <summary>Provedor de identidade, quando OAuth.</summary>
    public OAuthProviderKind OAuthProvider { get; init; }

    /// <summary>Estado da última sincronização.</summary>
    [ObservableProperty]
    private AccountSyncStatus _syncStatus;

    /// <summary>Se a conta está ativa.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Último erro de sincronização, quando houver.</summary>
    [ObservableProperty]
    private string? _lastSyncError;

    /// <summary>
    /// Descrição textual do estado, para leitores de tela e para a coluna de situação.
    /// </summary>
    public string StatusDescription => SyncStatus switch
    {
        AccountSyncStatus.NeverSynced => "Ainda não sincronizada.",
        AccountSyncStatus.Offline => "Sem conexão.",
        AccountSyncStatus.Online => "Conectada e em dia.",
        AccountSyncStatus.Syncing => "Sincronizando.",
        AccountSyncStatus.AuthenticationFailed => "As credenciais foram recusadas. É preciso reautenticar.",
        AccountSyncStatus.Disabled => "Desativada. Os dados locais continuam disponíveis.",
        _ => "Houve um erro na última sincronização.",
    };

    partial void OnSyncStatusChanged(AccountSyncStatus value)
        => OnPropertyChanged(nameof(StatusDescription));
}

/// <summary>
/// Lista as contas cadastradas e conduz a edição e a remoção.
/// </summary>
/// <remarks>
/// A remoção nunca acontece em um passo: <see cref="PrepareRemovalAsync"/> mede o que seria
/// perdido e <see cref="ConfirmRemovalAsync"/> executa. Apagar mensagens que só existem
/// neste computador — o caso das pastas locais — não pode ser consequência de um clique
/// desatento.
/// </remarks>
public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly IDomainDirectoryRepository _directories;
    private readonly UpdateAccountHandler _update;
    private readonly RemoveAccountHandler _remove;

    public AccountsViewModel(
        IAccountRepository accounts,
        IDomainDirectoryRepository directories,
        UpdateAccountHandler update,
        RemoveAccountHandler remove)
    {
        _accounts = accounts;
        _directories = directories;
        _update = update;
        _remove = remove;
    }

    /// <summary>Contas cadastradas.</summary>
    public ObservableCollection<AccountListItemViewModel> Accounts { get; } = [];

    /// <summary>Conta selecionada.</summary>
    [ObservableProperty]
    private AccountListItemViewModel? _selectedAccount;

    /// <summary>Impacto medido da remoção, exibido no pedido de confirmação.</summary>
    [ObservableProperty]
    private RemoveAccountImpact? _pendingRemovalImpact;

    /// <summary>Mensagem de erro ou aviso.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Se há um impacto medido aguardando confirmação.</summary>
    public bool HasPendingRemoval => PendingRemovalImpact is not null;

    /// <summary>Se alguma conta está selecionada.</summary>
    public bool HasSelection => SelectedAccount is not null;

    /// <summary>Resumo do que a remoção levaria junto, para exibição.</summary>
    public string RemovalSummary => PendingRemovalImpact is { } impact
        ? $"Remover '{impact.EmailAddress}' apagará {impact.FolderCount} pasta(s) e " +
          $"{impact.MessageCount} mensagem(ns) deste computador" +
          (impact.PendingOperationCount > 0
              ? $", além de descartar {impact.PendingOperationCount} operação(ões) aguardando sincronização."
              : ".")
        : string.Empty;

    /// <summary>Carrega as contas de todos os diretórios.</summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            Accounts.Clear();

            foreach (var directory in await _directories.ListAsync(cancellationToken).ConfigureAwait(true))
            {
                var accounts = await _accounts
                    .ListByDomainAsync(directory.Id, cancellationToken).ConfigureAwait(true);

                foreach (var account in accounts)
                {
                    Accounts.Add(new AccountListItemViewModel
                    {
                        AccountId = account.Id,
                        EmailAddress = account.EmailAddress.Value,
                        DisplayName = account.DisplayName,
                        DomainName = directory.DomainName.Value,
                        ImapHost = account.ImapHost,
                        SmtpHost = account.SmtpHost,
                        AuthenticationType = account.AuthenticationType,
                        OAuthProvider = account.OAuthProvider,
                        SyncStatus = account.SyncStatus,
                        IsActive = account.IsActive,
                        LastSyncError = account.LastSyncError,
                    });
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Ativa ou desativa a conta selecionada, sem exigir conexão.</summary>
    /// <remarks>
    /// Desativar uma conta cujo servidor saiu do ar precisa funcionar. Exigir teste de
    /// conexão aqui prenderia o usuário justamente à conta que ele quer parar de usar.
    /// </remarks>
    [RelayCommand]
    public async Task ToggleSelectedAccountAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is not { } item)
        {
            return;
        }

        var desiredState = !item.IsActive;

        var result = await _update.HandleAsync(
            new UpdateAccountCommand
            {
                AccountId = item.AccountId,
                DisplayName = item.DisplayName,
                ImapHost = item.ImapHost,
                SmtpHost = item.SmtpHost,
                IsActive = desiredState,
                TestBeforeSaving = desiredState,
            },
            cancellationToken).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage;
            return;
        }

        StatusMessage = null;
        item.IsActive = desiredState;
        item.SyncStatus = desiredState ? AccountSyncStatus.Online : AccountSyncStatus.Disabled;
    }

    /// <summary>Mede o que a remoção da conta selecionada levaria junto.</summary>
    [RelayCommand]
    public async Task PrepareRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is not { } item)
        {
            return;
        }

        PendingRemovalImpact = await _remove.AnalyzeAsync(item.AccountId, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Executa a remoção já confirmada.</summary>
    [RelayCommand]
    public async Task<bool> ConfirmRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is not { } item)
        {
            return false;
        }

        if (PendingRemovalImpact is null)
        {
            StatusMessage = "Verifique o que será removido antes de confirmar.";
            return false;
        }

        IsBusy = true;

        try
        {
            var result = await _remove.HandleAsync(item.AccountId, confirmed: true, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage;
                return false;
            }

            Accounts.Remove(item);
            SelectedAccount = null;
            PendingRemovalImpact = null;
            StatusMessage = null;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnSelectedAccountChanged(AccountListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        // O impacto medido pertence à conta que estava selecionada. Mantê-lo depois de
        // trocar a seleção permitiria confirmar a remoção de uma conta olhando os números
        // de outra.
        PendingRemovalImpact = null;
    }

    partial void OnPendingRemovalImpactChanged(RemoveAccountImpact? value)
    {
        OnPropertyChanged(nameof(HasPendingRemoval));
        OnPropertyChanged(nameof(RemovalSummary));
    }
}
