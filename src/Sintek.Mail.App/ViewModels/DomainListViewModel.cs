using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using System.Collections.ObjectModel;

namespace Sintek.Mail.App.ViewModels;

public partial class DomainListViewModel : ObservableObject
{
    private readonly IMailRepository _repository;
    private readonly CreateDomainDirectoryHandler _createHandler;

    [ObservableProperty]
    private ObservableCollection<DomainDirectoryDto> _domains = new();

    [ObservableProperty]
    private DomainDirectoryDto? _selectedDomain;

    [ObservableProperty]
    private string _newDomainName = string.Empty;

    [ObservableProperty]
    private string _newDomainDescription = string.Empty;

    public DomainListViewModel(IMailRepository repository, CreateDomainDirectoryHandler createHandler)
    {
        _repository = repository;
        _createHandler = createHandler;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        CreateCommand = new AsyncRelayCommand(CreateAsync);
    }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand CreateCommand { get; }

    private async Task LoadAsync()
    {
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
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDomainName))
            return;

        var command = new CreateDomainDirectoryCommand(NewDomainName, NewDomainDescription);
        var result = await _createHandler.HandleAsync(command);
        await LoadAsync();
        NewDomainName = string.Empty;
        NewDomainDescription = string.Empty;
    }
}
