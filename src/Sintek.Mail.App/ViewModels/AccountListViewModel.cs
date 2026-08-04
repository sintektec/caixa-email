using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Enums;
using System.Collections.ObjectModel;

namespace Sintek.Mail.App.ViewModels;

public partial class AccountListViewModel : ObservableObject
{
    private readonly IMailRepository _repository;
    private readonly AddAccountHandler _addHandler;

    [ObservableProperty]
    private ObservableCollection<AccountDto> _accounts = new();

    [ObservableProperty]
    private AccountDto? _selectedAccount;

    [ObservableProperty]
    private string _newEmail = string.Empty;

    [ObservableProperty]
    private string _newDisplayName = string.Empty;

    public AccountListViewModel(IMailRepository repository, AddAccountHandler addHandler)
    {
        _repository = repository;
        _addHandler = addHandler;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        AddCommand = new AsyncRelayCommand(AddAsync);
    }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand AddCommand { get; }

    private async Task LoadAsync()
    {
        // TODO: Load accounts for selected domain
        await Task.CompletedTask;
    }

    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEmail))
            return;

        // TODO: Get domain ID and server settings from UI
        var command = new AddAccountCommand(
            DomainId: Guid.Empty,
            EmailAddress: NewEmail,
            DisplayName: NewDisplayName,
            ImapHost: "imap.example.com",
            ImapPort: 993,
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            UseSsl: true,
            ImapSecurity: SecurityProtocol.Ssl,
            SmtpSecurity: SecurityProtocol.StartTls,
            AuthenticationType: AuthenticationType.Basic
        );

        var result = await _addHandler.HandleAsync(command);
        await LoadAsync();
        NewEmail = string.Empty;
        NewDisplayName = string.Empty;
    }
}
