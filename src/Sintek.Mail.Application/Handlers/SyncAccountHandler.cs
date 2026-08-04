using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Handlers;

public sealed record SyncAccountCommand(Guid AccountId);

public sealed class SyncAccountHandler
{
    private readonly IMailRepository _repository;
    private readonly IMailTransport _transport;
    private readonly ISyncQueue _syncQueue;

    public SyncAccountHandler(IMailRepository repository, IMailTransport transport, ISyncQueue syncQueue)
    {
        _repository = repository;
        _transport = transport;
        _syncQueue = syncQueue;
    }

    public async Task HandleAsync(SyncAccountCommand command, CancellationToken ct = default)
    {
        var account = await _repository.GetAccountByIdAsync(command.AccountId, ct)
            ?? throw new InvalidOperationException($"Account '{command.AccountId}' not found.");

        // Update status
        account.SyncStatus = AccountSyncStatus.Syncing;
        await _repository.UpdateAccountAsync(account, ct);
        await _repository.SaveChangesAsync(ct);

        try
        {
            // Test connection
            var connected = await _transport.TestConnectionAsync(account, ct);
            if (!connected)
            {
                account.SyncStatus = AccountSyncStatus.Error;
                account.LastSyncError = "Connection failed.";
                await _repository.UpdateAccountAsync(account, ct);
                await _repository.SaveChangesAsync(ct);
                return;
            }

            // Sync folders
            var remoteFolders = await _transport.FetchFoldersAsync(account, ct);
            var localFolders = await _repository.GetFoldersByAccountAsync(account.Id, ct);

            foreach (var remoteFolder in remoteFolders)
            {
                var existing = localFolders.FirstOrDefault(f => f.RemotePath == remoteFolder.RemotePath);
                if (existing is null)
                {
                    remoteFolder.AccountId = account.Id;
                    await _repository.AddFolderAsync(remoteFolder, ct);
                }
                else
                {
                    existing.Name = remoteFolder.Name;
                    existing.FolderType = remoteFolder.FolderType;
                    await _repository.UpdateFolderAsync(existing, ct);
                }
            }

            // Sync messages for each folder
            foreach (var folder in localFolders.Where(f => f.SyncEnabled))
            {
                var messages = await _transport.FetchMessagesAsync(account, folder, folder.LastSeenUid, ct);
                foreach (var message in messages)
                {
                    message.AccountId = account.Id;
                    message.FolderId = folder.Id;
                    await _repository.AddMessageAsync(message, ct);
                }

                if (messages.Count > 0)
                {
                    folder.LastSeenUid = messages.Max(m => m.Uid);
                    await _repository.UpdateFolderAsync(folder, ct);
                }
            }

            // Drain outbox
            var pendingOps = await _repository.GetPendingOutboxOperationsAsync(account.Id, ct);
            foreach (var op in pendingOps)
            {
                // Process operations (simplified — real implementation would use Polly retry)
                op.Status = OutboxOperationStatus.Processing;
                await _repository.UpdateOutboxOperationAsync(op, ct);
            }

            account.SyncStatus = AccountSyncStatus.Online;
            account.LastSyncAt = DateTime.UtcNow;
            account.LastSyncError = null;
        }
        catch (Exception ex)
        {
            account.SyncStatus = AccountSyncStatus.Error;
            account.LastSyncError = ex.Message;
        }

        await _repository.UpdateAccountAsync(account, ct);
        await _repository.SaveChangesAsync(ct);
    }
}
