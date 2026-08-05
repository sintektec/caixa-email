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

    /// <summary>
    /// Escopo de leitura e escrita da agenda.
    /// </summary>
    /// <remarks>
    /// <b>Vai junto do de e-mail, diferente do Entra.</b> A Google emite um token só, com
    /// todos os escopos consentidos, e o mesmo token abre o Gmail e a Calendar API. Pedir os
    /// dois de uma vez é o caminho certo aqui — e é o oposto do que o Entra aceita.
    /// </remarks>
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar";

    private static readonly string[] AllScopes = [MailScope, CalendarScope];

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

    /// <summary>
    /// Se há Client ID <b>e</b> Client secret.
    /// </summary>
    /// <remarks>
    /// A Google exige os dois em cliente do tipo "Desktop app" — o <c>client_secret</c> é
    /// parâmetro obrigatório na troca do código e na renovação por <c>refresh_token</c>.
    /// Aceitar só o Client ID faria o assistente anunciar a conta Google como configurada, o
    /// navegador abrir, o usuário consentir, e a falha aparecer só na troca do código, com
    /// <c>invalid_request: client_secret is missing</c>. Falhar cedo e explicar é melhor.
    /// </remarks>
    public bool IsConfigured => _options.IsConfiguredWithSecret;

    /// <summary>
    /// As credenciais do aplicativo, como a biblioteca da Google as espera.
    /// </summary>
    /// <remarks>
    /// O <c>ClientSecret</c> nulo não produz exceção: a <c>Google.Apis.Auth</c> simplesmente
    /// omite o campo do corpo do pedido. O erro só aparece na resposta do servidor de token,
    /// depois de o usuário já ter consentido — daí <see cref="EnsureConfigured"/> barrar antes.
    /// </remarks>
    private ClientSecrets Credentials => new()
    {
        ClientId = _options.ClientId,
        ClientSecret = _options.ClientSecret,
    };

    /// <inheritdoc />
    public async Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            Credentials,
            AllScopes,
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
    public Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default)
        => GetAccessTokenAsync(emailAddress, AllScopes, cancellationToken);

    /// <summary>
    /// Devolve o token da conta.
    /// </summary>
    /// <remarks>
    /// Os escopos pedidos são ignorados de propósito: a Google já emitiu um token único com
    /// tudo o que o usuário consentiu, e pedir um subconjunto não produziria outro token —
    /// produziria outra ida ao consentimento, sem ganho nenhum.
    /// </remarks>
    public async Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = Credentials,
            Scopes = AllScopes,
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
                "A autenticação Google não está configurada: crie um cliente OAuth do tipo " +
                "\"Aplicativo para computador\" no Google Cloud Console e informe os dois valores que " +
                "ele emite, em OAuth:Google:ClientId e OAuth:Google:ClientSecret.");
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
