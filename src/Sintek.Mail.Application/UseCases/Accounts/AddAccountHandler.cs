using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Accounts;

/// <summary>Pedido para cadastrar uma conta em um Diretório de Domínio.</summary>
public sealed record AddAccountCommand
{
    /// <summary>Diretório ao qual a conta será vinculada.</summary>
    public required Guid DomainDirectoryId { get; init; }

    /// <summary>Endereço da conta.</summary>
    public required string EmailAddress { get; init; }

    /// <summary>Nome exibido nas mensagens enviadas.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Servidor IMAP. Quando ausente, a descoberta automática é tentada.</summary>
    public string? ImapHost { get; init; }

    /// <summary>Porta IMAP.</summary>
    public int? ImapPort { get; init; }

    /// <summary>Modo de proteção do IMAP.</summary>
    public SecureSocketMode? ImapSecurity { get; init; }

    /// <summary>Servidor SMTP.</summary>
    public string? SmtpHost { get; init; }

    /// <summary>Porta SMTP.</summary>
    public int? SmtpPort { get; init; }

    /// <summary>Modo de proteção do SMTP.</summary>
    public SecureSocketMode? SmtpSecurity { get; init; }

    /// <summary>Como a conta se autentica.</summary>
    public AuthenticationType AuthenticationType { get; init; } = AuthenticationType.Password;

    /// <summary>Provedor de identidade, quando OAuth.</summary>
    public OAuthProviderKind OAuthProvider { get; init; } = OAuthProviderKind.None;

    /// <summary>
    /// Senha, quando a autenticação é por senha.
    /// </summary>
    /// <remarks>
    /// Vive apenas nesta instância de comando e é gravada no Credential Manager; nunca
    /// chega ao banco de dados nem a log algum.
    /// </remarks>
    public string? Password { get; init; }

    /// <summary>Nome de usuário, quando difere do endereço.</summary>
    public string? UserName { get; init; }
}

/// <summary>Resultado do cadastro.</summary>
/// <param name="Succeeded">Se a conta foi cadastrada.</param>
/// <param name="AccountId">Identificador da conta criada.</param>
/// <param name="ErrorMessage">Motivo exibível da falha.</param>
public readonly record struct AddAccountResult(bool Succeeded, Guid? AccountId, string? ErrorMessage);

