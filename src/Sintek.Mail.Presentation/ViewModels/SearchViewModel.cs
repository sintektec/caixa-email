using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Application.UseCases.Search;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma pesquisa salva exibida na lista.</summary>
public sealed record SavedSearchItemViewModel(Guid Id, string Name, bool IsPinned, string QueryJson);

/// <summary>
/// ViewModel da pesquisa: campo de texto, filtros avançados e pesquisas salvas.
/// </summary>
/// <remarks>
/// A montagem de <see cref="MessageSearchQuery"/> vive aqui, e não no XAML ou no
/// code-behind, para ser testável no job Linux: cada combinação de filtro tem teste sem
/// precisar de uma janela.
/// </remarks>
public sealed partial class SearchViewModel : ScopedViewModel
{
    public SearchViewModel(IServiceScopeFactory scopes)
        : base(scopes)
    {
        SelectedReadState = ReadStates[0];
        SelectedFlagState = FlagStates[0];
        SelectedAttachmentState = AttachmentStates[0];
        SelectedImportance = ImportanceOptions[0];
        SelectedSyncState = SyncStateOptions[0];
    }

    /// <summary>Texto livre digitado na caixa de pesquisa.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Filtro por remetente: endereço ou nome exibido.</summary>
    [ObservableProperty]
    private string _fromFilter = string.Empty;

    /// <summary>Filtro por destinatário direto (Para).</summary>
    [ObservableProperty]
    private string _recipientFilter = string.Empty;

    /// <summary>Filtro por participante em cópia (CC).</summary>
    [ObservableProperty]
    private string _ccFilter = string.Empty;

    /// <summary>Filtro por assunto.</summary>
    [ObservableProperty]
    private string _subjectFilter = string.Empty;

    /// <summary>Filtro por corpo da mensagem.</summary>
    [ObservableProperty]
    private string _bodyFilter = string.Empty;

    /// <summary>Filtro por nome de anexo.</summary>
    [ObservableProperty]
    private string _attachmentNameFilter = string.Empty;

    /// <summary>Início do intervalo de datas, inclusivo.</summary>
    [ObservableProperty]
    private DateTimeOffset? _receivedFrom;

    /// <summary>Fim do intervalo de datas, inclusivo — estendido até o fim do dia.</summary>
    [ObservableProperty]
    private DateTimeOffset? _receivedUntil;

    /// <summary>Contas disponíveis, com "Todas as contas" em primeiro.</summary>
    public ObservableCollection<ScopeFilterOption> AccountOptions { get; } = [];

    /// <summary>Diretórios de Domínio disponíveis, com "Todos" em primeiro.</summary>
    public ObservableCollection<ScopeFilterOption> DomainOptions { get; } = [];

    /// <summary>Conta selecionada no filtro.</summary>
    [ObservableProperty]
    private ScopeFilterOption? _selectedAccount;

    /// <summary>Diretório de Domínio selecionado no filtro.</summary>
    [ObservableProperty]
    private ScopeFilterOption? _selectedDomain;

    /// <summary>Categorias disponíveis, com "Todas" em primeiro.</summary>
    public ObservableCollection<ScopeFilterOption> CategoryOptions { get; } = [];

    /// <summary>Categoria selecionada no filtro.</summary>
    [ObservableProperty]
    private ScopeFilterOption? _selectedCategory;

    /// <summary>Opções do filtro de leitura.</summary>
    public IReadOnlyList<TriStateFilterOption> ReadStates => SelectionOptions.ReadStateFilters;

    /// <summary>Opções do filtro de sinalizador.</summary>
    public IReadOnlyList<TriStateFilterOption> FlagStates => SelectionOptions.FlagStateFilters;

    /// <summary>Opções do filtro de anexos.</summary>
    public IReadOnlyList<TriStateFilterOption> AttachmentStates => SelectionOptions.AttachmentFilters;

    /// <summary>Opções do filtro de importância.</summary>
    public IReadOnlyList<ImportanceFilterOption> ImportanceOptions => SelectionOptions.ImportanceFilters;

