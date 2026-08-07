using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Accounts;

/// <summary>Pedido de alteração de uma conta já cadastrada.</summary>
/// <remarks>
/// O endereço de e-mail <b>não</b> pode ser alterado. Ele determina a qual Diretório de
/// Domínio a conta pertence e é a identidade de tudo que já foi sincronizado; trocá-lo seria
/// outra conta, e o caminho honesto para isso é remover esta e cadastrar a nova.
/// </remarks>
public sealed record UpdateAccountCommand
{
    /// <summary>Conta a alterar.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Nome exibido nas mensagens enviadas.</summary>
    public required string DisplayName { get; init; }

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

    /// <summary>Nome de usuário, quando difere do endereço.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Nova senha. Quando ausente, a senha guardada é preservada.
    /// </summary>
    /// <remarks>
    /// A distinção importa: um campo de senha vazio na tela de edição significa "não mexer",
    /// e não "apagar a senha". Tratá-lo como apagamento deixaria a conta sem credencial
    /// depois de uma simples correção de porta.
    /// </remarks>
    public string? NewPassword { get; init; }

    /// <summary>Intervalo entre sincronizações automáticas, em minutos.</summary>
    public int SyncIntervalMinutes { get; init; } = 5;

    /// <summary>Quanto de cada mensagem baixar.</summary>
    public BodyDownloadPolicy BodyDownloadPolicy { get; init; } = BodyDownloadPolicy.RecentOnly;

    /// <summary>Se a conta permanece ativa.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Se a conexão deve ser testada antes de gravar.
    /// </summary>
    /// <remarks>
    /// Padrão verdadeiro: alterar servidor ou senha sem testar é a forma mais fácil de
    /// deixar a conta inutilizável e só descobrir na próxima sincronização.
    /// </remarks>
    public bool TestBeforeSaving { get; init; } = true;
}

/// <summary>Resultado da alteração.</summary>
/// <param name="Succeeded">Se a alteração foi aplicada.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
/// <param name="IsAuthenticationFailure">Se a recusa veio de credencial rejeitada.</param>
public readonly record struct UpdateAccountResult(
    bool Succeeded, string? ErrorMessage, bool IsAuthenticationFailure);

/// <summary>
/// Altera a configuração de uma conta existente.
/// </summary>
/// <remarks>
/// <b>Testa antes de alterar, nunca o contrário.</b> Mexer na entidade e desfazer em caso de
/// falha parece equivalente e não é: a entidade fica rastreada pelo contexto, e qualquer
/// gravação posterior — de outra operação, na mesma unidade de trabalho — levaria junto as
/// alterações que deveriam ter sido descartadas. O mesmo vale para a senha, que só chega ao
/// cofre depois de o servidor aceitá-la.
/// </remarks>
public sealed class UpdateAccountHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICredentialStore _credentials;
    private readonly TestAccountConnectionHandler _connectionTest;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateAccountHandler> _logger;

    public UpdateAccountHandler(
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        ICredentialStore credentials,
        TestAccountConnectionHandler connectionTest,
        TimeProvider timeProvider,
        ILogger<UpdateAccountHandler> logger)
    {
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _credentials = credentials;
        _connectionTest = connectionTest;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa a alteração.</summary>
    public async Task<UpdateAccountResult> HandleAsync(
        UpdateAccountCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await _accounts.GetByIdAsync(command.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new UpdateAccountResult(false, "A conta informada não existe.", false);
        }

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            return new UpdateAccountResult(false, "Informe o nome exibido da conta.", false);
        }

        var usesPassword = account.AuthenticationType == AuthenticationType.Password;

        if (command.TestBeforeSaving && command.IsActive)
        {
            var password = usesPassword
                ? command.NewPassword ?? await _credentials
                    .GetSecretAsync(account.CredentialKey, cancellationToken).ConfigureAwait(false)
                : null;

            var test = await _connectionTest.HandleAsync(
                new TestAccountConnectionCommand
                {
                    EmailAddress = account.EmailAddress.Value,
                    ImapHost = command.ImapHost,
                    ImapPort = command.ImapPort,
                    ImapSecurity = command.ImapSecurity,
                    SmtpHost = command.SmtpHost,
                    SmtpPort = command.SmtpPort,
                    SmtpSecurity = command.SmtpSecurity,
                    AuthenticationType = account.AuthenticationType,
                    OAuthProvider = account.OAuthProvider,
                    UserName = command.UserName,
                    Password = password,
                },
                cancellationToken).ConfigureAwait(false);

            if (!test.Succeeded)
            {
                return new UpdateAccountResult(
                    false,
                    test.FirstError,
                    test.Imap.IsAuthenticationFailure || test.Smtp.IsAuthenticationFailure);
            }
        }

        var now = _timeProvider.GetUtcNow();

        account.Rename(command.DisplayName, now);
        account.ConfigureServers(
            command.ImapHost,
            command.ImapPort,
            command.ImapSecurity,
            command.SmtpHost,
            command.SmtpPort,
            command.SmtpSecurity,
            now);

        if (usesPassword)
        {
            account.UsePasswordAuthentication(command.UserName, now);

            if (!string.IsNullOrEmpty(command.NewPassword))
            {
                await _credentials
                    .SetSecretAsync(account.CredentialKey, command.NewPassword, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        account.ConfigureSync(command.SyncIntervalMinutes, command.BodyDownloadPolicy, now);

        // Reconfigurar é o pedido de nova tentativa, e sem isto ele não chegava a lugar
        // nenhum. O agendador pula indefinidamente a conta com credencial recusada — por bom
        // motivo, insistir só rende bloqueio no provedor —, e contava com a reautenticação
        // para trazê-la de volta. Ninguém executava essa volta: corrigir a senha deixava a
        // conta tão parada quanto antes, agora com a credencial certa (D-040).
        account.ResumeSync(now);

        // Depois de retomar, nunca antes: desativar a conta define o estado como Disabled, e
        // é essa a palavra final. Na ordem inversa, salvar uma conta desativada a marcaria
        // como "nunca sincronizada" e ela voltaria à fila do agendador.
        account.SetActive(command.IsActive, now);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Conta {AccountId} alterada.", account.Id);

        return new UpdateAccountResult(true, null, false);
    }
}