/// <summary>
/// Cadastra uma conta de e-mail dentro de um Diretório de Domínio.
/// </summary>
/// <remarks>
/// A ordem das etapas é deliberada: <b>a validação de domínio vem antes de qualquer
/// acesso à rede</b>. Testar credenciais de uma conta que a regra vai recusar de qualquer
/// forma gastaria tempo do usuário e, pior, poderia disparar bloqueio por tentativa
/// malsucedida no provedor.
/// </remarks>
public sealed class AddAccountHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICredentialStore _credentials;
    private readonly IAutodiscoverService _autodiscover;
    private readonly IImapClient _imapClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AddAccountHandler> _logger;

    public AddAccountHandler(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IFolderRepository folders,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        ICredentialStore credentials,
        IAutodiscoverService autodiscover,
        IImapClient imapClient,
        TimeProvider timeProvider,
        ILogger<AddAccountHandler> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _folders = folders;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _credentials = credentials;
        _autodiscover = autodiscover;
        _imapClient = imapClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa o cadastro.</summary>
    /// <exception cref="DomainMismatchException">
    /// O domínio da conta difere do domínio do diretório.
    /// </exception>
    public async Task<AddAccountResult> HandleAsync(
        AddAccountCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EmailAddress.TryParse(command.EmailAddress, out var address, out var parseError))
        {
            return new AddAccountResult(false, null, parseError);
        }

        var directory = await _directories.GetByIdAsync(command.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        if (directory is null)
        {
            return new AddAccountResult(false, null, "O Diretório de Domínio informado não existe.");
        }

        // Validação de domínio ANTES de qualquer rede. Se falhar, lança
        // DomainMismatchException com a mensagem já redigida para o usuário.
        try
        {
            directory.ValidateAccount(address);
        }
        catch (DomainMismatchException ex)
        {
            await RecordRejectionAsync(directory, address, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Cadastro recusado: conta do domínio {ActualDomain} em diretório do domínio {ExpectedDomain}.",
                ex.ActualDomain.Value, ex.ExpectedDomain.Value);
            throw;
        }

        var existing = await _accounts.GetByAddressAsync(address, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new AddAccountResult(false, null, $"A conta '{address.Value}' já está cadastrada.");
        }

        var now = _timeProvider.GetUtcNow();
        var account = Account.Create(directory.Id, address, command.DisplayName, now);

        var settings = await ResolveServerSettingsAsync(command, address, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return new AddAccountResult(
                false,
                null,
                "Não foi possível descobrir os servidores deste domínio. Informe IMAP e SMTP manualmente.");
        }

        account.ConfigureServers(
            settings.Value.ImapHost,
            settings.Value.ImapPort,
            settings.Value.ImapSecurity,
            settings.Value.SmtpHost,
            settings.Value.SmtpPort,
            settings.Value.SmtpSecurity,
            now);

        if (command.AuthenticationType == AuthenticationType.OAuth2)
        {
            account.UseOAuthAuthentication(command.OAuthProvider, now);
        }
        else
        {
            account.UsePasswordAuthentication(command.UserName, now);

            if (string.IsNullOrEmpty(command.Password))
            {
                return new AddAccountResult(false, null, "Informe a senha da conta.");
            }

            // O segredo vai para o cofre do Windows antes do teste de conexão, porque é
            // de lá que o cliente IMAP o lerá.
            await _credentials.SetSecretAsync(account.CredentialKey, command.Password, cancellationToken)
                .ConfigureAwait(false);
        }

        // A especificação exige validar as credenciais antes de concluir o cadastro.
        var test = await _imapClient.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        if (!test.Succeeded)
        {
            await _credentials.DeleteSecretAsync(account.CredentialKey, cancellationToken).ConfigureAwait(false);
            return new AddAccountResult(false, null, test.ErrorMessage);
        }

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            directory.AttachAccount(account, now);
            await _accounts.AddAsync(account, ct).ConfigureAwait(false);

            foreach (var folder in CreateDefaultFolders(account.Id, now))
            {
                await _folders.AddAsync(folder, ct).ConfigureAwait(false);
            }

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.AccountLinked,
                    $"Conta '{address.Value}' vinculada ao Diretório de Domínio '{directory.DomainName.Value}'.",
                    now,
                    entityType: nameof(Account),
                    entityId: account.Id,
                    accountId: account.Id,
                    domainDirectoryId: directory.Id),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new AddAccountResult(true, account.Id, null);
    }

    private async Task<DiscoveredServerSettings?> ResolveServerSettingsAsync(
        AddAccountCommand command, EmailAddress address, CancellationToken cancellationToken)
    {
        // Configuração manual completa tem precedência: se o usuário informou os
        // servidores, não faz sentido consultar a rede para contrariá-lo.
        if (!string.IsNullOrWhiteSpace(command.ImapHost) && !string.IsNullOrWhiteSpace(command.SmtpHost))
        {
            return new DiscoveredServerSettings(
                command.ImapHost,
                command.ImapPort ?? 993,
                command.ImapSecurity ?? SecureSocketMode.SslOnConnect,
                command.SmtpHost,
                command.SmtpPort ?? 587,
                command.SmtpSecurity ?? SecureSocketMode.StartTls,
                command.AuthenticationType,
                command.OAuthProvider);
        }

        return await _autodiscover.DiscoverAsync(address.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cria as pastas padrão exigidas pela especificação, mais a de pendências.
    /// </summary>
    /// <remarks>
    /// A pasta de pendências é local: ela recebe o que a regra de domínio recusou, um
    /// conceito que não existe no IMAP e que portanto não pode ser espelhado no servidor.
    /// </remarks>
    private static IEnumerable<Folder> CreateDefaultFolders(Guid accountId, DateTimeOffset now)
    {
        yield return Folder.Create(accountId, "Caixa de Entrada", FolderType.Inbox, now, remotePath: "INBOX");
        yield return Folder.Create(accountId, "Itens Enviados", FolderType.Sent, now, remotePath: "Sent");
        yield return Folder.Create(accountId, "Rascunhos", FolderType.Drafts, now, remotePath: "Drafts");
        yield return Folder.Create(accountId, "Lixeira", FolderType.Trash, now, remotePath: "Trash");
        yield return Folder.Create(accountId, "Spam", FolderType.Junk, now, remotePath: "Junk");
        yield return Folder.Create(accountId, "Arquivados", FolderType.Archive, now, remotePath: "Archive");
        yield return Folder.Create(accountId, "Pendências", FolderType.Pending, now, isLocalOnly: true);
        yield return Folder.Create(accountId, "Caixa de Saída", FolderType.Outbox, now, isLocalOnly: true);
    }

    private async Task RecordRejectionAsync(
        DomainDirectory directory, EmailAddress address, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(
            AuditLogEntry.Record(
                AuditEventType.AccountRejectedByDomainRule,
                $"Tentativa de vincular a conta '{address.Value}' ao Diretório de Domínio " +
                $"'{directory.DomainName.Value}' recusada: os domínios não coincidem.",
                _timeProvider.GetUtcNow(),
                AuditSeverity.Warning,
                entityType: nameof(Account),
                domainDirectoryId: directory.Id),
            cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