    /// <summary>Opções do filtro de status de sincronização.</summary>
    public IReadOnlyList<SyncStateFilterOption> SyncStateOptions => SelectionOptions.SyncStateFilters;

    /// <summary>Filtro de leitura selecionado.</summary>
    [ObservableProperty]
    private TriStateFilterOption _selectedReadState;

    /// <summary>Filtro de sinalizador selecionado.</summary>
    [ObservableProperty]
    private TriStateFilterOption _selectedFlagState;

    /// <summary>Filtro de anexos selecionado.</summary>
    [ObservableProperty]
    private TriStateFilterOption _selectedAttachmentState;

    /// <summary>Filtro de importância selecionado.</summary>
    [ObservableProperty]
    private ImportanceFilterOption _selectedImportance;

    /// <summary>Filtro de status de sincronização selecionado.</summary>
    [ObservableProperty]
    private SyncStateFilterOption _selectedSyncState;

    /// <summary>Pesquisas salvas, fixadas primeiro.</summary>
    public ObservableCollection<SavedSearchItemViewModel> SavedSearches { get; } = [];

    /// <summary>Nome digitado para salvar a pesquisa atual.</summary>
    [ObservableProperty]
    private string _saveSearchName = string.Empty;

    /// <summary>Aviso exibido ao usuário.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Se há aviso a exibir.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Carrega contas, diretórios e pesquisas salvas para os filtros.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => await InScopeAsync(sp => LoadFiltersAsync(sp, cancellationToken), cancellationToken)
            .ConfigureAwait(true);

    /// <summary>Monta os critérios a partir do estado atual dos filtros.</summary>
    public MessageSearchQuery BuildQuery() => new()
    {
        Text = NullIfBlank(SearchText),
        From = NullIfBlank(FromFilter),
        Recipient = NullIfBlank(RecipientFilter),
        Cc = NullIfBlank(CcFilter),
        Subject = NullIfBlank(SubjectFilter),
        Body = NullIfBlank(BodyFilter),
        AttachmentName = NullIfBlank(AttachmentNameFilter),
        ReceivedFrom = ReceivedFrom,
        // O seletor devolve a meia-noite do dia escolhido; quem escolhe "até 10/08"
        // espera ver as mensagens DE 10/08, então o limite avança ao fim do dia.
        ReceivedUntil = ReceivedUntil?.AddDays(1).AddTicks(-1),
        AccountId = SelectedAccount?.Value,
        DomainDirectoryId = SelectedDomain?.Value,
        CategoryId = SelectedCategory?.Value,
        IsRead = SelectedReadState.Value,
        IsFlagged = SelectedFlagState.Value,
        HasAttachments = SelectedAttachmentState.Value,
        Importance = SelectedImportance.Value,
        SyncState = SelectedSyncState.Value,
    };

    /// <summary>
    /// Executa a pesquisa e devolve os identificadores encontrados, ou nulo quando não há
    /// critério algum.
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> ExecuteAsync(CancellationToken cancellationToken = default)
        => await InScopeAsync<IReadOnlyList<Guid>?>(
            sp => RunSearchAsync(sp, cancellationToken), cancellationToken).ConfigureAwait(true);

    /// <summary>Título exibido no painel central para os resultados.</summary>
    public string ResultsDescription
    {
        get
        {
            var text = SearchText.Trim();
            return text.Length > 0 ? $"Resultados de \"{text}\"" : "Resultados da pesquisa";
        }
    }

