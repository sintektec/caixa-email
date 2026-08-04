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

    /// <summary>Identificador do locatário (Entra ID). "common" atende contas de qualquer locatário.</summary>
    public string TenantId { get; set; } = "common";

    /// <summary>URI de redirecionamento registrada no provedor.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>Indica se há Client ID configurado.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
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
