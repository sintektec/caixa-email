using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Application.UseCases.Rules;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o editor de regras: montagem da definição a partir das linhas da tela e o ciclo
/// listar → editar → gravar.
/// </summary>
public class RulesViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IRuleRepository _rules = Substitute.For<IRuleRepository>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public RulesViewModelTests()
    {
        _rules.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Rule>());
        _accounts.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Account>());
        _folders.ListByAccountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());
        _categories.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Category>());
    }

    private RulesViewModel CreateViewModel()
    {
        var clock = new FakeTimeProvider(Now);
        return new RulesViewModel(
            new ManageRulesHandler(_rules, _unitOfWork, clock),
            new ManageCategoriesHandler(_categories, _unitOfWork, clock),
            _accounts,
            _folders);
    }

    [Fact]
    public async Task Inicializar_EditorNovo_ComecaComUmaCondicaoEUmaAcao()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.EditingRuleId.Should().BeNull();
        viewModel.Conditions.Should().ContainSingle();
        viewModel.Actions.Should().ContainSingle();
    }

    [Fact]
    public async Task Gravar_SemNome_MostraOAvisoENaoGrava()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SaveRuleAsync();

        viewModel.HasStatusMessage.Should().BeTrue();
        await _rules.DidNotReceive().AddAsync(Arg.Any<Rule>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gravar_RegraValida_MontaADefinicaoDasLinhas()
    {
        Rule? saved = null;
        await _rules.AddAsync(Arg.Do<Rule>(r => saved = r), Arg.Any<CancellationToken>());

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.RuleName = "Clientes VIP";
        viewModel.SelectedMatchType = viewModel.MatchTypes.First(m => m.Value == RuleMatchType.Any);

        viewModel.Conditions[0].SelectedField = SelectionOptions.RuleFields
            .First(f => f.Value == RuleField.Sender);
        viewModel.Conditions[0].SelectedOperator = SelectionOptions.RuleOperators
            .First(o => o.Value == RuleOperator.InDomain);
        viewModel.Conditions[0].Value = "cliente.com";

        viewModel.Actions[0].SelectedAction = SelectionOptions.RuleActions
            .First(a => a.Value == RuleActionType.MarkAsImportant);

        await viewModel.SaveRuleAsync();

        viewModel.HasStatusMessage.Should().BeFalse();
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Clientes VIP");
        saved.MatchType.Should().Be(RuleMatchType.Any);
        saved.Conditions.Should().ContainSingle(c =>
            c.Field == RuleField.Sender && c.Operator == RuleOperator.InDomain && c.Value == "cliente.com");
        saved.Actions.Should().ContainSingle(a => a.ActionType == RuleActionType.MarkAsImportant);
    }

    [Fact]
    public async Task Editar_RegraExistente_PreencheAsLinhasDoEditor()
    {
        var rule = Rule.Create("Antiga", Now, priority: 3);
        rule.AddCondition(RuleField.Subject, RuleOperator.Contains, "fatura", Now);
        rule.AddAction(RuleActionType.Flag, Now);
        _rules.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { rule });

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await viewModel.EditRuleAsync(rule.Id);

        viewModel.EditingRuleId.Should().Be(rule.Id);
        viewModel.RuleName.Should().Be("Antiga");
        viewModel.PriorityValue.Should().Be(3);
        viewModel.Conditions.Should().ContainSingle(c => c.Value == "fatura");
        viewModel.Actions.Should().ContainSingle(
            a => a.SelectedAction.Value == RuleActionType.Flag);
    }
}

/// <summary>
/// Cobre a organização: categorias, modelos e listas de remetentes na tela.
/// </summary>
public class OrganizationViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IMessageTemplateRepository _templates = Substitute.For<IMessageTemplateRepository>();
    private readonly ISenderReputationRepository _reputations = Substitute.For<ISenderReputationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public OrganizationViewModelTests()
    {
        _categories.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Category>());
        _templates.ListAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MessageTemplate>());
        _reputations.ListAsync(Arg.Any<SenderReputationKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SenderReputation>());
    }

    private OrganizationViewModel CreateViewModel()
    {
        var clock = new FakeTimeProvider(Now);
        return new OrganizationViewModel(
            new ManageCategoriesHandler(_categories, _unitOfWork, clock),
            new ManageTemplatesHandler(_templates, _unitOfWork, clock),
            new ManageSenderReputationHandler(_reputations, _unitOfWork, clock));
    }

    [Fact]
    public async Task GravarCategoria_SemNome_MostraAviso()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SaveCategoryAsync();

        viewModel.HasStatusMessage.Should().BeTrue();
    }

    [Fact]
    public async Task GravarCategoria_Valida_LimpaOEditorERecarrega()
    {
        Category? saved = null;
        await _categories.AddAsync(Arg.Do<Category>(c => saved = c), Arg.Any<CancellationToken>());

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.CategoryName = "Diretoria";
        viewModel.CategoryColor = "#FF0000";
        viewModel.CategoryShortcut = 1;

        await viewModel.SaveCategoryAsync();

        viewModel.HasStatusMessage.Should().BeFalse();
        viewModel.CategoryName.Should().BeEmpty();
        saved!.Name.Should().Be("Diretoria");
        saved.Shortcut.Should().Be(1);
    }

    [Fact]
    public async Task BloquearRemetente_TextoInvalido_MostraAviso()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.NewBlockedTarget = "isto não é um endereço @";
        await viewModel.AddBlockedSenderAsync();

        viewModel.HasStatusMessage.Should().BeTrue();
        await _reputations.DidNotReceive().AddAsync(
            Arg.Any<SenderReputation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BloquearRemetente_DominioValido_AdicionaELimpaOCampo()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.NewBlockedTarget = "promo.com";
        await viewModel.AddBlockedSenderAsync();

        viewModel.HasStatusMessage.Should().BeFalse();
        viewModel.NewBlockedTarget.Should().BeEmpty();
        await _reputations.Received(1).AddAsync(
            Arg.Is<SenderReputation>(s => s.Kind == SenderReputationKind.Blocked),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inicializar_SeparaBloqueadosDeConfiaveis()
    {
        _reputations.ListAsync((SenderReputationKind?)null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                SenderReputation.ForDomain(
                    SenderReputationKind.Blocked, EmailDomain.Parse("promo.com"), Now),
                SenderReputation.ForDomain(
                    SenderReputationKind.Trusted, EmailDomain.Parse("parceiro.com"), Now),
            });

        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.BlockedSenders.Should().ContainSingle(s => s.Target == "promo.com");
        viewModel.TrustedSenders.Should().ContainSingle(s => s.Target == "parceiro.com");
    }
}
