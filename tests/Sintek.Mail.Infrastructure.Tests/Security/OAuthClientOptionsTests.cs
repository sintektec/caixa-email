using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Infrastructure.Security;
using Sintek.Mail.Infrastructure.Tests.Calendar;

namespace Sintek.Mail.Infrastructure.Tests.Security;

/// <summary>
/// Cobre a discordância entre os dois provedores sobre <c>client secret</c>.
/// </summary>
/// <remarks>
/// <para>
/// Não é diferença de estilo: no Entra ID um cliente público <b>não tem</b> segredo — o
/// <c>PublicClientApplicationBuilder</c> sequer expõe <c>WithClientSecret</c>. A Google emite
/// Client ID <b>e</b> Client secret para o tipo "Desktop app" e exige o segundo na troca do
/// código e na renovação por <c>refresh_token</c>.
/// </para>
/// <para>
/// Estes testes existem porque o erro que eles pegam é invisível até a implantação: sem o
/// segredo, o assistente anuncia a conta Google como configurada, o navegador abre, o usuário
/// consente — e só então a troca do código falha com
/// <c>invalid_request: client_secret is missing</c>. A biblioteca da Google omite o campo nulo
/// do corpo sem lançar nada.
/// </para>
/// </remarks>
public class OAuthClientOptionsTests
{
    private static GoogleOAuthProvider CreateGoogle(OAuthClientOptions google)
        => new(
            Options.Create(new OAuthOptions { Google = google }),
            Substitute.For<ICredentialStore>(),
            NullLogger<GoogleOAuthProvider>.Instance);

    private static MicrosoftOAuthProvider CreateMicrosoft(OAuthClientOptions microsoft)
        => new(
            Options.Create(new OAuthOptions { Microsoft = microsoft }),
            Substitute.For<ICredentialStore>(),
            NullLogger<MicrosoftOAuthProvider>.Instance);

    [Fact]
    public void Google_SemClientSecret_NaoContaComoConfigurado()
        => CreateGoogle(new OAuthClientOptions { ClientId = "id.apps.googleusercontent.com" })
            .IsConfigured.Should().BeFalse();

    [Fact]
    public void Google_ComOsDoisValores_ContaComoConfigurado()
        => CreateGoogle(new OAuthClientOptions
        {
            ClientId = "id.apps.googleusercontent.com",
            ClientSecret = FakeSecret.For("google-client-secret"),
        })
            .IsConfigured.Should().BeTrue();

    /// <summary>
    /// Exigir segredo do Entra ID deixaria a autenticação Microsoft permanentemente "não
    /// configurada", já que ela não tem nem pode ter um.
    /// </summary>
    [Fact]
    public void Microsoft_SemClientSecret_ContaComoConfigurado()
        => CreateMicrosoft(new OAuthClientOptions
        {
            ClientId = "00000000-0000-0000-0000-000000000000",
        })
            .IsConfigured.Should().BeTrue();

    /// <summary>
    /// A mensagem precisa citar as duas chaves: quem lê só "Client ID" preenche uma e volta
    /// a bater no mesmo erro.
    /// </summary>
    [Fact]
    public async Task Google_SemClientSecret_ExplicaAsDuasChaves()
    {
        var provider = CreateGoogle(new OAuthClientOptions
        {
            ClientId = "id.apps.googleusercontent.com",
        });

        var acao = async () => await provider.AuthenticateInteractivelyAsync("joao@exemplo.com");

        (await acao.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("OAuth:Google:ClientId").And
            .Contain("OAuth:Google:ClientSecret");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("id", null, false)]
    [InlineData(null, "segredo", false)]
    [InlineData("id", "  ", false)]
    [InlineData("id", "segredo", true)]
    public void IsConfiguredWithSecret_ExigeOsDois(string? id, string? secret, bool esperado)
        => new OAuthClientOptions { ClientId = id, ClientSecret = secret }
            .IsConfiguredWithSecret.Should().Be(esperado);
}
