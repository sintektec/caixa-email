namespace Sintek.Mail.Application.Ports;

/// <summary>
/// Secure credential storage. Never stores secrets in the database.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Stores a credential securely.</summary>
    Task SetCredentialAsync(string key, string secret, CancellationToken ct = default);

    /// <summary>Retrieves a credential.</summary>
    Task<string?> GetCredentialAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes a credential.</summary>
    Task DeleteCredentialAsync(string key, CancellationToken ct = default);

    /// <summary>Checks if a credential exists.</summary>
    Task<bool> HasCredentialAsync(string key, CancellationToken ct = default);
}
