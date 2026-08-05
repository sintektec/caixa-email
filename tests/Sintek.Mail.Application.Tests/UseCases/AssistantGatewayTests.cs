using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Assistant;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a política de privacidade da assistência por IA — a parte que a fase 8 põe antes
/// dos recursos: quem processa, com qual consentimento e o que entra na auditoria.
/// </summary>
public class AssistantGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory;
    private readonly Account _account;

    public AssistantGatewayTests()
    {
        _directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _account = Account.Create(
            _directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _directories.GetByIdAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns(_directory);
    }

    /// <summary>Provedor de teste com localidade e disponibilidade controladas.</summary>
    private sealed class FakeProvider : IAssistantProvider
    {
        public FakeProvider(string id, AssistantLocality locality, bool available = true)
        {
            Id = id;
            Locality = locality;
            Available = available;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public AssistantLocality Locality { get; }

        public bool Available { get; set; }

        public int CallCount { get; private set; }

        public string? LastContent { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Available);

        public Task<AssistantResponse> CompleteAsync(
            AssistantRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContent = request.Content;
            return Task.FromResult(AssistantResponse.Success($"resposta de {Id}"));
        }
    }

    private AssistantGateway CreateGateway(params IAssistantProvider[] providers)
        => new(providers, _accounts, _directories, _audit, _unitOfWork, _clock,
            NullLogger<AssistantGateway>.Instance);

    private static AssistantRequest Request()
        => new(AssistantTask.Summarize, "Conteúdo sigiloso da mensagem");

    // ----- Escolha de provedor ---------------------------------------------------------

    [Fact]
    public async Task Pedido_ComModeloLocalDisponivel_UsaOLocal_MesmoComNuvemAutorizada()
    {
        // Autorizar a nuvem é dizer que PODE, não que DEVE. Preferir a nuvem por ser
        // melhor transformaria o consentimento em formalidade.
        _directory.SetCloudAssistantConsent(true, Now);

        var local = new FakeProvider("local", AssistantLocality.Local);
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        var result = await CreateGateway(cloud, local).RequestAsync(_account.Id, Request());

        result.Succeeded.Should().BeTrue();
        result.ProviderId.Should().Be("local");
        local.CallCount.Should().Be(1);
        cloud.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Pedido_SemLocalEComConsentimento_UsaANuvem()
    {
        _directory.SetCloudAssistantConsent(true, Now);

        var local = new FakeProvider("local", AssistantLocality.Local, available: false);
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        var result = await CreateGateway(local, cloud).RequestAsync(_account.Id, Request());

        result.Succeeded.Should().BeTrue();
        result.ProviderId.Should().Be("cloud");
    }

    [Fact]
    public async Task Pedido_SemLocalESemConsentimento_ERecusado()
    {
        var local = new FakeProvider("local", AssistantLocality.Local, available: false);
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        var result = await CreateGateway(local, cloud).RequestAsync(_account.Id, Request());

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(AssistantRefusal.CloudNotConsented);

        // O que mais importa: nada saiu da máquina.
        cloud.CallCount.Should().Be(0);
        result.UserMessage.Should().Contain("Diretório de Domínio");
    }

    [Fact]
    public async Task Pedido_SemProvedorAlgum_InformaQueNadaEstaConfigurado()
    {
        var result = await CreateGateway().RequestAsync(_account.Id, Request());

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(AssistantRefusal.NoProviderAvailable);
    }

    [Fact]
    public async Task Pedido_ContaSemDiretorioResolvivel_NaoAutorizaANuvem()
    {
        // Na dúvida o conteúdo fica na máquina: o custo dos dois erros não é simétrico.
        _directories.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((DomainDirectory?)null);

        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        var result = await CreateGateway(cloud).RequestAsync(_account.Id, Request());

        result.Refusal.Should().Be(AssistantRefusal.CloudNotConsented);
        cloud.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Consentimento_NasceDesligado()
    {
        // Sem ninguém tocar em nada, um diretório recém-criado não autoriza a nuvem.
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        var result = await CreateGateway(cloud).RequestAsync(_account.Id, Request());

        _directory.AllowsCloudAssistant.Should().BeFalse();
        result.Refusal.Should().Be(AssistantRefusal.CloudNotConsented);
    }

    [Fact]
    public async Task Consentimento_Revogado_VoltaARecusar()
    {
        _directory.SetCloudAssistantConsent(true, Now);
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);
        var gateway = CreateGateway(cloud);

        (await gateway.RequestAsync(_account.Id, Request())).Succeeded.Should().BeTrue();

        _directory.SetCloudAssistantConsent(false, Now);

        (await gateway.RequestAsync(_account.Id, Request())).Refusal
            .Should().Be(AssistantRefusal.CloudNotConsented);
        cloud.CallCount.Should().Be(1, "a segunda chamada não pode ter saído");
    }

    // ----- Auditoria -------------------------------------------------------------------

    [Fact]
    public async Task EnvioParaNuvem_EhRegistrado_SemOConteudo()
    {
        _directory.SetCloudAssistantConsent(true, Now);
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        AuditLogEntry? recorded = null;
        await _audit.RecordAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>());

        await CreateGateway(cloud).RequestAsync(_account.Id, Request());

        recorded.Should().NotBeNull();
        recorded!.EventType.Should().Be(AuditEventType.AssistantCloudRequest);

        // A regra que vale para o resto do produto vale aqui: identificadores e destino,
        // nunca o conteúdo.
        recorded.Description.Should().NotContain("sigiloso");
        recorded.DetailsJson.Should().NotContain("sigiloso");
        recorded.DetailsJson.Should().Contain("contentLength");
    }

    [Fact]
    public async Task ProcessamentoLocal_NaoGeraRegistroDeEnvioExterno()
    {
        var local = new FakeProvider("local", AssistantLocality.Local);

        await CreateGateway(local).RequestAsync(_account.Id, Request());

        // Nada saiu da máquina: não há envio externo a registrar.
        await _audit.DidNotReceive().RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.AssistantCloudRequest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecusaPorConsentimento_EhRegistrada()
    {
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);

        await CreateGateway(cloud).RequestAsync(_account.Id, Request());

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.AssistantBlockedByConsent),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disponibilidade_SeguirAMesmaPolitica()
    {
        var cloud = new FakeProvider("cloud", AssistantLocality.Cloud);
        var gateway = CreateGateway(cloud);

        // A interface pergunta antes de mostrar os botões: sem consentimento, não há
        // assistente utilizável para esta conta.
        (await gateway.IsAvailableForAsync(_account.Id)).Should().BeFalse();

        _directory.SetCloudAssistantConsent(true, Now);
        (await gateway.IsAvailableForAsync(_account.Id)).Should().BeTrue();
    }
}
