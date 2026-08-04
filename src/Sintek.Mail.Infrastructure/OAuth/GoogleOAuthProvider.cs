using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.OAuth;

public sealed class GoogleOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string[] _scopes = { "https://mail.google.com/" };

    public OAuthProvider ProviderType => OAuthProvider.Google;

    public GoogleOAuthProvider(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task<string> AuthenticateAsync(Account account, CancellationToken ct = default)
    {
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
            _scopes,
            account.EmailAddress,
            ct,
            new FileDataStore("Sintek.Mail.Google", true)
        );

        return credential.Token.AccessToken;
    }

    public async Task<string> RefreshTokenAsync(Account account, string refreshToken, CancellationToken ct = default)
    {
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
            _scopes,
            account.EmailAddress,
            ct,
            new FileDataStore("Sintek.Mail.Google", true)
        );

        if (credential.Token.IsStale)
        {
            await credential.RefreshTokenAsync(ct);
        }

        return credential.Token.AccessToken;
    }

    public async Task RevokeTokenAsync(Account account, string token, CancellationToken ct = default)
    {
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
            _scopes,
            account.EmailAddress,
            ct,
            new FileDataStore("Sintek.Mail.Google", true)
        );

        await credential.RevokeTokenAsync(ct);
    }

    public async Task<bool> ValidateTokenAsync(Account account, string token, CancellationToken ct = default)
    {
        try
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
                _scopes,
                account.EmailAddress,
                ct,
                new FileDataStore("Sintek.Mail.Google", true)
            );

            return !credential.Token.IsStale;
        }
        catch
        {
            return false;
        }
    }
}
