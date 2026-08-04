using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.DTOs;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using System.Collections.ObjectModel;

namespace Sintek.Mail.App.ViewModels;

public partial class MessageListViewModel : ObservableObject
{
    private readonly IMailRepository _repository;
    private readonly MoveMessageHandler _moveHandler;

    [ObservableProperty]
    private ObservableCollection<MessageDto> _messages = new();

    [ObservableProperty]
    private MessageDto? _selectedMessage;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    public MessageListViewModel(IMailRepository repository, MoveMessageHandler moveHandler)
    {
        _repository = repository;
        _moveHandler = moveHandler;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        MoveCommand = new AsyncRelayCommand<Guid>(MoveAsync);
    }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand<Guid> MoveCommand { get; }

    private async Task LoadAsync()
    {
        // TODO: Load messages for selected folder
        await Task.CompletedTask;
    }

    private async Task SearchAsync()
    {
        // TODO: Search messages
        await Task.CompletedTask;
    }

    private async Task MoveAsync(Guid targetFolderId)
    {
        if (SelectedMessage is null)
            return;

        var command = new MoveMessageCommand(SelectedMessage.Id, targetFolderId);
        await _moveHandler.HandleAsync(command);
        await LoadAsync();
    }
}
