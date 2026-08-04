using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.App.ViewModels;

/// <summary>Estado de conectividade exibido na barra superior.</summary>
public enum ConnectivityState
{
    /// <summary>Sem conexão. A aplicação continua utilizável com os dados locais.</summary>
    Offline,

    /// <summary>Conectado e em dia.</summary>
    Online,

    /// <summary>Sincronização em andamento.</summary>
    Syncing,

    /// <summary>Houve erro na última sincronização.</summary>
    Error,
}

/// <summary>
/// ViewModel da janela principal: monta a árvore de navegação e coordena os painéis.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IOutboxRepository _outbox;
    private readonly MoveMessageHandler _moveMessage;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IFolderRepository folders,
        IOutboxRepository outbox,
        MoveMessageHandler moveMessage,
        ILogger<ShellViewModel> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _folders = folders;
        _outbox = outbox;
        _moveMessage = moveMessage;
        _logger = logger;
    }

    /// <summary>Raízes da árvore de navegação.</summary>
    public ObservableCollection<NavigationNode> NavigationRoots { get; } = [];

    /// <summary>Nó selecionado.</summary>
    [ObservableProperty]
    private NavigationNode? _selectedNode;

    /// <summary>Estado de conectividade exibido na barra superior.</summary>
    [ObservableProperty]
    private ConnectivityState _connectivity = ConnectivityState.Offline;

    /// <summary>Texto da busca.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Quantas operações aguardam sincronização.</summary>
    [ObservableProperty]
    private int _pendingOperationCount;

    /// <summary>Mensagem de erro ou aviso exibida ao usuário.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Monta a árvore a partir do banco local.</summary>
    [RelayCommand]
    public async Task LoadNavigationAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            NavigationRoots.Clear();

            var favorites = new NavigationNode(
                NavigationNodeKind.Section, "Favoritos", NavigationNode.FavoriteIcon);

            var accountsRoot = new NavigationNode(
                NavigationNodeKind.Section, "Contas e Diretórios", NavigationNode.DomainIcon);

            foreach (var directory in await _directories.ListAsync(cancellationToken).ConfigureAwait(true))
            {
                var domainNode = new NavigationNode(
                    NavigationNodeKind.DomainDirectory,
                    directory.DomainName.Value,
                    NavigationNode.DomainIcon)
                {
                    EntityId = directory.Id,
                };

                foreach (var account in await _accounts
                    .ListByDomainAsync(directory.Id, cancellationToken).ConfigureAwait(true))
                {
                    domainNode.Children.Add(
                        await BuildAccountNodeAsync(account, favorites, cancellationToken).ConfigureAwait(true));
                }

                // O contador do domínio é a soma das contas: é o que o usuário espera ver
                // ao manter o nível recolhido.
                domainNode.UnreadCount = domainNode.Children.Sum(c => c.UnreadCount);
                accountsRoot.Children.Add(domainNode);
            }

            if (favorites.Children.Count > 0)
            {
                NavigationRoots.Add(favorites);
            }

            NavigationRoots.Add(accountsRoot);

            await RefreshPendingCountAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<NavigationNode> BuildAccountNodeAsync(
        Account account, NavigationNode favorites, CancellationToken cancellationToken)
    {
        var accountNode = new NavigationNode(
            NavigationNodeKind.Account,
            account.EmailAddress.Value,
            NavigationNode.AccountIcon)
        {
            EntityId = account.Id,
            AccountId = account.Id,
        };

        var folders = await _folders.ListByAccountAsync(account.Id, cancellationToken).ConfigureAwait(true);
        var nodesByFolderId = new Dictionary<Guid, NavigationNode>();

        // Duas passagens: a primeira cria os nós, a segunda os conecta. Uma passagem só
        // falharia sempre que uma subpasta aparecesse antes da sua pasta-mãe na lista.
        foreach (var folder in folders.OrderBy(f => f.SortOrder).ThenBy(f => f.FolderType))
        {
            nodesByFolderId[folder.Id] = new NavigationNode(
                NavigationNodeKind.Folder,
                folder.DisplayName,
                NavigationNode.IconForFolder(folder.FolderType))
            {
                EntityId = folder.Id,
                AccountId = account.Id,
                UnreadCount = folder.UnreadCount,
                TotalCount = folder.TotalCount,
                IsDomainRestricted = folder.IsDomainRestricted,
                IsRestrictionInherited = folder.IsRestrictionInherited,
            };
        }

        foreach (var folder in folders)
        {
            var node = nodesByFolderId[folder.Id];

            if (folder.ParentFolderId.HasValue
                && nodesByFolderId.TryGetValue(folder.ParentFolderId.Value, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                accountNode.Children.Add(node);
            }

            if (folder.IsFavorite)
            {
                favorites.Children.Add(node);
            }
        }

        accountNode.UnreadCount = folders
            .Where(f => f.FolderType == FolderType.Inbox)
            .Sum(f => f.UnreadCount);

        return accountNode;
    }

    /// <summary>Atualiza o contador da fila de sincronização.</summary>
    public async Task RefreshPendingCountAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _outbox.ListPendingAsync(null, cancellationToken).ConfigureAwait(true);
        PendingOperationCount = pending.Count;
    }

    /// <summary>
    /// Move uma mensagem para uma pasta — o caminho usado pelo arrastar e soltar.
    /// </summary>
    /// <remarks>
    /// A interface <b>não</b> avalia a regra de domínio por conta própria: ela chama o
    /// caso de uso e apenas apresenta o resultado. Duplicar a regra aqui abriria a porta
    /// para as duas versões divergirem e a interface acabar permitindo o que o domínio
    /// proíbe.
    /// </remarks>
    [RelayCommand]
    public async Task<bool> MoveMessageAsync(Guid messageId, Guid targetFolderId, bool userConfirmed = false)
    {
        try
        {
            var result = await _moveMessage
                .HandleAsync(new MoveMessageCommand(messageId, targetFolderId, userConfirmed))
                .ConfigureAwait(true);

            switch (result.Outcome)
            {
                case MoveMessageOutcome.RequiresConfirmation:
                    StatusMessage = result.UserMessage;
                    return false;

                case MoveMessageOutcome.MovedToPending:
                    StatusMessage =
                        "A mensagem foi movida para a pasta de pendências por não pertencer ao domínio da pasta.";
                    return true;

                default:
                    StatusMessage = null;
                    return true;
            }
        }
        catch (FolderDomainRestrictionException ex)
        {
            // Mensagem literal da especificação, redigida para leitura do usuário.
            StatusMessage = ex.UserMessage;
            _logger.LogInformation("Movimentação recusada pela regra de domínio da pasta {FolderId}.", targetFolderId);
            return false;
        }
    }

    /// <summary>Descrição textual do estado de conectividade, para leitores de tela.</summary>
    public string ConnectivityDescription => Connectivity switch
    {
        ConnectivityState.Offline => "Sem conexão. As alterações serão sincronizadas quando a internet voltar.",
        ConnectivityState.Online => "Conectado e sincronizado.",
        ConnectivityState.Syncing => "Sincronizando mensagens.",
        ConnectivityState.Error => "Houve um erro na última sincronização.",
        _ => string.Empty,
    };

    partial void OnConnectivityChanged(ConnectivityState value)
        => OnPropertyChanged(nameof(ConnectivityDescription));
}
