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

    /// <summary>Protocolo do servidor de agenda, quando houver um a testar.</summary>
    public CalendarProviderKind CalendarProvider { get; init; } = CalendarProviderKind.None;

    /// <summary>Endereço HTTPS do servidor de agenda.</summary>
    public string? CalendarUrl { get; init; }
}

/// <summary>Resultado do teste, separado por protocolo.</summary>
/// <param name="Imap">Resultado do IMAP.</param>
/// <param name="Smtp">Resultado do SMTP.</param>
/// <param name="Calendar">
/// Resultado do servidor de agenda, ou <see langword="null"/> quando não havia um a testar.
/// </param>
public readonly record struct TestAccountConnectionResult(
    ConnectionTestResult Imap, ConnectionTestResult Smtp, ConnectionTestResult? Calendar = null)
{
    /// <summary>
    /// Se o correio respondeu.
    /// </summary>
    /// <remarks>
    /// <b>A agenda não entra nesta conta.</b> Um servidor de calendário fora do ar não
    /// impede o cadastro de uma conta de e-mail que funciona: o erro é exibido, e o usuário
    /// decide. Bloquear aqui trocaria um recurso opcional por uma conta que não existe.
    /// </remarks>
    public bool Succeeded => Imap.Succeeded && Smtp.Succeeded;

    /// <summary>Primeira mensagem de erro encontrada, para exibição.</summary>
    public string? FirstError => Imap.Succeeded
        ? Smtp.Succeeded ? Calendar?.ErrorMessage : Smtp.ErrorMessage
        : Imap.ErrorMessage;
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
    private readonly IEnumerable<Abstractions.Calendar.ICalendarSyncProvider> _calendarProviders;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TestAccountConnectionHandler> _logger;

    public TestAccountConnectionHandler(
        IImapClient imapClient,
        ISmtpSender smtpSender,
        ICredentialStore credentials,
        IOAuthProviderRegistry oauthProviders,
        IEnumerable<Abstractions.Calendar.ICalendarSyncProvider> calendarProviders,
        TimeProvider timeProvider,
        ILogger<TestAccountConnectionHandler> logger)
    {
        _imapClient = imapClient;
        _smtpSender = smtpSender;
        _credentials = credentials;
        _oauthProviders = oauthProviders;
        _calendarProviders = calendarProviders;
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

            var calendar = await TestCalendarAsync(probe, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Teste de conexão para {ImapHost}/{SmtpHost}: IMAP {ImapOk}, SMTP {SmtpOk}.",
                command.ImapHost, command.SmtpHost, imap.Succeeded, smtp.Succeeded);

            return new TestAccountConnectionResult(imap, smtp, calendar);
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

    /// <summary>
    /// Testa o servidor de agenda, quando a configuração tem um.
    /// </summary>
    /// <remarks>
    /// Uma exceção aqui derrubaria o teste inteiro, inclusive o resultado do correio que já
    /// veio — e é o correio que decide se a conta pode ser cadastrada.
    /// </remarks>
    private async Task<ConnectionTestResult?> TestCalendarAsync(
        Account probe, CancellationToken cancellationToken)
    {
        if (probe.CalendarProvider == CalendarProviderKind.None)
        {
            return null;
        }

        var provider = _calendarProviders.FirstOrDefault(p => p.Provider == probe.CalendarProvider);

        if (provider is null)
        {
            return ConnectionTestResult.Failure(
                $"Não há suporte a {probe.CalendarProvider} nesta versão.");
        }

        try
        {
            return await provider.TestAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Failure(ex.Message);
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

        try
        {
            probe.ConfigureCalendar(
                command.CalendarProvider, command.CalendarUrl, syncEnabled: true, now);
        }
        catch (ArgumentException)
        {
            // Endereço fora de HTTPS. O teste do correio segue; o da agenda não acontece, e
            // é o CalendarUrlError da tela que explica o motivo enquanto se digita.
        }

        return probe;
    }
}
