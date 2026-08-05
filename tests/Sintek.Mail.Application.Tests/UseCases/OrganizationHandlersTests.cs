using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Application.UseCases.Rules;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a gestão de regras: validação da definição e reconstrução na gravação.
/// </summary>
public class ManageRulesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IRuleRepository _rules = Substitute.For<IRuleRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private ManageRulesHandler Handler() => new(_rules, _unitOfWork, new FakeTimeProvider(Now));

    [Fact]
    public async Task Gravar_RegraSemAcao_ERecusada()
    {
        var result = await Handler().SaveAsync(new RuleDefinition { Name = "Sem ação" });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ação");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gravar_MoverSemPastaDeDestino_ERecusado()
    {
        var result = await Handler().SaveAsync(new RuleDefinition
        {
            Name = "Mover sem destino",
            Actions = [new RuleActionDefinition(RuleActionType.MoveToFolder)],
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pasta");
    }

    [Fact]
    public async Task Gravar_RegraNova_MontaCondicoesEAcoes()
    {
        Rule? saved = null;
        await _rules.AddAsync(Arg.Do<Rule>(r => saved = r), Arg.Any<CancellationToken>());

        var result = await Handler().SaveAsync(new RuleDefinition
        {
            Name = "Clientes importantes",
            Priority = 2,
            MatchType = RuleMatchType.Any,
            StopProcessing = true,
            Conditions = [new RuleConditionDefinition(RuleField.Sender, RuleOperator.InDomain, "cliente.com")],
            Actions = [new RuleActionDefinition(RuleActionType.MarkAsImportant)],
        });

        result.Succeeded.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.Priority.Should().Be(2);
        saved.MatchType.Should().Be(RuleMatchType.Any);
        saved.StopProcessing.Should().BeTrue();
        saved.Conditions.Should().ContainSingle(c => c.Field == RuleField.Sender);
        saved.Actions.Should().ContainSingle(a => a.ActionType == RuleActionType.MarkAsImportant);
    }

    [Fact]
    public async Task Gravar_RegraExistente_ReconstroiADefinicao()
    {
        var existing = Rule.Create("Antiga", Now);
        existing.AddCondition(RuleField.Subject, RuleOperator.Contains, "velho", Now);
        existing.AddAction(RuleActionType.MarkAsRead, Now);
        _rules.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await Handler().SaveAsync(new RuleDefinition
        {
            RuleId = existing.Id,
            Name = "Renovada",
            Conditions = [new RuleConditionDefinition(RuleField.Subject, RuleOperator.Contains, "novo")],
            Actions = [new RuleActionDefinition(RuleActionType.Flag)],
        });

        result.Succeeded.Should().BeTrue();
        existing.Name.Should().Be("Renovada");
        existing.Conditions.Should().ContainSingle(c => c.Value == "novo");
        existing.Actions.Should().ContainSingle(a => a.ActionType == RuleActionType.Flag);
    }
}

/// <summary>
/// Cobre as listas de remetentes: o texto vira endereço ou domínio, e duplicata é recusada.
/// </summary>
public class ManageSenderReputationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ISenderReputationRepository _reputations = Substitute.For<ISenderReputationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public ManageSenderReputationHandlerTests()
        => _reputations.ListAsync(Arg.Any<SenderReputationKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SenderReputation>());

    private ManageSenderReputationHandler Handler()
        => new(_reputations, _unitOfWork, new FakeTimeProvider(Now));

    [Fact]
    public async Task Adicionar_TextoComArroba_ViraEnderecoExato()
    {
        SenderReputation? saved = null;
        await _reputations.AddAsync(Arg.Do<SenderReputation>(s => saved = s), Arg.Any<CancellationToken>());

        var result = await Handler().AddAsync(SenderReputationKind.Blocked, " Spam@Promo.com ");

        result.Succeeded.Should().BeTrue();
        saved!.Address.Should().NotBeNull();
        saved.Domain.Should().BeNull();

        // A parte local preserva o caso (a RFC a define sensível); só o domínio normaliza.
        saved.Target.Should().Be("Spam@promo.com");
    }

    [Fact]
    public async Task Adicionar_TextoSemArroba_ViraDominioInteiro()
    {
        SenderReputation? saved = null;
        await _reputations.AddAsync(Arg.Do<SenderReputation>(s => saved = s), Arg.Any<CancellationToken>());

        var result = await Handler().AddAsync(SenderReputationKind.Trusted, "parceiro.com");

        result.Succeeded.Should().BeTrue();
        saved!.Domain.Should().NotBeNull();
        saved.Address.Should().BeNull();
    }

    [Fact]
    public async Task Adicionar_EntradaDuplicada_ERecusada()
    {
        _reputations.ListAsync(SenderReputationKind.Blocked, Arg.Any<CancellationToken>())
            .Returns([SenderReputation.ForDomain(SenderReputationKind.Blocked, EmailDomain.Parse("promo.com"), Now)]);

        var result = await Handler().AddAsync(SenderReputationKind.Blocked, "promo.com");

        result.Succeeded.Should().BeFalse();
        await _reputations.DidNotReceive().AddAsync(Arg.Any<SenderReputation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confiavel_LiberaPorDominio()
    {
        _reputations.ListAsync(SenderReputationKind.Trusted, Arg.Any<CancellationToken>())
            .Returns([SenderReputation.ForDomain(SenderReputationKind.Trusted, EmailDomain.Parse("parceiro.com"), Now)]);

        (await Handler().IsTrustedAsync(EmailAddress.Parse("a@parceiro.com"), Guid.CreateVersion7()))
            .Should().BeTrue();
        (await Handler().IsTrustedAsync(EmailAddress.Parse("a@desconhecido.com"), Guid.CreateVersion7()))
            .Should().BeFalse();
    }
}

/// <summary>Cobre a gestão de categorias e de modelos de mensagem.</summary>
public class CategoriesAndTemplatesHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IMessageTemplateRepository _templates = Substitute.For<IMessageTemplateRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private ManageCategoriesHandler Categories()
        => new(_categories, _unitOfWork, new FakeTimeProvider(Now));

    private ManageTemplatesHandler Templates()
        => new(_templates, _unitOfWork, new FakeTimeProvider(Now));

    [Fact]
    public async Task Categoria_SemNome_ERecusada()
    {
        var result = await Categories().SaveAsync(null, "  ", "#FF0000", null);

        result.Succeeded.Should().BeFalse();
        await _categories.DidNotReceive().AddAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Categoria_AplicarDuasVezes_NaoDuplica()
    {
        var messageId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();

        _categories.IsAssignedAsync(messageId, categoryId, Arg.Any<CancellationToken>())
            .Returns(false, true);

        (await Categories().AssignAsync(messageId, categoryId)).Should().BeTrue();
        (await Categories().AssignAsync(messageId, categoryId)).Should().BeFalse();

        await _categories.Received(1).AssignAsync(
            Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Modelo_GravaEAtualizaPeloIdentificador()
    {
        MessageTemplate? saved = null;
        await _templates.AddAsync(Arg.Do<MessageTemplate>(t => saved = t), Arg.Any<CancellationToken>());

        var created = await Templates().SaveAsync(null, "Boas-vindas", "Bem-vindo", "<p>Olá!</p>");
        created.Succeeded.Should().BeTrue();
        saved.Should().NotBeNull();

        _templates.GetByIdAsync(saved!.Id, Arg.Any<CancellationToken>()).Returns(saved);

        var updated = await Templates().SaveAsync(saved.Id, "Boas-vindas v2", "Bem-vindo!", "<p>Olá de novo</p>");
        updated.Succeeded.Should().BeTrue();
        saved.Name.Should().Be("Boas-vindas v2");
    }
}
