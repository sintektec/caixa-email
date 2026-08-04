using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using System.Collections.ObjectModel;

namespace Sintek.Mail.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IMailRepository _repository;
    private readonly SyncAccountHandler _syncHandler;

    [ObservableProperty]
    private ObservableCollection<DomainDirectoryDto> _domains = new();

    [ObservableProperty]
    private DomainDirectoryDto? _selectedDomain;

    [ObservableProperty]
    private ObservableCollection<MessageDto> _messages = new();

    [ObservableProperty]
    private MessageDto? _selectedMessage;

    [ObservableProperty]
    private string _statusMessage = "Pronto";

    public MainViewModel(IMailRepository repository, SyncAccountHandler syncHandler)
    {
        _repository = repository;
        _syncHandler = syncHandler;
        LoadDomainsCommand = new AsyncRelayCommand(LoadDomainsAsync);
        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync);
        SyncCommand = new AsyncRelayCommand(SyncAsync);
        SettingsCommand = new RelayCommand(OpenSettings);
    }

    public IAsyncRelayCommand LoadDomainsCommand { get; }
    public IAsyncRelayCommand AddAccountCommand { get; }
    public IAsyncRelayCommand SyncCommand { get; }
    public IRelayCommand SettingsCommand { get; }

    private async Task LoadDomainsAsync()
    {
        StatusMessage = "Carregando domínios...";
        var domains = await _repository.GetAllDomainsAsync();
        Domains.Clear();
        foreach (var d in domains)
        {
            Domains.Add(new DomainDirectoryDto(
                d.Id,
                d.DomainName,
                d.Description,
                d.ValidationMode,
                d.InvalidEmailAction,
                d.AllowSubdomains,
                d.IsActive,
                d.SortOrder,
                d.IsFavorite,
                0
            ));
        }
        StatusMessage = "Pronto";
    }

    private async Task AddAccountAsync()
    {
        // TODO: Show dialog to add account
        StatusMessage = "Adicionar conta — não implementado";
        await Task.CompletedTask;
    }

    private async Task SyncAsync()
    {
        StatusMessage = "Sincronizando...";
        // TODO: Sync all accounts
        await Task.CompletedTask;
        StatusMessage = "Sincronização concluída";
    }

    private void OpenSettings()
    {
        // TODO: Open settings page
        StatusMessage = "Configurações — não implementado";
    }
}
