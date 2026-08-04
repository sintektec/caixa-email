using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Accounts;

/// <summary>Configuração a testar, antes de qualquer coisa ser gravada.</summary>
public sealed record TestAccountConnectionCommand
{
    /// <summary>Endereço da conta.</summary>
    public required string EmailAddress { get; init; }

    /// <summary>Servidor IMAP.</summary>
    public required string ImapHost { get; init; }

    /// <summary>Porta IMAP.</summary>
    public int ImapPort { get; init; } = 993;

    /// <summary>Modo de proteção do IMAP.</summary>
    public SecureSocketMode ImapSecurity { get; init; } = SecureSocketMode.SslOnConnect;

    /// <summary>Servidor SMTP.</summary>
    public required string SmtpHost { get; init; }

    /// <summary>Porta SMTP.</summary>
    public int SmtpPort { get; init; } = 587;

    /// <summary>Modo de proteção do SMTP.</summary>
    public SecureSocketMode SmtpSecurity { get; init; } = SecureSocketMode.StartTls;

    /// <summary>Como autenticar.</summary>
    public AuthenticationType AuthenticationType { get; init; } = AuthenticationType.Password;

    /// <summary>Provedor de identidade, quando OAuth.</summary>
    public OAuthProviderKind OAuthProvider { get; init; } = OAuthProviderKind.None;

    /// <summary>Nome de usuário, quando difere do endereço.</summary>
    public string? UserName { get; init; }

    /// <summary>Senha, quando a autenticação é por senha.</summary>
    /// <remarks>Vive apenas nesta instância; nunca é gravada por este caso de uso.</remarks>
    public string? Password { get; init; }
}

/// <summary>Resultado do teste, separado por protocolo.</summary>
/// <param name="Imap">Resultado do IMAP.</param>
/// <param name="Smtp">Resultado do SMTP.</param>
public readonly record struct TestAccountConnectionResult(
    ConnectionTestResult Imap, ConnectionTestResult Smtp)
{
    /// <summary>Se os dois protocolos responderam.</summary>
    public bool Succeeded => Imap.Succeeded && Smtp.Succeeded;

    /// <summary>Primeira mensagem de erro encontrada, para exibição.</summary>
    public string? FirstError => Imap.Succeeded ? Smtp.ErrorMessage : Imap.ErrorMessage;
}

/// <summary>
/// Testa uma configuração de conta sem gravá-la.
/// </summary>
/// <remarks>
/// <para>
/// A especificação exige validar as credenciais antes de concluir o cadastro, e o
/// assistente precisa poder testar quantas vezes o usuário quiser enquanto corrige host,
/// porta e senha. Daí um caso de uso próprio: cadastrar e testar são coisas diferentes.
/// </para>
/// <para>
/// <b>Os dois protocolos são testados.</b> Só o IMAP passar produz a falha mais frustrante
/// que existe em cliente de e-mail: recebe mas não envia, e a descoberta acontece na hora
/// em que o usuário precisa responder alguma coisa.
/// </para>
/// <para>
/// Quando a autenticação é por senha, o segredo é gravado no cofre sob uma chave temporária
/// e apagado ao final — o cliente IMAP lê a credencial de lá, e é o único jeito de testar
/// sem que a senha trafegue por outros caminhos do sistema.
/// </para>
/// </remarks>
public sealed class TestAccountConnectionHandler
{
    private readonly IImapClient _imapClient;
    private readonly ISmtpSender _smtpSender;
    private readonly ICredentialStore _credentials;
    private readonly IOAuthProviderRegistry _oauthProviders;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TestAccountConnectionHandler> _logger;

