using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Sync;

/// <summary>Resultado da sincronização de uma conta.</summary>
/// <param name="Succeeded">Se o ciclo chegou ao fim.</param>
/// <param name="OutboxDrained">Operações da fila aplicadas no servidor.</param>
/// <param name="FoldersMirrored">Resultado do espelhamento de pastas.</param>
/// <param name="MessagesAdded">Mensagens novas trazidas.</param>
/// <param name="MessagesUpdated">Mensagens já conhecidas com marcadores alterados.</param>
/// <param name="MessagesRedirected">Mensagens desviadas pela regra de Diretório de Domínio.</param>
/// <param name="ErrorMessage">Motivo exibível da falha.</param>
/// <param name="IsAuthenticationFailure">Se a falha foi de credencial.</param>
public sealed record SyncAccountResult(
    bool Succeeded,
    int OutboxDrained,
    FolderMirrorResult FoldersMirrored,
    int MessagesAdded,
    int MessagesUpdated,
    int MessagesRedirected,
    string? ErrorMessage = null,
    bool IsAuthenticationFailure = false);

/// <summary>Aplica no servidor as operações que aguardam na fila de saída.</summary>
/// <remarks>
/// A implementação vive na infraestrutura, junto do cliente IMAP. A abstração existe para
/// que o orquestrador de sincronização possa ser testado sem servidor algum.
/// </remarks>
public interface IOutboxDrainer
{
    /// <summary>Processa as operações pendentes da conta e devolve quantas concluíram.</summary>
    Task<int> DrainAsync(Account account, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executa um ciclo completo de sincronização de uma conta.
/// </summary>
/// <remarks>
/// <para>
/// A ordem das etapas é a parte que importa:
/// </para>
/// <list type="number">
/// <item>Conectar e autenticar.</item>
/// <item><b>Drenar a fila de saída.</b></item>
/// <item>Espelhar a árvore de pastas.</item>
/// <item>Sincronizar o conteúdo de cada pasta.</item>
/// </list>
/// <para>
/// A fila vem <b>antes</b> da leitura por um motivo concreto: enquanto ela não drena, o
/// servidor ainda não sabe do que o usuário fez offline. Ler primeiro traria o estado
/// antigo e sobrescreveria localmente a intenção dele — a mensagem que ele marcou como lida
/// voltaria a não lida, e só depois a fila a marcaria de novo. Ele veria o marcador piscar
/// e concluiria, com razão, que o programa está confuso.
/// </para>
/// <para>
/// Falha de credencial não é tratada como erro comum: ela desliga a conta do ciclo
/// automático até que o usuário reautentique. Insistir com senha recusada é a forma mais
/// rápida de ganhar um bloqueio temporário no provedor.
/// </para>
/// </remarks>
public sealed class SyncAccountHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IImapClient _imapClient;
    private readonly IOutboxDrainer _outboxDrainer;
    private readonly FolderMirrorService _folderMirror;
    private readonly MessageSyncService _messageSync;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncAccountHandler> _logger;

    public SyncAccountHandler(
        IAccountRepository accounts,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        IOutboxDrainer outboxDrainer,
        FolderMirrorService folderMirror,
        MessageSyncService messageSync,
        TimeProvider timeProvider,
        ILogger<SyncAccountHandler> logger)
    {
        _accounts = accounts;
        _folders = folders;
        _unitOfWork = unitOfWork;
        _imapClient = imapClient;
        _outboxDrainer = outboxDrainer;
        _folderMirror = folderMirror;
        _messageSync = messageSync;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Sincroniza uma conta.</summary>
    public async Task<SyncAccountResult> HandleAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return Failure("A conta informada não existe.");
        }

        if (!account.IsActive)
        {
            return Failure("A conta está desativada.");
        }

        account.SetSyncStatus(AccountSyncStatus.Syncing, _timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await RunCycleAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            account.SetSyncStatus(AccountSyncStatus.Offline, _timeProvider.GetUtcNow());
            await _unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // A mensagem entra em LastSyncError, que a interface exibe. Ela não pode conter
            // credencial nem conteúdo de mensagem — daí só ex.Message, nunca o objeto todo.
            account.MarkSyncFailed(ex.Message, isAuthenticationFailure: false, _timeProvider.GetUtcNow());
            await _unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogError(ex, "A sincronização da conta {AccountId} falhou.", accountId);

            return Failure(ex.Message);
        }
    }

    private async Task<SyncAccountResult> RunCycleAsync(Account account, CancellationToken cancellationToken)
    {
        var connection = await _imapClient.ConnectAsync(account, cancellationToken).ConfigureAwait(false);

        if (!connection.Succeeded)
        {
            var now = _timeProvider.GetUtcNow();

            if (connection.IsAuthenticationFailure)
            {
                account.MarkSyncFailed(connection.ErrorMessage!, isAuthenticationFailure: true, now);
            }
            else
            {
                // Sem conexão não é erro: é o modo offline funcionando como projetado. Os
                // dados locais seguem utilizáveis e a fila espera a rede voltar.
                account.SetSyncStatus(AccountSyncStatus.Offline, now);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new SyncAccountResult(
                false, 0, default, 0, 0, 0, connection.ErrorMessage, connection.IsAuthenticationFailure);
        }

        var drained = await _outboxDrainer.DrainAsync(account, cancellationToken).ConfigureAwait(false);

        var remoteFolders = await _imapClient.ListFoldersAsync(cancellationToken).ConfigureAwait(false);
        var mirrored = await _folderMirror.MirrorAsync(account.Id, remoteFolders, cancellationToken)
            .ConfigureAwait(false);

        var added = 0;
        var updated = 0;
        var redirected = 0;

        var folders = await _folders.ListByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);

        foreach (var folder in OrderForSync(folders))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _messageSync.SyncFolderAsync(folder, cancellationToken).ConfigureAwait(false);

            added += result.Added;
            updated += result.Updated;
            redirected += result.RedirectedToPending;
        }

        account.MarkSynced(_timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _imapClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Conta {AccountId} sincronizada: {Drained} da fila, {Added} nova(s), {Updated} atualizada(s).",
            account.Id, drained, added, updated);

        return new SyncAccountResult(true, drained, mirrored, added, updated, redirected);
    }

    /// <summary>
    /// Ordena as pastas para sincronizar primeiro o que o usuário olha primeiro.
    /// </summary>
    /// <remarks>
    /// Caixa de Entrada na frente, depois Enviados e Rascunhos, e o resto por último. Numa
    /// primeira sincronização de caixa grande, a ordem decide se o usuário vê a correspondência
    /// recente em segundos ou depois de o Arquivo Morto de 2019 terminar de baixar.
    /// </remarks>
    internal static IEnumerable<Folder> OrderForSync(IReadOnlyList<Folder> folders)
        => folders
            .Where(f => !f.IsLocalOnly && f.SyncEnabled)
            .OrderBy(f => f.FolderType switch
            {
                FolderType.Inbox => 0,
                FolderType.Sent => 1,
                FolderType.Drafts => 2,
                FolderType.Junk => 4,
                FolderType.Trash => 5,
                FolderType.Archive => 6,
                _ => 3,
            })
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.RemotePath, StringComparer.Ordinal);

    private static SyncAccountResult Failure(string message)
        => new(false, 0, default, 0, 0, 0, message);
}
