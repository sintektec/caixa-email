using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Estado de conectividade exibido na barra superior.</summary>
public enum ConnectivityState
{
    /// <summary>
    /// Ainda não se sabe: nenhuma sincronização foi tentada nesta sessão.
    /// </summary>
    /// <remarks>
    /// É o estado inicial, e precisa existir. Antes o padrão era <see cref="Offline"/>, e o
    /// aplicativo abria <b>afirmando estar sem conexão</b> — antes de ter tentado conectar uma
    /// única vez. Com a internet funcionando, isso não é um estado impreciso: é uma
    /// informação falsa, e das que fazem o usuário ir procurar defeito no roteador (D-049).
    /// </remarks>
    Unknown,

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
public sealed partial class ShellViewModel : ScopedViewModel
{
    /// <remarks>
    /// O relógio é singleton e entra por construtor como os demais ViewModels o recebem.
    /// Serve só para descrever há quanto tempo foi a última sincronização — cálculo de
    /// apresentação, que precisa ser verificável sem esperar o tempo passar.
    /// </remarks>
    public ShellViewModel(IServiceScopeFactory scopes, TimeProvider timeProvider)
        : base(scopes)
    {
        _timeProvider = timeProvider;
    }

    private readonly TimeProvider _timeProvider;

    /// <summary>Raízes da árvore de navegação.</summary>
    public ObservableCollection<NavigationNode> NavigationRoots { get; } = [];

    /// <summary>Nó selecionado.</summary>
    [ObservableProperty]
    private NavigationNode? _selectedNode;

    /// <summary>Estado de conectividade exibido na barra superior.</summary>
    [ObservableProperty]
    private ConnectivityState _connectivity = ConnectivityState.Unknown;

    /// <summary>Quantas operações aguardam sincronização.</summary>
    [ObservableProperty]
    private int _pendingOperationCount;

    /// <summary>Mensagem de erro ou aviso exibida ao usuário.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Se uma sincronização manual está em curso.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separado de <see cref="IsBusy"/> de propósito, e isso é a correção de um defeito. O
    /// botão de sincronizar desistia quando <c>IsBusy</c> estivesse ligado — e
    /// <c>LoadNavigationAsync</c> liga o mesmo sinalizador. Depois que o laço de sincronização
    /// passou a mandar a árvore recarregar a cada volta (D-038), essas recargas ficaram
    /// frequentes, e clicar em sincronizar durante uma delas **não fazia nada e não avisava
    /// nada** — o pior desfecho possível para um botão (D-043).
    /// </para>
    /// <para>
    /// Recarregar a árvore e sincronizar são operações diferentes, e uma não é motivo para
    /// recusar a outra: a leitura do banco não impede a conversa com o servidor.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool _isSyncing;

    /// <summary>Quanto da sincronização manual já passou, de 0 a 100.</summary>
    /// <remarks>
    /// Em porcentagem, e não fração, porque é o que o <c>ProgressBar</c> do WinUI espera com
    /// <c>Maximum="100"</c>. A conversão fica aqui, uma vez, e não espalhada pelo XAML.
    /// </remarks>
    [ObservableProperty]
    private double _syncProgressPercent;

    /// <summary>O que a sincronização está fazendo agora, em texto.</summary>
    /// <remarks>
    /// Vazio, nunca nulo: alimenta um <c>TextBlock</c>, e o WinUI lança ao receber nulo em
    /// propriedade de texto.
    /// </remarks>
    [ObservableProperty]
    private string _syncProgressDescription = string.Empty;

