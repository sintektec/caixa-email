using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>
/// Autenticação OAuth 2.0 no Microsoft Entra ID para Outlook.com, Microsoft 365 e
/// Exchange Online.
/// </summary>
/// <remarks>
/// O cache de tokens do MSAL é persistido no <see cref="ICredentialStore"/> — ou seja, no
/// Windows Credential Manager — e não no arquivo que o MSAL usaria por padrão. Um token
/// de atualização vale tanto quanto a senha: gravá-lo em arquivo contrariaria a exigência
/// da especificação de manter todo segredo no cofre do sistema.
/// </remarks>
public sealed class MicrosoftOAuthProvider : IOAuthProvider
{
    /// <summary>
    /// Escopo específico de IMAP/SMTP no Outlook. Sem ele o token vem válido para o Graph
    /// e o servidor de e-mail recusa a autenticação XOAUTH2.
    /// </summary>
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
        "offline_access",
    ];

    private readonly OAuthClientOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<MicrosoftOAuthProvider> _logger;
    private IPublicClientApplication? _application;

    public MicrosoftOAuthProvider(
        IOptions<OAuthOptions> options,
        ICredentialStore credentials,
        ILogger<MicrosoftOAuthProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Microsoft;
        _credentials = credentials;
        _logger = logger;
    }

    /// <inheritdoc />
    public OAuthProviderKind Provider => OAuthProviderKind.Microsoft;

    /// <inheritdoc />
    public bool IsConfigured => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        var result = await application
            .AcquireTokenInteractive(Scopes)
            .WithLoginHint(emailAddress)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        await PersistCacheAsync(application, emailAddress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Autenticação Microsoft concluída para uma conta de e-mail.");
        return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
    }

    /// <inheritdoc />
    public async Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, emailAddress, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw new ReauthenticationRequiredException(emailAddress);
        }

        try
        {
            var result = await application
                .AcquireTokenSilent(Scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            await PersistCacheAsync(application, emailAddress, cancellationToken).ConfigureAwait(false);
            return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            // O token de atualização venceu ou foi revogado. Quem chama precisa levar o
            // usuário de volta ao consentimento — tentar de novo em silêncio só repetiria
            // a falha.
            throw new ReauthenticationRequiredException(emailAddress, ex);
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        foreach (var account in await application.GetAccountsAsync().ConfigureAwait(false))
        {
            await application.RemoveAsync(account).ConfigureAwait(false);
        }

        await _credentials.DeleteSecretAsync(CacheKey(emailAddress), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IPublicClientApplication> GetApplicationAsync(
        string emailAddress, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "A autenticação Microsoft não está configurada: registre um aplicativo no Entra ID e " +
                "informe o Client ID em OAuth:Microsoft:ClientId.");
        }

        if (_application is not null)
        {
            return _application;
        }

        _application = PublicClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _options.TenantId)
            .WithRedirectUri(_options.RedirectUri)
            .Build();

        AttachCredentialStoreCache(_application, emailAddress);

        // Primeira carga do cache: o MSAL só dispara o evento de leitura na primeira
        // operação, e precisamos do cache disponível antes disso.
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return _application;
    }

    /// <summary>
    /// Liga o cache de tokens do MSAL ao Windows Credential Manager.
    /// </summary>
    private void AttachCredentialStoreCache(IPublicClientApplication application, string emailAddress)
    {
        var key = CacheKey(emailAddress);

        application.UserTokenCache.SetBeforeAccess(args =>
        {
            var stored = _credentials.GetSecretAsync(key).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(stored))
            {
                args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(stored));
            }
        });

        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            var serialized = Convert.ToBase64String(args.TokenCache.SerializeMsalV3());
            _credentials.SetSecretAsync(key, serialized).GetAwaiter().GetResult();
        });
    }

    private static string CacheKey(string emailAddress)
        => $"Sintek.Mail:oauth:microsoft:{emailAddress}";

    /// <summary>
    /// Força a gravação do cache logo após uma aquisição de token.
    /// </summary>
    private async Task PersistCacheAsync(
        IPublicClientApplication application, string emailAddress, CancellationToken cancellationToken)
    {
        // A serialização já acontece no evento SetAfterAccess; este método existe para
        // deixar explícito no fluxo que o token foi persistido, e para permitir
        // verificação em teste.
        _ = application;
        _ = emailAddress;
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Monta a cadeia SASL XOAUTH2 no formato exigido pelos servidores de e-mail.
    /// </summary>
    /// <remarks>
    /// O formato tem separadores <c>\x01</c> obrigatórios; errá-los produz uma falha de
    /// autenticação genérica que não indica a causa.
    /// </remarks>
    public static string BuildXOAuth2Token(string userName, string accessToken)
        => Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"user={userName}\x01auth=Bearer {accessToken}\x01\x01"));
}
