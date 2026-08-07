using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Services;

/// <summary>
/// Descarta as credenciais de uma conta ao removê-la.
/// </summary>
/// <remarks>
/// <para>
/// Existe como serviço, e não dentro de um caso de uso, porque duas operações precisam
/// dele: remover uma conta e remover o Diretório de Domínio que a contém. Duplicar a
/// limpeza faria uma das duas esquecer alguma coisa, e o que fica esquecido aqui é
/// credencial de acesso a e-mail.
/// </para>
/// <para>
/// <b>Nenhuma falha aqui interrompe a remoção.</b> Revogar o consentimento OAuth depende de
/// rede; se ela estiver fora, a conta ainda assim precisa sumir da máquina. O que não pode
/// falhar em silêncio é a exclusão do segredo local, e por isso ela é registrada em log
/// quando não acontece.
/// </para>
/// </remarks>
public sealed class AccountCredentialRevoker
{
    private readonly ICredentialStore _credentials;
    private readonly IOAuthProviderRegistry _oauthProviders;
    private readonly ILogger<AccountCredentialRevoker> _logger;

    public AccountCredentialRevoker(
        ICredentialStore credentials,
        IOAuthProviderRegistry oauthProviders,
        ILogger<AccountCredentialRevoker> logger)
    {
        _credentials = credentials;
        _oauthProviders = oauthProviders;
        _logger = logger;
    }

    /// <summary>Revoga o consentimento OAuth, quando houver, e apaga o segredo local.</summary>
    public async Task RevokeAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.AuthenticationType == AuthenticationType.OAuth2)
        {
            var provider = _oauthProviders.Resolve(account.OAuthProvider);

            if (provider is not null)
            {
                try
                {
                    await provider.SignOutAsync(account.EmailAddress.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // O consentimento continua registrado no provedor; o usuário pode
                    // revogá-lo pelo painel dele. Travar a remoção local por causa disso
                    // deixaria a conta em um estado pior: presente e inutilizável.
                    _logger.LogWarning(
                        ex,
                        "Não foi possível revogar o consentimento OAuth da conta {AccountId}. " +
                        "A remoção local prosseguiu.",
                        account.Id);
                }
            }
        }

        var deleted = await _credentials.DeleteSecretAsync(account.CredentialKey, cancellationToken)
            .ConfigureAwait(false);

        if (!deleted)
        {
            _logger.LogWarning(
                "O segredo da conta {AccountId} não foi encontrado no cofre durante a remoção.",
                account.Id);
        }
    }
}