    public TestAccountConnectionHandler(
        IImapClient imapClient,
        ISmtpSender smtpSender,
        ICredentialStore credentials,
        IOAuthProviderRegistry oauthProviders,
        TimeProvider timeProvider,
        ILogger<TestAccountConnectionHandler> logger)
    {
        _imapClient = imapClient;
        _smtpSender = smtpSender;
        _credentials = credentials;
        _oauthProviders = oauthProviders;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa o teste.</summary>
    public async Task<TestAccountConnectionResult> HandleAsync(
        TestAccountConnectionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EmailAddress.TryParse(command.EmailAddress, out var address, out var parseError))
        {
            var failure = ConnectionTestResult.Failure(parseError!);
            return new TestAccountConnectionResult(failure, failure);
        }

        if (command.AuthenticationType == AuthenticationType.OAuth2)
        {
            var unavailable = CheckOAuthAvailability(command.OAuthProvider);
            if (unavailable is not null)
            {
                return new TestAccountConnectionResult(unavailable.Value, unavailable.Value);
            }
        }
        else if (string.IsNullOrEmpty(command.Password))
        {
            var missing = ConnectionTestResult.AuthenticationFailure("Informe a senha da conta.");
            return new TestAccountConnectionResult(missing, missing);
        }

        var probe = BuildProbeAccount(command, address);
        var storedSecret = false;

        try
        {
            if (command.AuthenticationType == AuthenticationType.Password)
            {
                await _credentials
                    .SetSecretAsync(probe.CredentialKey, command.Password!, cancellationToken)
                    .ConfigureAwait(false);

                storedSecret = true;
            }

            var imap = await _imapClient.ConnectAsync(probe, cancellationToken).ConfigureAwait(false);

            // O SMTP é testado mesmo com o IMAP falhando: host errado nos dois é comum, e
            // mostrar os dois erros de uma vez poupa uma rodada inteira de tentativa e erro.
            var smtp = await _smtpSender.TestConnectionAsync(probe, cancellationToken).ConfigureAwait(false);

            if (imap.Succeeded)
            {
                await _imapClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Teste de conexão para {ImapHost}/{SmtpHost}: IMAP {ImapOk}, SMTP {SmtpOk}.",
                command.ImapHost, command.SmtpHost, imap.Succeeded, smtp.Succeeded);

            return new TestAccountConnectionResult(imap, smtp);
        }
        finally
        {
            if (storedSecret)
            {
                // A chave é temporária: deixá-la para trás povoaria o Gerenciador de
                // Credenciais com senhas de tentativas que o usuário abandonou.
                await _credentials.DeleteSecretAsync(probe.CredentialKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private ConnectionTestResult? CheckOAuthAvailability(OAuthProviderKind kind)
    {
        var provider = _oauthProviders.Resolve(kind);

        if (provider is null)
        {
            return ConnectionTestResult.Failure(
                $"Não há suporte a {kind} nesta versão.");
        }

        if (!provider.IsConfigured)
        {
            // Explicar que falta configuração é diferente de falhar na autenticação: a
            // ação necessária é do administrador, não do usuário.
            return ConnectionTestResult.Failure(
                $"A autenticação {kind} ainda não foi configurada nesta instalação. " +
                "É preciso registrar o aplicativo no provedor e informar o Client ID.");
        }

        return null;
    }

    /// <summary>
    /// Monta uma conta desanexada, só para o teste.
    /// </summary>
    /// <remarks>
    /// <see cref="Account.CreateProbe"/> dá a ela uma chave de credencial própria, que não
    /// colide com a de uma conta real do mesmo endereço já cadastrada — colidir apagaria a
    /// senha em uso ao final do teste.
    /// </remarks>
    private Account BuildProbeAccount(TestAccountConnectionCommand command, EmailAddress address)
    {
        var now = _timeProvider.GetUtcNow();
        var probe = Account.CreateProbe(address, now);

        probe.ConfigureServers(
            command.ImapHost,
            command.ImapPort,
            command.ImapSecurity,
            command.SmtpHost,
            command.SmtpPort,
            command.SmtpSecurity,
            now);

        if (command.AuthenticationType == AuthenticationType.OAuth2)
        {
            probe.UseOAuthAuthentication(command.OAuthProvider, now);
        }
        else
        {
            probe.UsePasswordAuthentication(command.UserName, now);
        }

        return probe;
    }
}
