using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Persistence;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Composition.Tests;

/// <summary>
/// Verifica o contêiner que a aplicação monta, e não uma imitação dele.
/// </summary>
/// <remarks>
/// <para>
/// <b>Esta é a rede que faltava.</b> A aplicação rodou com um único <c>DbContext</c> para a
/// execução inteira desde a fase 1, e nenhum dos 987 testes percebeu — todos montam
/// repositórios direto, com o contexto que o próprio teste cria. O defeito só existe na
/// composição, então é lá que precisa ser procurado.
/// </para>
/// <para>
/// O sintoma, quando apareceu, foi <c>DbUpdateConcurrencyException</c> e travamento ao clicar
/// numa mensagem: o contexto da interface guardava valores antigos enquanto o laço de
/// sincronização, com escopo próprio, escrevia nas mesmas linhas. E <c>DbContext</c> não é
/// seguro para uso concorrente.
/// </para>
/// </remarks>
public class ContainerCompositionTests
{
    /// <summary>
    /// Monta a coleção como o <c>AppHost</c> monta, menos o que é Windows-only.
    /// </summary>
    /// <remarks>
    /// <c>ICredentialStore</c> e <c>IAttachmentStore</c> entram como dublês porque as
    /// implementações reais falam com o Gerenciador de Credenciais e com o disco. Tudo o mais
    /// vem de <c>AddSintekMailCore</c> — a mesma chamada do aplicativo.
    /// </remarks>
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<ICredentialStore>());
        services.AddSingleton(Substitute.For<IAttachmentStore>());

        return services.AddSintekMailCore(
            new ConfigurationBuilder().Build(),
            _ => new DatabaseOptions("/tmp/composicao-nao-aberta.db", "chave-de-teste"));
    }

    private static ServiceProvider BuildProvider()
        => BuildServices().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

    /// <summary>
    /// A própria construção é a asserção: com validação ligada, dependência cativa não passa.
    /// </summary>
    /// <remarks>
    /// Este teste <b>reprova</b> o registro que existia antes desta correção, com a mensagem
    /// "Cannot consume scoped service ... from singleton ShellViewModel". É a forma mais
    /// barata de trancar a invariante: não há o que asserir além de conseguir montar.
    /// </remarks>
    [Fact]
    public void Conteiner_ComValidacaoDeEscopo_ConstroiSemDependenciaCativa()
    {
        var acao = BuildProvider;

        acao.Should().NotThrow();
    }

    /// <summary>
    /// Os ViewModels que a janela guarda não podem depender de serviço com escopo.
    /// </summary>
    /// <remarks>
    /// Resolvê-los do provedor raiz é exatamente o que a aplicação faz, e com
    /// <c>ValidateScopes</c> ligado isso lança se algum voltar a receber repositório por
    /// construtor. É o caminho pelo qual o defeito entrou.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ShellViewModel))]
    [InlineData(typeof(MessageListViewModel))]
    [InlineData(typeof(ReadingPaneViewModel))]
    [InlineData(typeof(SearchViewModel))]
    [InlineData(typeof(AssistantViewModel))]
    public async Task ViewModelsResidentes_ResolvemDoProvedorRaiz(Type tipo)
    {
        await using var provider = BuildProvider();

        var acao = () => provider.GetRequiredService(tipo);

        acao.Should().NotThrow(
            "ViewModel residente da janela recebe IServiceScopeFactory, nunca repositório");
    }

    /// <summary>
    /// Os ViewModels de diálogo, ao contrário, <b>exigem</b> escopo.
    /// </summary>
    /// <remarks>
    /// A invariante é prendida nos dois sentidos de propósito. Exigir só que funcionem dentro
    /// do escopo deixaria alguém "consertar" uma falha promovendo um repositório a singleton —
    /// que faria o teste passar e devolveria o defeito.
    /// </remarks>
    [Theory]
    [InlineData(typeof(AccountSetupViewModel))]
    [InlineData(typeof(AccountsViewModel))]
    [InlineData(typeof(ComposerViewModel))]
    [InlineData(typeof(ContactsViewModel))]
    [InlineData(typeof(CalendarViewModel))]
    [InlineData(typeof(DomainDirectoryEditorViewModel))]
    [InlineData(typeof(FolderActionsViewModel))]
    [InlineData(typeof(MaintenanceViewModel))]
    [InlineData(typeof(OrganizationViewModel))]
    [InlineData(typeof(OutboxQueueViewModel))]
    [InlineData(typeof(RulesViewModel))]
    public async Task ViewModelsDeDialogo_ExigemEscopo(Type tipo)
    {
        await using var provider = BuildProvider();

        var noRaiz = () => provider.GetRequiredService(tipo);
        noRaiz.Should().Throw<InvalidOperationException>(
            "resolver do raiz prenderia o DbContext pela vida inteira da aplicação");

        // Escopo assíncrono, e não `using` comum: o MailKitImapClient implementa só
        // IAsyncDisposable, e descartar de forma síncrona um escopo que o resolveu lança
        // "type only implements IAsyncDisposable". Foi este teste que mostrou isso.
        await using var scope = provider.CreateAsyncScope();
        var noEscopo = () => scope.ServiceProvider.GetRequiredService(tipo);
        noEscopo.Should().NotThrow();
    }

    /// <summary>
    /// As portas de persistência são scoped, e o <c>DbContext</c> junto.
    /// </summary>
    /// <remarks>
    /// Promover qualquer uma a singleton faria o contêiner voltar a compartilhar um contexto
    /// entre a interface e o laço de sincronização — o defeito, por outro caminho.
    /// </remarks>
    [Fact]
    public void PortasDePersistencia_SaoScoped()
    {
        var services = BuildServices();

        var portas = typeof(Sintek.Mail.Application.Abstractions.Persistence.IUnitOfWork).Assembly
            .GetTypes()
            .Where(t => t.IsInterface
                && t.Namespace == "Sintek.Mail.Application.Abstractions.Persistence")
            .ToList();

        portas.Should().NotBeEmpty("a varredura precisa achar algo para ter valor");

        foreach (var porta in portas)
        {
            var registro = services.FirstOrDefault(d => d.ServiceType == porta);

            if (registro is null)
            {
                continue;
            }

            registro.Lifetime.Should().Be(
                ServiceLifetime.Scoped,
                "{0} é porta de persistência e carrega o DbContext junto",
                porta.Name);
        }

        services.First(d => d.ServiceType == typeof(MailDbContext))
            .Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
