using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma linha da lista de mensagens.</summary>
public sealed partial class MessageListItemViewModel : ObservableObject
{
    public required Guid MessageId { get; init; }

    /// <summary>Remetente exibido: o nome quando houver, o endereço caso contrário.</summary>
    public required string From { get; init; }

    public required string Subject { get; init; }

    /// <summary>Prévia do corpo.</summary>
    public required string Preview { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public bool HasAttachments { get; init; }

    [ObservableProperty]
    private bool _isRead;

    [ObservableProperty]
    private bool _isFlagged;

    public MessageImportance Importance { get; init; }

    /// <summary>Situação perante o servidor, exibida como indicador de sincronização.</summary>
    [ObservableProperty]
    private MessageSyncState _syncState;

    /// <summary>Domínio do remetente, exibido quando a pasta é restrita.</summary>
    public string? RelatedDomain { get; init; }

    /// <summary>Cores das categorias aplicadas.</summary>
    public IReadOnlyList<string> CategoryColors { get; init; } = [];

    /// <summary>
    /// Data formatada como nos clientes de e-mail: hora para hoje, dia da semana para
    /// esta semana, data completa para o resto.
    /// </summary>
    public string DisplayDate
    {
        get
        {
            var local = ReceivedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;

            if (local.Date == today)
            {
                return local.ToString("HH:mm");
            }

            return local.Date > today.AddDays(-7)
                ? local.ToString("ddd HH:mm")
                : local.ToString("dd/MM/yyyy");
        }
    }

    /// <summary>Se há alteração local ainda não propagada ao servidor.</summary>
    public bool HasPendingChanges => SyncState is not (MessageSyncState.Synced or MessageSyncState.LocalOnly);

    /// <summary>Descrição para leitores de tela, reunindo o que os ícones comunicam.</summary>
    public string AccessibleDescription
    {
        get
        {
            var parts = new List<string>
            {
                IsRead ? "Lida" : "Não lida",
                $"De {From}",
                $"Assunto {Subject}",
                DisplayDate,
            };

            if (HasAttachments)
            {
                parts.Add("Com anexo");
            }

            if (IsFlagged)
            {
                parts.Add("Sinalizada");
            }

            if (Importance == MessageImportance.High)
            {
                parts.Add("Importante");
            }

            if (HasPendingChanges)
            {
                parts.Add("Aguardando sincronização");
            }

            return string.Join(". ", parts);
        }
    }

    partial void OnSyncStateChanged(MessageSyncState value) => OnPropertyChanged(nameof(HasPendingChanges));
}

/// <summary>ViewModel do painel central: a lista de mensagens da pasta selecionada.</summary>
public sealed partial class MessageListViewModel : ObservableObject
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;

    public MessageListViewModel(IMessageRepository messages, IFolderRepository folders)
    {
        _messages = messages;
        _folders = folders;
    }

    /// <summary>Mensagens exibidas.</summary>
    public ObservableCollection<MessageListItemViewModel> Messages { get; } = [];

    /// <summary>Mensagem selecionada.</summary>
    [ObservableProperty]
    private MessageListItemViewModel? _selectedMessage;

    /// <summary>Pasta atualmente exibida.</summary>
    [ObservableProperty]
    private Guid? _folderId;

    /// <summary>Nome da pasta, exibido no cabeçalho do painel.</summary>
    [ObservableProperty]
    private string _folderName = string.Empty;

    /// <summary>Se a pasta é restrita por um Diretório de Domínio.</summary>
    [ObservableProperty]
    private bool _isFolderDomainRestricted;

    /// <summary>Se o agrupamento por conversa está ativo.</summary>
    [ObservableProperty]
    private bool _groupByConversation;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Carrega as mensagens da pasta.</summary>
    [RelayCommand]
    public async Task LoadFolderAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            FolderId = folderId;

            var folder = await _folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(true);
            FolderName = folder?.DisplayName ?? string.Empty;
            IsFolderDomainRestricted = folder?.IsDomainRestricted ?? false;

            Messages.Clear();

            var ids = await _messages.ListIdsByFolderAsync(folderId, cancellationToken).ConfigureAwait(true);

            foreach (var id in ids)
            {
                var message = await _messages.GetByIdAsync(id, cancellationToken).ConfigureAwait(true);
                if (message is not null)
                {
                    Messages.Add(ToListItem(message));
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static MessageListItemViewModel ToListItem(Message message) => new()
    {
        MessageId = message.Id,
        From = message.FromDisplayName ?? message.FromAddress?.Value ?? "(sem remetente)",
        Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(sem assunto)" : message.Subject,
        Preview = message.Preview,
        ReceivedAt = message.ReceivedAt,
        HasAttachments = message.HasAttachments,
        IsRead = message.IsRead,
        IsFlagged = message.IsFlagged,
        Importance = message.Importance,
        SyncState = message.SyncState,
        RelatedDomain = message.FromAddress?.Domain.Value,
    };
}
