using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Infrastructure.Assistant;

namespace Sintek.Mail.Infrastructure.Tests.Assistant;

/// <summary>
/// Cobre os provedores de IA: o que vai no pedido, o que sai da resposta e como cada um
/// se declara indisponível quando não está configurado.
/// </summary>
public class AssistantProviderTests
{
    /// <summary>Captura o que foi enviado e devolve uma resposta controlada.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, string? body = null)
        {
            Status = status;
            Body = body ?? """
                {"choices":[{"message":{"role":"assistant","content":"Resumo em tópicos."}}]}
                """;
        }

        public HttpStatusCode Status { get; }

        public string Body { get; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static IOptions<AssistantOptions> Options(
        string localEndpoint = "http://127.0.0.1:11434/v1/chat/completions",
        string cloudEndpoint = "",
        string cloudModel = "")
        => Microsoft.Extensions.Options.Options.Create(new AssistantOptions
        {
            Local = new LocalAssistantOptions { Endpoint = localEndpoint, Model = "modelo-local" },
            Cloud = new CloudAssistantOptions
            {
                Endpoint = cloudEndpoint,
                Model = cloudModel,
                CredentialKey = "chave-do-cofre",
            },
        });

    // ----- Modelo local ----------------------------------------------------------------

    [Fact]
    public void ModeloLocal_SeDeclaraLocal()
    {
        var provider = new LocalAssistantProvider(
            FactoryFor(new CapturingHandler()), Options());

        // A localidade é o que o guardião consulta antes de deixar conteúdo passar.
        provider.Locality.Should().Be(AssistantLocality.Local);
    }

    [Fact]
    public async Task ModeloLocal_SemEndereco_EstaIndisponivel()
    {
        var provider = new LocalAssistantProvider(
            FactoryFor(new CapturingHandler()), Options(localEndpoint: string.Empty));

        (await provider.IsAvailableAsync()).Should().BeFalse();

        var response = await provider.CompleteAsync(
            new AssistantRequest(AssistantTask.Summarize, "texto"));

        response.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ModeloLocal_EnviaModeloEConteudo_SemCredencial()
    {
        var handler = new CapturingHandler();
        var provider = new LocalAssistantProvider(FactoryFor(handler), Options());

        var response = await provider.CompleteAsync(
            new AssistantRequest(AssistantTask.Summarize, "corpo da mensagem"));

        response.Succeeded.Should().BeTrue();
        response.Text.Should().Be("Resumo em tópicos.");

        handler.LastRequestBody.Should().Contain("modelo-local");
        handler.LastRequestBody.Should().Contain("corpo da mensagem");

        // Runtime local não usa chave: mandar um cabeçalho de autorização vazio só
        // confundiria quem estivesse depurando.
        handler.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ModeloLocal_RespostaDeErro_NaoVazaOCorpoNaMensagem()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest, """{"error":"prompt: corpo sigiloso da mensagem"}""");

        var provider = new LocalAssistantProvider(FactoryFor(handler), Options());

        var response = await provider.CompleteAsync(
            new AssistantRequest(AssistantTask.Summarize, "corpo sigiloso da mensagem"));

        response.Succeeded.Should().BeFalse();
        response.ErrorMessage.Should().NotContain("sigiloso");
    }

    [Fact]
    public async Task ModeloLocal_RespostaVazia_EhTratadaComoFalha()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var provider = new LocalAssistantProvider(FactoryFor(handler), Options());

        var response = await provider.CompleteAsync(
            new AssistantRequest(AssistantTask.Rewrite, "texto"));

        response.Succeeded.Should().BeFalse();
    }

    // ----- Serviço em nuvem ------------------------------------------------------------

    [Fact]
    public async Task Nuvem_SemEndereco_EstaIndisponivel()
    {
        var credentials = Substitute.For<ICredentialStore>();
        credentials.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var provider = new CloudAssistantProvider(
            FactoryFor(new CapturingHandler()), Options(), credentials,
            NullLogger<CloudAssistantProvider>.Instance);

        (await provider.IsAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Nuvem_SemCredencialNoCofre_EstaIndisponivel()
    {
        var credentials = Substitute.For<ICredentialStore>();
        credentials.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var provider = new CloudAssistantProvider(
            FactoryFor(new CapturingHandler()),
            Options(cloudEndpoint: "https://api.exemplo.com/v1/chat/completions", cloudModel: "modelo"),
            credentials,
            NullLogger<CloudAssistantProvider>.Instance);

        // Indisponível é estado normal, não erro: a interface apresenta como
        // "não configurado".
        (await provider.IsAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Nuvem_ComCredencial_EnviaOCabecalhoDeAutorizacao()
    {
        var credentials = Substitute.For<ICredentialStore>();
        credentials.ExistsAsync("chave-do-cofre", Arg.Any<CancellationToken>()).Returns(true);
        credentials.GetSecretAsync("chave-do-cofre", Arg.Any<CancellationToken>())
            .Returns("valor-ficticio-da-chave");

        var handler = new CapturingHandler();
        var provider = new CloudAssistantProvider(
            FactoryFor(handler),
            Options(cloudEndpoint: "https://api.exemplo.com/v1/chat/completions", cloudModel: "modelo"),
            credentials,
            NullLogger<CloudAssistantProvider>.Instance);

        (await provider.IsAvailableAsync()).Should().BeTrue();

        var response = await provider.CompleteAsync(
            new AssistantRequest(AssistantTask.Summarize, "texto"));

        response.Succeeded.Should().BeTrue();
        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");

        // A chave sai do cofre a cada chamada, como as senhas de conta — nunca de arquivo
        // de configuração.
        await credentials.Received().GetSecretAsync("chave-do-cofre", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Nuvem_SeDeclaraNuvem()
    {
        var provider = new CloudAssistantProvider(
            FactoryFor(new CapturingHandler()), Options(),
            Substitute.For<ICredentialStore>(),
            NullLogger<CloudAssistantProvider>.Instance);

        provider.Locality.Should().Be(AssistantLocality.Cloud);
    }
}
