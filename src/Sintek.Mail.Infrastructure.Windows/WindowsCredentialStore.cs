using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Security;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Sintek.Mail.Infrastructure.Windows;

/// <summary>
/// Cofre de segredos sobre o Gerenciador de Credenciais do Windows.
/// </summary>
/// <remarks>
/// <para>
/// Usamos <c>CredRead</c>/<c>CredWrite</c> em vez de
/// <c>Windows.Security.Credentials.PasswordVault</c> porque o PasswordVault exige
/// identidade de pacote (MSIX) e a aplicação também roda no modo unpackaged — a decisão
/// de empacotamento dual tornaria o PasswordVault inutilizável em metade dos cenários.
/// </para>
/// <para>
/// Os segredos são gravados com persistência <c>LocalMachine</c> escopada ao usuário
/// corrente: o Windows os protege com a chave de logon, de modo que outro usuário da
/// mesma máquina não os lê.
/// </para>
/// </remarks>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly ILogger<WindowsCredentialStore> _logger;

    public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger) => _logger = logger;

    /// <inheritdoc />
    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();

        // O Windows exige UTF-16 no blob de credencial; gravar UTF-8 devolveria texto
        // ilegível ao ser lido pelo painel de credenciais do sistema.
        var blob = Encoding.Unicode.GetBytes(secret);

        // Limite rígido do CredWrite. Um token OAuth grande pode se aproximar dele, e a
        // falha nativa não explicaria a causa.
        if (blob.Length > 2560)
        {
            throw new ArgumentException(
                "O segredo excede o limite de 2560 bytes do Gerenciador de Credenciais do Windows.",
                nameof(secret));
        }

        unsafe
        {
            fixed (byte* blobPointer = blob)
            fixed (char* targetPointer = key)
            {
                var credential = new CREDENTIALW
                {
                    Type = CRED_TYPE.CRED_TYPE_GENERIC,
                    TargetName = targetPointer,
                    CredentialBlob = blobPointer,
                    CredentialBlobSize = (uint)blob.Length,
                    Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
                };

                if (!PInvoke.CredWrite(credential, 0))
                {
                    throw new InvalidOperationException(
                        $"Falha ao gravar a credencial no Windows (código {Marshal.GetLastWin32Error()}).");
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        unsafe
        {
            CREDENTIALW* credential = null;

            try
            {
                if (!PInvoke.CredRead(key, CRED_TYPE.CRED_TYPE_GENERIC, out credential))
                {
                    var error = Marshal.GetLastWin32Error();

                    // Credencial ausente é resultado normal — a conta pode ainda não ter
                    // sido configurada. Só os demais códigos são falha de verdade.
                    if (error == (int)WIN32_ERROR.ERROR_NOT_FOUND)
                    {
                        return Task.FromResult<string?>(null);
                    }

                    _logger.LogWarning("Falha ao ler credencial do Windows (código {Error}).", error);
                    return Task.FromResult<string?>(null);
                }

                if (credential is null || credential->CredentialBlobSize == 0)
                {
                    return Task.FromResult<string?>(null);
                }

                var secret = Encoding.Unicode.GetString(
                    credential->CredentialBlob, (int)credential->CredentialBlobSize);

                return Task.FromResult<string?>(secret);
            }
            finally
            {
                if (credential is not null)
                {
                    PInvoke.CredFree(credential);
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        // CredDelete não ganha a sobrecarga amigável que o CsWin32 gera para CredRead:
        // é preciso montar o PCWSTR à mão e passar o CRED_TYPE tipado.
        bool deleted;

        unsafe
        {
            fixed (char* targetPointer = key)
            {
                deleted = PInvoke.CredDelete(new PCWSTR(targetPointer), CRED_TYPE.CRED_TYPE_GENERIC, 0);
            }
        }

        return Task.FromResult(deleted);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => await GetSecretAsync(key, cancellationToken).ConfigureAwait(false) is not null;
}
