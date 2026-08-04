using System.Runtime.InteropServices;
using System.Text;
using Sintek.Mail.Application.Ports;

namespace Sintek.Mail.Infrastructure.Windows;

/// <summary>
/// Windows Credential Manager implementation using P/Invoke.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    private const string CredentialPrefix = "Sintek.Mail:";

    public Task SetCredentialAsync(string key, string secret, CancellationToken ct = default)
    {
        var credential = new NativeMethods.CREDENTIAL
        {
            Type = NativeMethods.CRED_TYPE_GENERIC,
            TargetName = CredentialPrefix + key,
            CredentialBlob = Marshal.StringToCoTaskMemUni(secret),
            CredentialBlobSize = (uint)(secret.Length * 2),
            Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            UserName = key
        };

        if (!NativeMethods.CredWrite(ref credential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to write credential. Error: {error}");
        }

        Marshal.FreeCoTaskMem(credential.CredentialBlob);
        return Task.CompletedTask;
    }

    public Task<string?> GetCredentialAsync(string key, CancellationToken ct = default)
    {
        if (!NativeMethods.CredRead(CredentialPrefix + key, NativeMethods.CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
            var secret = Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return Task.FromResult(secret);
        }
        finally
        {
            NativeMethods.CredFree(credentialPtr);
        }
    }

    public Task DeleteCredentialAsync(string key, CancellationToken ct = default)
    {
        if (!NativeMethods.CredDelete(CredentialPrefix + key, NativeMethods.CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_NOT_FOUND)
            {
                throw new InvalidOperationException($"Failed to delete credential. Error: {error}");
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> HasCredentialAsync(string key, CancellationToken ct = default)
    {
        var exists = NativeMethods.CredRead(CredentialPrefix + key, NativeMethods.CRED_TYPE_GENERIC, 0, out var credentialPtr);
        if (exists)
        {
            NativeMethods.CredFree(credentialPtr);
        }
        return Task.FromResult(exists);
    }

    private static class NativeMethods
    {
        public const int CRED_TYPE_GENERIC = 1;
        public const int CRED_PERSIST_LOCAL_MACHINE = 2;
        public const int ERROR_NOT_FOUND = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern void CredFree(IntPtr buffer);
    }
}
