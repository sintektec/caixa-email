using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>Credenciais de aplicativo de um provedor OAuth.</summary>
/// <remarks>
/// O Client ID não é segredo (ele aparece na URL de consentimento), mas é configuração de
/// implantação: cada organização registra o próprio aplicativo no Entra ID ou no Google
/// Cloud Console. Por isso vem de configuração externa e não do código.
/// </remarks>
public sealed class OAuthClientOptions
{
    /// <summary>Client ID registrado no provedor.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret, <b>obrigatório no Google e inexistente no Entra ID</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Os dois provedores discordam sobre isto, e a discordância não é de estilo. No Entra ID
    /// um cliente público não tem segredo — o <c>PublicClientApplicationBuilder</c> sequer
    /// expõe <c>WithClientSecret</c>, e o PKCE é o que protege o fluxo.
    /// </para>
    /// <para>
    /// A Google emite <b>Client ID e Client secret</b> para o tipo "Desktop app", e exige o
    /// <c>client_secret</c> tanto na troca do código quanto na renovação por
    /// <c>refresh_token</c>. Só os tipos iOS e Android saem sem segredo. A própria Google
    /// documenta que o valor é embutido no aplicativo e que um app instalado não guarda
    /// segredo de verdade — ele identifica o aplicativo, não o autentica.
    /// </para>
    /// <para>
    /// <b>Por isso ele fica em configuração e não no cofre:</b> é credencial de aplicativo,
    /// não de usuário. O que vai para o <c>ICredentialStore</c> é o token de atualização, esse
    /// sim equivalente à senha de quem entrou. A regra de "nenhum segredo no banco" continua
    /// valendo por inteiro.
    /// </para>
    /// </remarks>
    public string? ClientSecret { get; set; }

    /// <summary>Identificador do locatário (Entra ID). "common" atende contas de qualquer locatário.</summary>
    public string TenantId { get; set; } = "common";

    /// <summary>URI de redirecionamento registrada no provedor.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>Indica se há Client ID configurado.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Indica se há Client ID <b>e</b> Client secret — o que o Google exige.
    /// </summary>
    /// <remarks>
    /// Separado de <see cref="IsConfigured"/> de propósito: exigir segredo do Entra ID
    /// deixaria a autenticação Microsoft permanentemente "não configurada", já que ela não
    /// tem nem pode ter um.
    /// </remarks>
    public bool IsConfiguredWithSecret
        => IsConfigured && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Configuração de todos os provedores OAuth.</summary>
public sealed class OAuthOptions
{
    /// <summary>Nome da seção em arquivo de configuração.</summary>
    public const string SectionName = "OAuth";

    /// <summary>Credenciais do Microsoft Entra ID.</summary>
    public OAuthClientOptions Microsoft { get; set; } = new();

    /// <summary>Credenciais do Google Cloud.</summary>
    public OAuthClientOptions Google { get; set; } = new();
}

/// <inheritdoc cref="IOAuthProviderRegistry" />
/// <remarks>
/// Resolver o provedor por enum, em vez de escolher por <c>if</c> espalhado pelo código,
/// é o que permite acrescentar um provedor novo registrando uma implementação — sem tocar
/// em nenhum consumidor.
/// </remarks>
public sealed class OAuthProviderRegistry : IOAuthProviderRegistry
{
    private readonly Dictionary<OAuthProviderKind, IOAuthProvider> _providers;

    public OAuthProviderRegistry(IEnumerable<IOAuthProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToDictionary(p => p.Provider);
    }

    /// <inheritdoc />
    public IOAuthProvider? Resolve(OAuthProviderKind provider)
        => _providers.GetValueOrDefault(provider);

    /// <inheritdoc />
    public IReadOnlyList<IOAuthProvider> ConfiguredProviders
        => _providers.Values.Where(p => p.IsConfigured).ToList();
}