    /// <summary>Salva a pesquisa atual com o nome digitado.</summary>
    [RelayCommand]
    public async Task SaveCurrentSearchAsync(CancellationToken cancellationToken = default)
    {
        var name = SaveSearchName.Trim();

        if (name.Length == 0)
        {
            StatusMessage = "Dê um nome à pesquisa antes de salvar.";
            return;
        }

        var query = BuildQuery();

        if (!query.HasAnyCriteria)
        {
            StatusMessage = "Não há critérios para salvar: preencha a pesquisa primeiro.";
            return;
        }

        await InScopeAsync(
            async sp =>
            {
                var savedSearches = sp.GetRequiredService<SavedSearchesHandler>();

                // A pesquisa gravada é devolvida como entidade e descartada aqui de
                // propósito: a lista é remontada pela releitura, no mesmo escopo.
                await savedSearches.SaveAsync(name, query, isPinned: false, cancellationToken)
                    .ConfigureAwait(true);

                SaveSearchName = string.Empty;
                StatusMessage = null;
                await LoadSavedSearchesAsync(savedSearches, cancellationToken).ConfigureAwait(true);
            },
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Exclui uma pesquisa salva.</summary>
    public async Task DeleteSavedSearchAsync(
        SavedSearchItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await InScopeAsync(
            async sp =>
            {
                var savedSearches = sp.GetRequiredService<SavedSearchesHandler>();

                await savedSearches.DeleteAsync(item.Id, cancellationToken).ConfigureAwait(true);
                await LoadSavedSearchesAsync(savedSearches, cancellationToken).ConfigureAwait(true);
            },
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Executa uma pesquisa salva pelo identificador — o caminho da barra lateral.
    /// </summary>
    /// <remarks>
    /// Aplica os critérios aos filtros antes de executar, para que abrir o flyout em
    /// seguida mostre exatamente o que está sendo pesquisado.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>?> ExecuteSavedSearchAsync(
        Guid savedSearchId, CancellationToken cancellationToken = default)
        => await InScopeAsync<IReadOnlyList<Guid>?>(
            async sp =>
            {
                // Carregar os filtros e pesquisar é uma ação só do usuário, e por isso um
                // escopo só: os critérios aplicados aos filtros vêm da mesma leitura que a
                // pesquisa em seguida usa.
                await LoadFiltersAsync(sp, cancellationToken).ConfigureAwait(true);

                var item = SavedSearches.FirstOrDefault(s => s.Id == savedSearchId);

                if (item is null)
                {
                    StatusMessage = "A pesquisa salva não foi encontrada.";
                    return null;
                }

                ApplySavedSearch(item);
                return await RunSearchAsync(sp, cancellationToken).ConfigureAwait(true);
            },
            cancellationToken).ConfigureAwait(true);

    /// <summary>Preenche os filtros com os critérios de uma pesquisa salva.</summary>
    public void ApplySavedSearch(SavedSearchItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var query = SavedSearchesHandler.Deserialize(item.QueryJson);

        SearchText = query.Text ?? string.Empty;
        FromFilter = query.From ?? string.Empty;
        RecipientFilter = query.Recipient ?? string.Empty;
        CcFilter = query.Cc ?? string.Empty;
        SubjectFilter = query.Subject ?? string.Empty;
        BodyFilter = query.Body ?? string.Empty;
        AttachmentNameFilter = query.AttachmentName ?? string.Empty;
        ReceivedFrom = query.ReceivedFrom;
        // Desfaz a extensão ao fim do dia aplicada por BuildQuery, para o seletor de data
        // voltar a mostrar o dia escolhido.
        ReceivedUntil = query.ReceivedUntil?.AddTicks(1).AddDays(-1);

        SelectedAccount = AccountOptions.FirstOrDefault(o => o.Value == query.AccountId)
            ?? AccountOptions.FirstOrDefault();
        SelectedDomain = DomainOptions.FirstOrDefault(o => o.Value == query.DomainDirectoryId)
            ?? DomainOptions.FirstOrDefault();
        SelectedCategory = CategoryOptions.FirstOrDefault(o => o.Value == query.CategoryId)
            ?? CategoryOptions.FirstOrDefault();

        SelectedReadState = ReadStates.First(o => o.Value == query.IsRead);
        SelectedFlagState = FlagStates.First(o => o.Value == query.IsFlagged);
        SelectedAttachmentState = AttachmentStates.First(o => o.Value == query.HasAttachments);
        SelectedImportance = ImportanceOptions.First(o => o.Value == query.Importance);
        SelectedSyncState = SyncStateOptions.First(o => o.Value == query.SyncState);
    }

    /// <summary>Limpa todos os filtros, voltando ao estado inicial.</summary>
    [RelayCommand]
    public void ClearFilters()
    {
        SearchText = string.Empty;
        FromFilter = string.Empty;
        RecipientFilter = string.Empty;
        CcFilter = string.Empty;
        SubjectFilter = string.Empty;
        BodyFilter = string.Empty;
        AttachmentNameFilter = string.Empty;
        ReceivedFrom = null;
        ReceivedUntil = null;
        SelectedAccount = AccountOptions.FirstOrDefault();
        SelectedDomain = DomainOptions.FirstOrDefault();
        SelectedCategory = CategoryOptions.FirstOrDefault();
        SelectedReadState = ReadStates[0];
        SelectedFlagState = FlagStates[0];
        SelectedAttachmentState = AttachmentStates[0];
        SelectedImportance = ImportanceOptions[0];
        SelectedSyncState = SyncStateOptions[0];
        StatusMessage = null;
    }

    /// <summary>
    /// Preenche as listas de filtro e as pesquisas salvas num escopo já aberto por quem chama.
    /// </summary>
    /// <remarks>
    /// Recebe o provedor em vez de abrir escopo próprio para que executar uma pesquisa salva
    /// — carregar os filtros e pesquisar — leia tudo do mesmo contexto. Das entidades lidas
    /// aqui só saem identificador e rótulo, dentro de <see cref="ScopeFilterOption"/>.
    /// </remarks>
    private async Task LoadFiltersAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var accounts = services.GetRequiredService<IAccountRepository>();
        var directories = services.GetRequiredService<IDomainDirectoryRepository>();
        var categories = services.GetRequiredService<ICategoryRepository>();

        AccountOptions.Clear();
        AccountOptions.Add(new ScopeFilterOption(null, "Todas as contas"));
        foreach (var account in await accounts.ListActiveAsync(cancellationToken).ConfigureAwait(true))
        {
            AccountOptions.Add(new ScopeFilterOption(account.Id, account.EmailAddress.Value));
        }

        DomainOptions.Clear();
        DomainOptions.Add(new ScopeFilterOption(null, "Todos os diretórios"));
        foreach (var directory in await directories.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            DomainOptions.Add(new ScopeFilterOption(directory.Id, directory.DomainName.Value));
        }

        CategoryOptions.Clear();
        CategoryOptions.Add(new ScopeFilterOption(null, "Todas as categorias"));
        foreach (var category in await categories.ListAsync(null, cancellationToken).ConfigureAwait(true))
        {
            CategoryOptions.Add(new ScopeFilterOption(category.Id, category.Name));
        }

        SelectedAccount = AccountOptions[0];
        SelectedDomain = DomainOptions[0];
        SelectedCategory = CategoryOptions[0];

        await LoadSavedSearchesAsync(
            services.GetRequiredService<SavedSearchesHandler>(), cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Executa a pesquisa num escopo já aberto por quem chama.</summary>
    private async Task<IReadOnlyList<Guid>?> RunSearchAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var query = BuildQuery();

        if (!query.HasAnyCriteria)
        {
            StatusMessage = "Digite um termo ou escolha ao menos um filtro para pesquisar.";
            return null;
        }

        StatusMessage = null;
        return await services.GetRequiredService<ISearchService>()
            .SearchAsync(query, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Remonta a lista de pesquisas salvas com o manipulador do escopo em curso.
    /// </summary>
    /// <remarks>
    /// A entidade lida vira <see cref="SavedSearchItemViewModel"/> aqui dentro: é esse
    /// registro, e não a entidade, que fica guardado na coleção depois do escopo fechar.
    /// </remarks>
    private async Task LoadSavedSearchesAsync(
        SavedSearchesHandler savedSearches, CancellationToken cancellationToken)
    {
        SavedSearches.Clear();

        foreach (var saved in await savedSearches.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            SavedSearches.Add(new SavedSearchItemViewModel(
                saved.Id, saved.Name, saved.IsPinned, saved.QueryJson));
        }
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));
}
