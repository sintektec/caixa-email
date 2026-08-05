using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Application.UseCases.Search;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre a montagem dos critérios de pesquisa a partir dos filtros da tela e o ciclo das
/// pesquisas salvas — tudo sem janela, no job Linux.
/// </summary>
public class SearchViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ISearchService _searchService = Substitute.For<ISearchService>();
    private readonly ISavedSearchRepository _savedSearches = Substitute.For<ISavedSearchRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();

    public SearchViewModelTests()
    {
        _accounts.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Account>());
        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<DomainDirectory>());
        _savedSearches.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SavedSearch>());
    }

    private SearchViewModel CreateViewModel() => new(
        _searchService,
        new SavedSearchesHandler(_savedSearches, _unitOfWork, new FakeTimeProvider(Now)),
        _accounts,
        _directories);

    [Fact]
    public void MontarCriterios_CamposEmBranco_ViramNulosENaoStringVazia()
    {
        // Os campos ficam em string.Empty por exigência do WinUI; a consulta, porém,
        // precisa de nulos — string vazia viraria um MATCH sem termos.
        var query = CreateViewModel().BuildQuery();

        query.Text.Should().BeNull();
        query.From.Should().BeNull();
        query.Subject.Should().BeNull();
        query.HasAnyCriteria.Should().BeFalse();
    }

    [Fact]
    public void MontarCriterios_ComFiltrosPreenchidos_MapeiaCadaCampo()
    {
        var viewModel = CreateViewModel();
        viewModel.SearchText = " orçamento ";
        viewModel.FromFilter = "João";
        viewModel.RecipientFilter = "contato@sintek.com.br";
        viewModel.CcFilter = "copia@sintek.com.br";
        viewModel.SubjectFilter = "proposta";
        viewModel.BodyFilter = "valores";
        viewModel.AttachmentNameFilter = "planilha";
        viewModel.SelectedReadState = viewModel.ReadStates.First(o => o.Value == false);
        viewModel.SelectedFlagState = viewModel.FlagStates.First(o => o.Value == true);
        viewModel.SelectedImportance = viewModel.ImportanceOptions
            .First(o => o.Value == MessageImportance.High);
        viewModel.SelectedSyncState = viewModel.SyncStateOptions
            .First(o => o.Value == MessageSyncState.PendingUpdate);

        var query = viewModel.BuildQuery();

        query.Text.Should().Be("orçamento");
        query.From.Should().Be("João");
        query.Recipient.Should().Be("contato@sintek.com.br");
        query.Cc.Should().Be("copia@sintek.com.br");
        query.Subject.Should().Be("proposta");
        query.Body.Should().Be("valores");
        query.AttachmentName.Should().Be("planilha");
        query.IsRead.Should().BeFalse();
        query.IsFlagged.Should().BeTrue();
        query.Importance.Should().Be(MessageImportance.High);
        query.SyncState.Should().Be(MessageSyncState.PendingUpdate);
    }

    [Fact]
    public void MontarCriterios_DataFinal_EstendeAteOFimDoDia()
    {
        var viewModel = CreateViewModel();
        viewModel.ReceivedUntil = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        var query = viewModel.BuildQuery();

        // Quem escolhe "até 10/08" espera ver as mensagens DE 10/08; cortar à meia-noite
        // excluiria o dia inteiro escolhido.
        query.ReceivedUntil.Should().Be(
            new DateTimeOffset(2026, 8, 10, 23, 59, 59, TimeSpan.Zero)
                .AddSeconds(1).AddTicks(-1));
    }

    [Fact]
    public async Task Executar_SemCriterios_AvisaEDevolveNulo()
    {
        var viewModel = CreateViewModel();

        var result = await viewModel.ExecuteAsync();

        result.Should().BeNull();
        viewModel.StatusMessage.Should().NotBeNullOrWhiteSpace();
        await _searchService.DidNotReceive()
            .SearchAsync(Arg.Any<MessageSearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Executar_ComCriterios_DelegaAoServico()
    {
        var ids = new List<Guid> { Guid.CreateVersion7() };
        _searchService.SearchAsync(Arg.Any<MessageSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(ids);

        var viewModel = CreateViewModel();
        viewModel.SearchText = "fatura";

        var result = await viewModel.ExecuteAsync();

        result.Should().BeEquivalentTo(ids);
        viewModel.StatusMessage.Should().BeNull();
        viewModel.ResultsDescription.Should().Contain("fatura");
    }

    [Fact]
    public async Task Inicializar_ComContasEDiretorios_MontaAsOpcoesComEscopoTotalPrimeiro()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        _accounts.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new[] { account });
        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { directory });

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.AccountOptions.Should().HaveCount(2);
        viewModel.AccountOptions[0].Value.Should().BeNull("a primeira opção é 'todas as contas'");
        viewModel.AccountOptions[1].Label.Should().Be("contato@sintek.com.br");
        viewModel.DomainOptions[1].Label.Should().Be("sintek.com.br");
        viewModel.SelectedAccount.Should().Be(viewModel.AccountOptions[0]);
    }

    [Fact]
    public async Task AplicarPesquisaSalva_RestauraOsFiltrosNaTela()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        var query = new MessageSearchQuery
        {
            Text = "diretoria",
            From = "presidencia",
            IsRead = false,
            Importance = MessageImportance.High,
        };

        viewModel.ApplySavedSearch(new SavedSearchItemViewModel(
            Guid.CreateVersion7(), "Não lidas da diretoria", false,
            SavedSearchesHandler.Serialize(query)));

        viewModel.SearchText.Should().Be("diretoria");
        viewModel.FromFilter.Should().Be("presidencia");
        viewModel.SelectedReadState.Value.Should().BeFalse();
        viewModel.SelectedImportance.Value.Should().Be(MessageImportance.High);

        // E a volta: os filtros restaurados produzem a mesma consulta que foi salva.
        viewModel.BuildQuery().Text.Should().Be("diretoria");
        viewModel.BuildQuery().IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task SalvarPesquisaAtual_SemNome_AvisaENaoGrava()
    {
        var viewModel = CreateViewModel();
        viewModel.SearchText = "fatura";

        await viewModel.SaveCurrentSearchAsync();

        viewModel.StatusMessage.Should().NotBeNullOrWhiteSpace();
        await _savedSearches.DidNotReceive()
            .AddAsync(Arg.Any<SavedSearch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SalvarPesquisaAtual_ComNomeECriterios_GravaERecarregaALista()
    {
        _savedSearches.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SavedSearch?)null);

        var viewModel = CreateViewModel();
        viewModel.SearchText = "fatura";
        viewModel.SaveSearchName = "Faturas";

        await viewModel.SaveCurrentSearchAsync();

        await _savedSearches.Received(1).AddAsync(
            Arg.Is<SavedSearch>(s => s.Name == "Faturas"), Arg.Any<CancellationToken>());
        viewModel.SaveSearchName.Should().BeEmpty("o campo limpa após salvar");
    }

    [Fact]
    public async Task ExecutarPesquisaSalva_PeloIdentificador_AplicaOsFiltrosEExecuta()
    {
        var saved = SavedSearch.Create(
            "Não lidas", SavedSearchesHandler.Serialize(new MessageSearchQuery
            {
                Text = "diretoria",
                IsRead = false,
            }), Now);

        _savedSearches.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { saved });

        var ids = new List<Guid> { Guid.CreateVersion7() };
        _searchService.SearchAsync(Arg.Any<MessageSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(ids);

        var viewModel = CreateViewModel();

        var result = await viewModel.ExecuteSavedSearchAsync(saved.Id);

        result.Should().BeEquivalentTo(ids);

        // Os filtros da tela refletem o que foi executado: abrir o flyout em seguida
        // mostra exatamente a pesquisa em curso.
        viewModel.SearchText.Should().Be("diretoria");
        viewModel.SelectedReadState.Value.Should().BeFalse();

        await _searchService.Received(1).SearchAsync(
            Arg.Is<MessageSearchQuery>(q => q.Text == "diretoria" && q.IsRead == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutarPesquisaSalva_Inexistente_AvisaSemExecutar()
    {
        var viewModel = CreateViewModel();

        var result = await viewModel.ExecuteSavedSearchAsync(Guid.CreateVersion7());

        result.Should().BeNull();
        viewModel.StatusMessage.Should().NotBeNullOrWhiteSpace();
        await _searchService.DidNotReceive()
            .SearchAsync(Arg.Any<MessageSearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LimparFiltros_DepoisDePreencher_VoltaAoEstadoInicial()
    {
        var viewModel = CreateViewModel();
        viewModel.SearchText = "algo";
        viewModel.FromFilter = "alguém";
        viewModel.ReceivedFrom = Now;
        viewModel.SelectedReadState = viewModel.ReadStates[1];

        viewModel.ClearFilters();

        viewModel.SearchText.Should().BeEmpty();
        viewModel.FromFilter.Should().BeEmpty();
        viewModel.ReceivedFrom.Should().BeNull();
        viewModel.BuildQuery().HasAnyCriteria.Should().BeFalse();
    }
}
