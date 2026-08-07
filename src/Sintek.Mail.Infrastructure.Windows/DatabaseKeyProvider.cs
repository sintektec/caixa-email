using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Infrastructure.Windows;

/// <summary>
/// Fornece a chave de criptografia do banco local, criando-a na primeira execução.
/// </summary>
/// <remarks>
/// A chave é aleatória e guardada no Gerenciador de Credenciais do Windows. Deliberadamente
/// <b>não</b> é derivada de senha do usuário: derivá-la obrigaria a pedir a senha a cada
/// abertura do aplicativo — inviável para um cliente de e-mail que precisa sincronizar em
/// segundo plano — e uma senha fraca enfraqueceria a criptografia do banco inteiro.
/// </remarks>
public sealed class DatabaseKeyProvider : IDatabaseKeyProvider
{
    /// <summary>Identificador da chave no cofre do Windows.</summary>
    public const string CredentialKey = "Sintek.Mail:database-key";

    /// <summary>256 bits, o tamanho de chave usado pelo SQLCipher.</summary>
    private const int KeySizeInBytes = 32;

    private readonly ICredentialStore _credentials;
    private readonly ILogger<DatabaseKeyProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseKeyProvider(ICredentialStore credentials, ILogger<DatabaseKeyProvider> logger)
    {
        _credentials = credentials;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        // A exclusão mútua evita a corrida em que duas inicializações concorrentes geram
        // chaves diferentes: a segunda sobrescreveria a primeira e o banco já criado
        // ficaria inacessível para sempre.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _credentials.GetSecretAsync(CredentialKey, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeInBytes));
            await _credentials.SetSecretAsync(CredentialKey, key, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Chave do banco local criada e guardada no cofre do Windows.");
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Registro dos adaptadores exclusivos do Windows.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra o cofre de credenciais e o provedor da chave do banco.
    /// </summary>
    public static IServiceCollection AddSintekMailWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IDatabaseKeyProvider, DatabaseKeyProvider>();

        return services;
    }
}
