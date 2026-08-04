using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Ports;

/// <summary>
/// OAuth 2.0 authentication provider.
/// </summary>
public interface IOAuthProvider
{
    /// <summary>Gets the provider type.</summary>
    Domain.Enums.OAuthProvider ProviderType { get; }

    /// <summary>Initiates the OAuth flow and returns an access token.</summary>
    Task<string> AuthenticateAsync(Account account, CancellationToken ct = default);

    /// <summary>Refreshes an expired access token.</summary>
    Task<string> RefreshTokenAsync(Account account, string refreshToken, CancellationToken ct = default);

    /// <summary>Revokes a token.</summary>
    Task RevokeTokenAsync(Account account, string token, CancellationToken ct = default);

    /// <summary>Validates a token without refreshing.</summary>
    Task<bool> ValidateTokenAsync(Account account, string token, CancellationToken ct = default);
}
