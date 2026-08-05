using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Presentation.ViewModels;

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
    private readonly ISavedSearchRepository _savedSearches;
    private readonly MoveMessageHandler _moveMessage;
    private readonly MarkAsSpamHandler _markAsSpam;
    private readonly SyncAccountHandler _syncAccount;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IFolderRepository folders,
        IOutboxRepository outbox,
        ISavedSearchRepository savedSearches,
        MoveMessageHandler moveMessage,
        MarkAsSpamHandler markAsSpam,
        SyncAccountHandler syncAccount,
        ILogger<ShellViewModel> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _folders = folders;
        _outbox = outbox;
        _savedSearches = savedSearches;
        _moveMessage = moveMessage;
        _markAsSpam = markAsSpam;
        _syncAccount = syncAccount;
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

            // Pesquisas salvas na barra lateral, fixadas primeiro — selecionar uma executa
            // a pesquisa no painel central.
            var savedSearches = await _savedSearches.ListAsync(cancellationToken).ConfigureAwait(true);

            if (savedSearches.Count > 0)
            {
                var searchesRoot = new NavigationNode(
                    NavigationNodeKind.Section, "Pesquisas salvas", NavigationNode.SavedSearchIcon);

                foreach (var saved in savedSearches)
                {
                    searchesRoot.Children.Add(new NavigationNode(
                        NavigationNodeKind.SavedSearch, saved.Name, NavigationNode.SavedSearchIcon)
                    {
                        EntityId = saved.Id,
                    });
                }

                NavigationRoots.Add(searchesRoot);
            }

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
                IsFavorite = folder.IsFavorite,
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
    /// <remarks>
    /// Sem <c>[RelayCommand]</c>: o gerador do MVVM Toolkit aceita no máximo um parâmetro,
    /// e este método precisa de três. O arrastar e soltar chama o método diretamente do
    /// code-behind, que é onde a mensagem e a pasta de destino são conhecidas.
    /// </remarks>
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

    /// <summary>
    /// Sincroniza todas as contas ativas agora.
    /// </summary>
    /// <remarks>
    /// Uma conta que falha não interrompe as demais: em um cliente com várias contas, o
    /// servidor de uma delas fora do ar não pode deixar o usuário sem a correspondência das
    /// outras. O estado exibido é o pior encontrado — é o que ele precisa notar.
    /// </remarks>
    [RelayCommand]
    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Connectivity = ConnectivityState.Syncing;
        StatusMessage = null;

        try
        {
            var accounts = await _accounts.ListActiveAsync(cancellationToken).ConfigureAwait(true);

            if (accounts.Count == 0)
            {
                Connectivity = ConnectivityState.Offline;
                StatusMessage = "Nenhuma conta cadastrada. Adicione uma conta para começar.";
                return;
            }

            var worst = ConnectivityState.Online;
            string? firstError = null;

            foreach (var account in accounts)
            {
                var result = await _syncAccount.HandleAsync(account.Id, cancellationToken).ConfigureAwait(true);

                if (result.Succeeded)
                {
                    continue;
                }

                firstError ??= result.ErrorMessage;
                worst = result.IsAuthenticationFailure ? ConnectivityState.Error : worst;

                if (worst != ConnectivityState.Error)
                {
                    worst = ConnectivityState.Offline;
                }
            }

            Connectivity = worst;
            StatusMessage = firstError;

            await LoadNavigationAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Connectivity = ConnectivityState.Error;
            StatusMessage = ex.Message;
            _logger.LogError(ex, "A sincronização manual falhou.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Marca ou desmarca uma mensagem como spam.
    /// </summary>
    /// <remarks>
    /// As duas metades acontecem juntas: mover para a pasta de lixo eletrônico e aplicar a
    /// palavra-chave $Junk/$NotJunk que treina o filtro do servidor. Só mover deixaria o
    /// servidor classificando errado para sempre.
    /// </remarks>
    public async Task<bool> MarkAsSpamAsync(
        Guid messageId, bool isSpam, CancellationToken cancellationToken = default)
    {
        var result = await _markAsSpam.HandleAsync(messageId, isSpam, cancellationToken).ConfigureAwait(true);

        StatusMessage = result.ErrorMessage;

        if (result.Succeeded)
        {
            await RefreshPendingCountAsync(cancellationToken).ConfigureAwait(true);
        }

        return result.Succeeded;
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