    /// <summary>Monta a árvore a partir do banco local.</summary>
    /// <remarks>
    /// A árvore inteira — diretórios, contas, pastas, pesquisas salvas e o contador da fila —
    /// sai de um escopo só, e portanto de um instante só do banco. Um escopo por leitura
    /// deixaria a montagem misturar dois estados quando a sincronização gravasse no meio dela:
    /// a conta de antes com as pastas de depois.
    /// </remarks>
    [RelayCommand]
    public async Task LoadNavigationAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            await InScopeAsync(
                async sp =>
                {
                    var directories = sp.GetRequiredService<IDomainDirectoryRepository>();
                    var accounts = sp.GetRequiredService<IAccountRepository>();
                    var folders = sp.GetRequiredService<IFolderRepository>();
                    var savedSearchRepository = sp.GetRequiredService<ISavedSearchRepository>();

                    NavigationRoots.Clear();

                    var favorites = new NavigationNode(
                        NavigationNodeKind.Section, "Favoritos", NavigationNode.FavoriteIcon);

                    var accountsRoot = new NavigationNode(
                        NavigationNodeKind.Section, "Contas e Diretórios", NavigationNode.DomainIcon);

                    foreach (var directory in await directories.ListAsync(cancellationToken).ConfigureAwait(true))
                    {
                        // A descrição é o nome que a pessoa deu; o domínio é o que a regra usa. Quem
                        // olha a árvore quer o primeiro — mas um diretório sem descrição viraria linha
                        // em branco, então o domínio continua sendo o rótulo de reserva.
                        var domainNode = new NavigationNode(
                            NavigationNodeKind.DomainDirectory,
                            string.IsNullOrWhiteSpace(directory.Description)
                                ? directory.DomainName.Value
                                : directory.Description,
                            NavigationNode.DomainIcon)
                        {
                            EntityId = directory.Id,
                        };

                        foreach (var account in await accounts
                            .ListByDomainAsync(directory.Id, cancellationToken).ConfigureAwait(true))
                        {
                            domainNode.Children.Add(
                                await BuildAccountNodeAsync(folders, account, favorites, cancellationToken)
                                    .ConfigureAwait(true));
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
                    var savedSearches = await savedSearchRepository.ListAsync(cancellationToken).ConfigureAwait(true);

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

                    await RefreshPendingCountAsync(
                        sp.GetRequiredService<IOutboxRepository>(), cancellationToken).ConfigureAwait(true);
                },
                cancellationToken).ConfigureAwait(true);

            ReportSyncProblems();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Leva à barra de status o que as contas registraram na última sincronização.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O laço de sincronização roda sozinho e não fala com a interface: quando ele falha,
    /// grava o motivo na conta e segue, para não morrer. Sem esta leitura o motivo ficava só
    /// no log de depuração, e a conta parada era indistinguível de uma conta sem mensagem
    /// nova — o usuário só descobria dias depois, procurando um e-mail que nunca chegou.
    /// </para>
    /// <para>
    /// Nomeia a conta porque com várias cadastradas "falha de sincronização" não diz onde
    /// mexer. Com mais de uma parada, mostra a primeira e conta as demais: a barra tem uma
    /// linha, e a árvore já marca cada uma delas.
    /// </para>
    /// <para>
    /// Não sobrescreve mensagem já posta. Uma recusa da regra de domínio acabou de ser
    /// explicada ao usuário, e trocá-la por um aviso de sincronização apagaria a resposta à
    /// ação que ele acabou de fazer.
    /// </para>
    /// </remarks>
    private void ReportSyncProblems()
    {
        if (!string.IsNullOrEmpty(StatusMessage))
        {
            return;
        }

        // Sincronização em curso tem a última palavra sobre o indicador. O laço manda a
        // árvore recarregar a cada volta, e sem esta guarda a recarga anunciaria "em dia" no
        // meio de uma sincronização que ainda está correndo.
        if (IsSyncing)
        {
            return;
        }

        var accounts = NavigationRoots
            .SelectMany(root => root.Children)
            .SelectMany(directory => directory.Children)
            .Where(node => node.Kind == NavigationNodeKind.Account)
            .ToList();

        var problems = accounts.Where(node => node.HasSyncProblem).ToList();

        // A mais recente entre as contas: é a que responde "isto está funcionando?". A mais
        // antiga responderia por uma conta desativada e assustaria à toa.
        LastSyncDescription = DescribeLastSync(
            accounts.Select(a => a.LastSyncAt).Where(d => d.HasValue).Max(),
            _timeProvider.GetUtcNow());

        if (problems.Count == 0)
        {
            Connectivity = ConnectivityState.Online;
            return;
        }

        var first = problems[0];
        var reason = string.IsNullOrWhiteSpace(first.SyncError)
            ? "a última sincronização falhou"
            : first.SyncError;

        StatusMessage = problems.Count == 1
            ? $"{first.Title}: {reason}"
            : $"{first.Title}: {reason} (e mais {problems.Count - 1} conta(s) com problema)";

        Connectivity = problems.Any(p => p.SyncStatus == AccountSyncStatus.AuthenticationFailed)
            ? ConnectivityState.Error
            : ConnectivityState.Offline;
    }

    /// <remarks>
    /// Recebe o repositório em vez de resolvê-lo: os nós da conta pertencem à mesma leitura
    /// da árvore, e as entidades de pasta não passam daqui — só viram <c>NavigationNode</c>.
    /// </remarks>
    private static async Task<NavigationNode> BuildAccountNodeAsync(
        IFolderRepository folderRepository,
        Account account,
        NavigationNode favorites,
        CancellationToken cancellationToken)
    {
        // Mesma escolha do diretório: a descrição é o nome que a pessoa deu, e é o que ela
        // procura na árvore. O endereço continua sendo o rótulo de reserva, porque conta sem
        // descrição viraria linha em branco — e porque, quando a descrição repete em duas
        // contas, é o endereço que desempata.
        var accountNode = new NavigationNode(
            NavigationNodeKind.Account,
            string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.EmailAddress.Value
                : account.DisplayName,
            NavigationNode.AccountIcon)
        {
            EntityId = account.Id,
            AccountId = account.Id,

            // O estado da última sincronização vem junto com o nó. Sem isto, conta parada
            // por senha expirada ficava idêntica a conta sem mensagem nova, e o motivo só
            // existia no log de depuração.
            SyncStatus = account.SyncStatus,
            SyncError = account.LastSyncError ?? string.Empty,
            LastSyncAt = account.LastSyncAt,
        };

        var folders = await folderRepository
            .ListByAccountAsync(account.Id, cancellationToken).ConfigureAwait(true);
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
        => await InScopeAsync(
            sp => RefreshPendingCountAsync(sp.GetRequiredService<IOutboxRepository>(), cancellationToken),
            cancellationToken).ConfigureAwait(true);

    /// <remarks>
    /// A sobrecarga interna deixa o contador ser lido dentro do escopo de quem enfileirou:
    /// quem grava e quem conta precisam do mesmo contexto, senão o número exibido é o de
    /// antes da gravação.
    /// </remarks>
    private async Task RefreshPendingCountAsync(
        IOutboxRepository outbox, CancellationToken cancellationToken)
    {
        var pending = await outbox.ListPendingAsync(null, cancellationToken).ConfigureAwait(true);
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
    /// <summary>
    /// Reposiciona um Diretório de Domínio ou uma conta na árvore.
    /// </summary>
    /// <param name="movedId">Identificador do item arrastado.</param>
    /// <param name="targetIndex">Posição de destino, entre os irmãos.</param>
    /// <returns>Se a nova ordem foi gravada.</returns>
    /// <remarks>
    /// <para>
    /// A árvore é reordenada <b>na memória primeiro</b> e só então gravada. É o que faz o
    /// item ficar onde foi solto em vez de saltar de volta e reaparecer no lugar certo um
    /// instante depois — e, se a gravação falhar, a árvore é recarregada do banco, que
    /// desfaz o movimento na tela sem inventar um estado intermediário.
    /// </para>
    /// <para>
    /// Um item só se move <b>entre os próprios irmãos</b>. Arrastar uma conta para outro
    /// diretório seria mudança de diretório, não reordenação: passa pela regra de
    /// pertinência, e o gesto de arrastar não é lugar para decidir isso em silêncio.
    /// </para>
    /// </remarks>
    public async Task<bool> ReorderNodeAsync(
        Guid movedId, int targetIndex, CancellationToken cancellationToken = default)
    {
        var siblings = FindSiblings(movedId);

        if (siblings is null)
        {
            return false;
        }

        var (parent, collection) = siblings.Value;
        var moved = collection.First(n => n.EntityId == movedId);
        var from = collection.IndexOf(moved);
        var to = Math.Clamp(targetIndex, 0, collection.Count - 1);

        if (from == to)
        {
            return true;
        }

        collection.Move(from, to);

        var orderedIds = collection.Select(n => n.EntityId).ToList();

        var result = await InScopeAsync(
            sp =>
            {
                var reorder = sp.GetRequiredService<ReorderNavigationHandler>();

                return moved.Kind == NavigationNodeKind.DomainDirectory
                    ? reorder.ReorderDirectoriesAsync(orderedIds, cancellationToken)
                    : reorder.ReorderAccountsAsync(parent!.EntityId, orderedIds, cancellationToken);
            },
            cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            return true;
        }

        StatusMessage = result.ErrorMessage;

        // Recarregar é mais honesto que desfazer o Move à mão: a recusa quase sempre
        // significa que a árvore na tela não corresponde mais ao banco. A recarga é outra
        // operação e abre o escopo dela — o da gravação já foi descartado aqui.
        await LoadNavigationAsync(cancellationToken).ConfigureAwait(true);
        return false;
    }

    /// <summary>
    /// Encontra a coleção de irmãos de um item reordenável, e o nó que a contém.
    /// </summary>
    /// <remarks>
    /// Devolve nada para qualquer outro tipo de nó — pasta, pesquisa salva, seção. Só
    /// diretório e conta têm posição manual; as pastas seguem a ordem do servidor.
    /// </remarks>
    private (NavigationNode? Parent, ObservableCollection<NavigationNode> Siblings)? FindSiblings(Guid movedId)
    {
        foreach (var root in NavigationRoots)
        {
            if (root.Children.Any(c => c.EntityId == movedId && c.Kind == NavigationNodeKind.DomainDirectory))
            {
                return (root, root.Children);
            }

            foreach (var directory in root.Children.Where(c => c.Kind == NavigationNodeKind.DomainDirectory))
            {
                if (directory.Children.Any(c => c.EntityId == movedId && c.Kind == NavigationNodeKind.Account))
                {
                    return (directory, directory.Children);
                }
            }
        }

        return null;
    }
    /// <remarks>
    /// Sem <c>[RelayCommand]</c>: o gerador do MVVM Toolkit aceita no máximo um parâmetro,
    /// e este método precisa de três. O arrastar e soltar chama o método diretamente do
    /// code-behind, que é onde a mensagem e a pasta de destino são conhecidas.
    /// </remarks>
    public async Task<bool> MoveMessageAsync(Guid messageId, Guid targetFolderId, bool userConfirmed = false)
        => await InScopeAsync(async sp =>
        {
            try
            {
                var result = await sp.GetRequiredService<MoveMessageHandler>()
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
                sp.GetRequiredService<ILogger<ShellViewModel>>().LogInformation(
                    "Movimentação recusada pela regra de domínio da pasta {FolderId}.", targetFolderId);
                return false;
            }
        }).ConfigureAwait(true);

    /// <summary>
    /// Sincroniza todas as contas ativas agora.
    /// </summary>
    /// <remarks>
    /// Uma conta que falha não interrompe as demais: em um cliente com várias contas, o
    /// servidor de uma delas fora do ar não pode deixar o usuário sem a correspondência das
    /// outras. O estado exibido é o pior encontrado — é o que ele precisa notar.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        // Só a própria sincronização impede outra. Recarga da árvore não é motivo: ela lê o
        // banco, e isso não atrapalha conversar com o servidor. Antes o botão desistia com
        // qualquer IsBusy ligado, calado, e o usuário via o clique não fazer nada (D-043).
        if (IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        Connectivity = ConnectivityState.Syncing;
        StatusMessage = null;
        SyncProgressPercent = 0;
        SyncProgressDescription = "Preparando…";

        try
        {
            // A varredura inteira num escopo só, como o AccountSyncWorker faz a cada volta.
            var reloadNavigation = await InScopeAsync(
                async sp =>
                {
                    var accountRepository = sp.GetRequiredService<IAccountRepository>();
                    var syncAccount = sp.GetRequiredService<SyncAccountHandler>();

                    // Da listagem saem só os identificadores: a sincronização reescreve as
                    // mesmas linhas, e a entidade lida antes dela já não vale nada.
                    var accountIds =
                        (await accountRepository.ListActiveAsync(cancellationToken).ConfigureAwait(true))
                        .Select(a => a.Id)
                        .ToList();

                    if (accountIds.Count == 0)
                    {
                        Connectivity = ConnectivityState.Offline;
                        StatusMessage = "Nenhuma conta cadastrada. Adicione uma conta para começar.";
                        return false;
                    }

                    var worst = ConnectivityState.Online;
                    string? firstError = null;
                    var indice = 0;

                    foreach (var accountId in accountIds)
                    {
                        indice++;

                        // O Progress<T> captura o contexto de sincronização de quem o cria —
                        // aqui, a thread da interface. É o que permite ao caso de uso relatar
                        // de onde estiver sem que o ViewModel precise despachar nada.
                        var posicao = indice;
                        var relator = new Progress<SyncProgressReport>(report =>
                        {
                            var comPosicao = report with
                            {
                                AccountIndex = posicao,
                                AccountCount = accountIds.Count,
                            };

                            SyncProgressPercent = comPosicao.Fraction * 100;
                            SyncProgressDescription = comPosicao.Description;
                        });

                        var result = await syncAccount
                            .HandleAsync(accountId, cancellationToken, relator).ConfigureAwait(true);

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
                    return true;
                },
                cancellationToken).ConfigureAwait(true);

            if (reloadNavigation)
            {
                await LoadNavigationAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Connectivity = ConnectivityState.Error;
            StatusMessage = ex.Message;

            // O escopo da operação já foi descartado quando a exceção chega aqui, e o
            // registrador precisa de um. Sem token: um cancelamento não pode calar o registro.
            await InScopeAsync(sp =>
            {
                sp.GetRequiredService<ILogger<ShellViewModel>>()
                    .LogError(ex, "A sincronização manual falhou.");
                return Task.CompletedTask;
            }).ConfigureAwait(true);
        }
        finally
        {
            IsSyncing = false;
            SyncProgressPercent = 0;
            SyncProgressDescription = string.Empty;
        }
    }

    /// <summary>O botão fica desabilitado enquanto a sincronização corre, e não inerte.</summary>
    /// <remarks>
    /// A diferença importa: botão desabilitado comunica "já estou fazendo"; botão habilitado
    /// que ignora o clique comunica "este programa está quebrado".
    /// </remarks>
    private bool CanSyncNow() => !IsSyncing;

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
        => await InScopeAsync(
            async sp =>
            {
                var result = await sp.GetRequiredService<MarkAsSpamHandler>()
                    .HandleAsync(messageId, isSpam, cancellationToken).ConfigureAwait(true);

                StatusMessage = result.ErrorMessage;

                if (result.Succeeded)
                {
                    await RefreshPendingCountAsync(
                        sp.GetRequiredService<IOutboxRepository>(), cancellationToken).ConfigureAwait(true);
                }

                return result.Succeeded;
            },
            cancellationToken).ConfigureAwait(true);

    /// <summary>
    /// Marca uma mensagem como lida ou não lida, propagando pela fila.
    /// </summary>
    /// <remarks>
    /// A gravação local e o enfileiramento acontecem no mesmo caso de uso; aqui só se
    /// traduz o gesto da interface.
    /// </remarks>
    public async Task<bool> SetMessageReadAsync(
        Guid messageId, bool isRead, CancellationToken cancellationToken = default)
        => await InScopeAsync(
            async sp =>
            {
                var changed = await sp.GetRequiredService<MessageFlagsHandler>()
                    .SetReadAsync(messageId, isRead, cancellationToken).ConfigureAwait(true);

                if (changed)
                {
                    await RefreshPendingCountAsync(
                        sp.GetRequiredService<IOutboxRepository>(), cancellationToken).ConfigureAwait(true);
                }

                return changed;
            },
            cancellationToken).ConfigureAwait(true);

    /// <summary>Move uma mensagem para a lixeira.</summary>
    public async Task<bool> DeleteMessageAsync(
        Guid messageId, CancellationToken cancellationToken = default)
        => await InScopeAsync(
            async sp =>
            {
                var result = await sp.GetRequiredService<MessageFlagsHandler>()
                    .MoveToTrashAsync(messageId, cancellationToken).ConfigureAwait(true);

                StatusMessage = result.ErrorMessage;

                if (result.Succeeded)
                {
                    await RefreshPendingCountAsync(
                        sp.GetRequiredService<IOutboxRepository>(), cancellationToken).ConfigureAwait(true);
                }

                return result.Succeeded;
            },
            cancellationToken).ConfigureAwait(true);

    /// <summary>Descrição textual do estado de conectividade, para leitores de tela.</summary>
    public string ConnectivityDescription => Connectivity switch
    {
        ConnectivityState.Unknown => "Ainda não sincronizado. Clique para sincronizar todas as contas.",
        ConnectivityState.Offline => "Sem conexão. As alterações serão sincronizadas quando a internet voltar.",
        ConnectivityState.Online => LastSyncDescription,
        ConnectivityState.Syncing => "Sincronizando mensagens.",
        ConnectivityState.Error => "Houve um erro na última sincronização.",
        _ => string.Empty,
    };

    /// <summary>
    /// Glifo do botão de sincronizar, conforme o estado.
    /// </summary>
    /// <remarks>
    /// O ícone era fixo, e só a dica mudava — para saber o que estava acontecendo era preciso
    /// parar o mouse em cima. Uma sincronização de dois segundos ninguém pegava, e conta com
    /// erro parecia igual a conta em dia. Estado que só existe na dica é estado que ninguém lê.
    /// </remarks>
    public string ConnectivityIcon => Connectivity switch
    {
        // Sync, SyncFolder, Error e Cloud offline, nesta ordem. Escapes, e não o caractere
        // literal: são da área de uso privado, e uma ferramenta que reescreva o arquivo os
        // perde em silêncio.
        ConnectivityState.Syncing => "\uE895",
        ConnectivityState.Error => "\uE783",
        ConnectivityState.Offline => "\uEB5E",

        // Unknown e Online usam o mesmo glifo de sincronizar: nenhum dos dois é alerta, e
        // inventar um terceiro ícone para "ainda não tentei" só acrescentaria ruído.
        _ => "\uE895",
    };

    /// <summary>Quando foi a última sincronização bem-sucedida, em texto.</summary>
    /// <remarks>
    /// O instante já estava gravado em <c>Account.LastSyncAt</c> desde a fase 3 e nunca
    /// chegou à tela. Sem ele, "conectado" não distingue uma conta sincronizada agora de uma
    /// parada desde ontem — e é justamente essa diferença que o usuário quer saber quando
    /// desconfia que algo não está chegando.
    /// </remarks>
    [ObservableProperty]
    private string _lastSyncDescription = "Ainda não sincronizado nesta sessão.";

    partial void OnConnectivityChanged(ConnectivityState value)
    {
        OnPropertyChanged(nameof(ConnectivityDescription));
        OnPropertyChanged(nameof(ConnectivityIcon));
    }

    partial void OnLastSyncDescriptionChanged(string value)
        => OnPropertyChanged(nameof(ConnectivityDescription));

    /// <summary>
    /// Converte o instante da última sincronização em algo que se lê de relance.
    /// </summary>
    /// <remarks>
    /// Texto relativo, e não um carimbo: "há 3 minutos" responde a pergunta que a pessoa tem
    /// ("isto está funcionando?"), enquanto "22:41" a obriga a olhar o relógio e subtrair. A
    /// hora absoluta entra só quando a distância deixa de ser útil.
    /// </remarks>
    internal static string DescribeLastSync(DateTimeOffset? lastSyncAt, DateTimeOffset now)
    {
        if (lastSyncAt is not { } instant)
        {
            return "Nenhuma conta sincronizou ainda.";
        }

        var elapsed = now - instant;

        // Relógio para trás — fuso, NTP, hibernação — não pode virar "há -4 minutos".
        if (elapsed < TimeSpan.Zero || elapsed < TimeSpan.FromSeconds(90))
        {
            return "Sincronizado agora há pouco.";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"Sincronizado há {(int)elapsed.TotalMinutes} minuto(s).";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"Sincronizado há {(int)elapsed.TotalHours} hora(s).";
        }

        return "Sincronizado há mais de um dia. Verifique as contas.";
    }
}
