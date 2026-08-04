using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>
/// Autenticação OAuth 2.0 no Google para Gmail e Google Workspace.
/// </summary>
/// <remarks>
/// O Gmail não aceita mais senha comum em IMAP/SMTP: OAuth 2.0 é o único caminho para
/// contas Google, o que torna este provedor obrigatório e não opcional.
/// </remarks>
public sealed class GoogleOAuthProvider : IOAuthProvider
{
    /// <summary>Escopo que habilita IMAP e SMTP; o Gmail recusa tokens com escopo menor.</summary>
    private const string MailScope = "https://mail.google.com/";

    private readonly OAuthClientOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<GoogleOAuthProvider> _logger;

    public GoogleOAuthProvider(
        IOptions<OAuthOptions> options,
        ICredentialStore credentials,
        ILogger<GoogleOAuthProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Google;
        _credentials = credentials;
        _logger = logger;
    }

    /// <inheritdoc />
    public OAuthProviderKind Provider => OAuthProviderKind.Google;

    /// <inheritdoc />
    public bool IsConfigured => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _options.ClientId },
            [MailScope],
            emailAddress,
            cancellationToken,
            new CredentialStoreDataStore(_credentials)).ConfigureAwait(false);

        var token = credential.Token;
        if (token.IsStale)
        {
            await credential.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            token = credential.Token;
        }

        _logger.LogInformation("Autenticação Google concluída para uma conta de e-mail.");
        return ToAccessToken(token);
    }

    /// <inheritdoc />
    public async Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = _options.ClientId },
            Scopes = [MailScope],
            DataStore = new CredentialStoreDataStore(_credentials),
        });

        var token = await flow.LoadTokenAsync(emailAddress, cancellationToken).ConfigureAwait(false)
            ?? throw new ReauthenticationRequiredException(emailAddress);

        if (!token.IsStale)
        {
            return ToAccessToken(token);
        }

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new ReauthenticationRequiredException(emailAddress);
        }

        try
        {
            var refreshed = await flow
                .RefreshTokenAsync(emailAddress, token.RefreshToken, cancellationToken)
                .ConfigureAwait(false);

            return ToAccessToken(refreshed);
        }
        catch (TokenResponseException ex)
        {
            // O Google revoga tokens de atualização quando o usuário troca a senha ou
            // revoga o acesso. Não há como recuperar em silêncio.
            throw new ReauthenticationRequiredException(emailAddress, ex);
        }
    }

    /// <inheritdoc />
    public Task SignOutAsync(string emailAddress, CancellationToken cancellationToken = default)
        => _credentials.DeleteSecretAsync(CacheKey(emailAddress), cancellationToken);

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "A autenticação Google não está configurada: crie um projeto no Google Cloud Console e " +
                "informe o Client ID em OAuth:Google:ClientId.");
        }
    }

    private static OAuthAccessToken ToAccessToken(TokenResponse token)
    {
        var issued = token.IssuedUtc;
        var expiresIn = token.ExpiresInSeconds ?? 3600;

        return new OAuthAccessToken(
            token.AccessToken,
            new DateTimeOffset(issued, TimeSpan.Zero).AddSeconds(expiresIn));
    }

    internal static string CacheKey(string emailAddress) => $"Sintek.Mail:oauth:google:{emailAddress}";

    /// <summary>
    /// Adapta o armazenamento de tokens do Google para o Windows Credential Manager.
    /// </summary>
    /// <remarks>
    /// A biblioteca do Google usa <c>FileDataStore</c> por padrão, que grava o token de
    /// atualização em arquivo JSON no perfil do usuário. Um token de atualização do Gmail
    /// dá acesso completo à caixa postal — deixá-lo em arquivo contrariaria a exigência da
    /// especificação de manter todo segredo no cofre do sistema.
    /// </remarks>
    private sealed class CredentialStoreDataStore : IDataStore
    {
        private readonly ICredentialStore _credentials;

        public CredentialStoreDataStore(ICredentialStore credentials) => _credentials = credentials;

        public Task StoreAsync<T>(string key, T value)
            => _credentials.SetSecretAsync(BuildKey(key), JsonSerializer.Serialize(value));

        public Task DeleteAsync<T>(string key) => _credentials.DeleteSecretAsync(BuildKey(key));

        public async Task<T> GetAsync<T>(string key)
        {
            var stored = await _credentials.GetSecretAsync(BuildKey(key)).ConfigureAwait(false);

            return string.IsNullOrEmpty(stored)
                ? default!
                : JsonSerializer.Deserialize<T>(stored)!;
        }

        public Task ClearAsync() => Task.CompletedTask;

        private static string BuildKey(string key) => CacheKey(key);
    }
}
