using Microsoft.Identity.Client;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.OAuth;

public sealed class MsalOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string[] _scopes = { "https://outlook.office.com/IMAP.AccessAsUser.All", "https://outlook.office.com/SMTP.Send", "offline_access" };

    public OAuthProvider ProviderType => OAuthProvider.Microsoft365;

    public MsalOAuthProvider(string clientId, string tenantId = "common")
    {
        _clientId = clientId;
        _tenantId = tenantId;
    }

    public async Task<string> AuthenticateAsync(Account account, CancellationToken ct = default)
    {
        var app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var result = await app.AcquireTokenInteractive(_scopes)
            .WithLoginHint(account.EmailAddress)
            .ExecuteAsync(ct);

        return result.AccessToken;
    }

    public async Task<string> RefreshTokenAsync(Account account, string refreshToken, CancellationToken ct = default)
    {
        var app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var accounts = await app.GetAccountsAsync();
        var firstAccount = accounts.FirstOrDefault();

        if (firstAccount is null)
            throw new InvalidOperationException("No cached account found for token refresh.");

        var result = await app.AcquireTokenSilent(_scopes, firstAccount).ExecuteAsync(ct);
        return result.AccessToken;
    }

    public async Task RevokeTokenAsync(Account account, string token, CancellationToken ct = default)
    {
        var app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var accounts = await app.GetAccountsAsync();
        var firstAccount = accounts.FirstOrDefault();

        if (firstAccount is not null)
        {
            await app.RemoveAsync(firstAccount);
        }
    }

    public async Task<bool> ValidateTokenAsync(Account account, string token, CancellationToken ct = default)
    {
        try
        {
            var app = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
                .WithRedirectUri("http://localhost")
                .Build();

            var accounts = await app.GetAccountsAsync();
            return accounts.Any();
        }
        catch
        {
            return false;
        }
    }
}
