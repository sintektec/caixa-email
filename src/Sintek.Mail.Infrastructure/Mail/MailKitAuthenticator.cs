using System.Net.Sockets;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>
/// Conecta e autentica clientes MailKit, escolhendo entre senha e OAuth 2.0.
/// </summary>
/// <remarks>
/// Concentrar isto em um lugar evita que IMAP e SMTP divirjam no tratamento de
/// autenticação — divergência que produziria o sintoma clássico de "recebo mas não
/// consigo enviar".
/// </remarks>
public sealed class MailKitAuthenticator
{
    private readonly ICredentialStore _credentials;
    private readonly IOAuthProviderRegistry _oauthProviders;

    public MailKitAuthenticator(ICredentialStore credentials, IOAuthProviderRegistry oauthProviders)
    {
        _credentials = credentials;
        _oauthProviders = oauthProviders;
    }

    /// <summary>Traduz o modo de proteção do domínio para a opção do MailKit.</summary>
    public static SecureSocketOptions ToSecureSocketOptions(SecureSocketMode mode) => mode switch
    {
        SecureSocketMode.None => SecureSocketOptions.None,
        SecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecureSocketMode.StartTls => SecureSocketOptions.StartTls,
        SecureSocketMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        SecureSocketMode.Auto => SecureSocketOptions.Auto,
        _ => SecureSocketOptions.Auto,
    };

    /// <summary>Conecta e autentica um cliente IMAP.</summary>
    public async Task<ConnectionTestResult> ConnectImapAsync(
        ImapClient client, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);

        return await ConnectAsync(
            () => client.ConnectAsync(
                account.ImapHost,
                account.ImapPort,
                ToSecureSocketOptions(account.ImapSecurity),
                cancellationToken),
            mechanism => client.AuthenticateAsync(mechanism, cancellationToken),
            (user, password) => client.AuthenticateAsync(user, password, cancellationToken),
            account,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Conecta e autentica um cliente SMTP.</summary>
    public async Task<ConnectionTestResult> ConnectSmtpAsync(
        SmtpClient client, Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);

        return await ConnectAsync(
            () => client.ConnectAsync(
                account.SmtpHost,
                account.SmtpPort,
                ToSecureSocketOptions(account.SmtpSecurity),
                cancellationToken),
            mechanism => client.AuthenticateAsync(mechanism, cancellationToken),
            (user, password) => client.AuthenticateAsync(user, password, cancellationToken),
            account,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionTestResult> ConnectAsync(
        Func<Task> connect,
        Func<SaslMechanism, Task> authenticateWithMechanism,
        Func<string, string, Task> authenticateWithPassword,
        Account account,
        CancellationToken cancellationToken)
    {
        try
        {
            await connect().ConfigureAwait(false);

            var userName = account.UserName ?? account.EmailAddress.Value;

            if (account.AuthenticationType == AuthenticationType.OAuth2)
            {
                var provider = _oauthProviders.Resolve(account.OAuthProvider)
                    ?? throw new InvalidOperationException(
                        $"Não há provedor OAuth registrado para {account.OAuthProvider}.");

                var token = await provider
                    .GetAccessTokenAsync(account.EmailAddress.Value, cancellationToken)
                    .ConfigureAwait(false);

                await authenticateWithMechanism(new SaslMechanismOAuth2(userName, token.AccessToken))
                    .ConfigureAwait(false);
            }
            else
            {
                var password = await _credentials
                    .GetSecretAsync(account.CredentialKey, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(password))
                {
                    return ConnectionTestResult.AuthenticationFailure(
                        "A senha desta conta não foi encontrada no Gerenciador de Credenciais do Windows. " +
                        "Informe a senha novamente nas configurações da conta.");
                }

                await authenticateWithPassword(userName, password).ConfigureAwait(false);
            }

            return ConnectionTestResult.Success();
        }
        catch (AuthenticationException ex)
        {
            // Credencial recusada: a ação do usuário é reautenticar, não tentar de novo.
            return ConnectionTestResult.AuthenticationFailure(
                $"Não foi possível autenticar em {account.EmailAddress.Value}: {ex.Message}");
        }
        catch (ReauthenticationRequiredException)
        {
            return ConnectionTestResult.AuthenticationFailure(
                $"O acesso autorizado para {account.EmailAddress.Value} expirou. Entre novamente na conta.");
        }
        catch (SslHandshakeException ex)
        {
            return ConnectionTestResult.Failure(
                $"Falha na negociação TLS com o servidor: {ex.Message}. " +
                "Verifique a porta e o modo de segurança configurados.");
        }
        catch (TimeoutException)
        {
            // O MailKit tem teto próprio de 120 segundos e o sinaliza com TimeoutException,
            // que não é IOException nem SocketException. Sem esta captura ela escapa daqui,
            // sobe até o manipulador `async void` do diálogo e derruba a aplicação — uma
            // porta errada não pode fechar o programa.
            return ConnectionTestResult.Failure(
                "O servidor aceitou a conexão e não respondeu. Isso costuma ser porta " +
                "filtrada por firewall, ou o modo de proteção trocado — SSL direto numa " +
                "porta que espera STARTTLS, ou o contrário.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or SmtpProtocolException)
        {
            return ConnectionTestResult.Failure(
                $"Não foi possível conectar ao servidor: {ex.Message}");
        }
    }
}
