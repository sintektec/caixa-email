using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.Handlers;

public sealed record MoveMessageCommand(
    Guid MessageId,
    Guid TargetFolderId,
    bool ConfirmOverride = false
);

public sealed class MoveMessageHandler
{
    private readonly IMailRepository _repository;
    private readonly ISyncQueue _syncQueue;

    public MoveMessageHandler(IMailRepository repository, ISyncQueue syncQueue)
    {
        _repository = repository;
        _syncQueue = syncQueue;
    }

    public async Task HandleAsync(MoveMessageCommand command, CancellationToken ct = default)
    {
        var message = await _repository.GetMessageByIdAsync(command.MessageId, ct)
            ?? throw new InvalidOperationException($"Message '{command.MessageId}' not found.");

        var targetFolder = await _repository.GetFolderByIdAsync(command.TargetFolderId, ct)
            ?? throw new InvalidOperationException($"Folder '{command.TargetFolderId}' not found.");

        // Check domain restriction
        if (targetFolder.IsDomainRestricted && targetFolder.RestrictedToDomainId.HasValue)
        {
            var domain = await _repository.GetDomainByIdAsync(targetFolder.RestrictedToDomainId.Value, ct)
                ?? throw new InvalidOperationException($"Domain '{targetFolder.RestrictedToDomainId}' not found.");

            var evaluator = new DomainMembershipEvaluator(domain);
            var isValid = evaluator.EvaluateMessage(message);

            if (!isValid)
            {
                switch (domain.InvalidEmailAction)
                {
                    case InvalidEmailAction.Block:
                        throw new MessageDomainViolationException();

                    case InvalidEmailAction.WarnAndConfirm:
                        if (!command.ConfirmOverride)
                            throw new MessageDomainViolationException();
                        break;

                    case InvalidEmailAction.MoveToPending:
                        // Find or create pending folder
                        var pendingFolder = await GetOrCreatePendingFolderAsync(message.AccountId, ct);
                        targetFolder = pendingFolder;
                        break;

                    case InvalidEmailAction.LogOnly:
                        // Log but allow
                        await _repository.AddAuditLogAsync(new AuditLog
                        {
                            EventType = "DomainValidationWarning",
                            EntityType = "Message",
                            EntityId = message.Id,
                            Description = $"Message moved to restricted folder despite domain mismatch.",
                            Severity = "Warning"
                        }, ct);
                        break;
                }
            }
        }

        // Update local state
        message.FolderId = targetFolder.Id;
        message.SyncState = SyncState.PendingUpdate;
        message.UpdatedAt = DateTime.UtcNow;

        await _repository.UpdateMessageAsync(message, ct);

        // Enqueue sync operation
        await _syncQueue.EnqueueAsync(new OutboxOperation
        {
            AccountId = message.AccountId,
            OperationType = OutboxOperationType.MoveMessage,
            EntityId = message.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                targetFolderId = targetFolder.Id,
                sourceFolderId = message.FolderId
            })
        }, ct);

        await _repository.SaveChangesAsync(ct);
    }

    private async Task<Folder> GetOrCreatePendingFolderAsync(Guid accountId, CancellationToken ct)
    {
        var folders = await _repository.GetFoldersByAccountAsync(accountId, ct);
        var pending = folders.FirstOrDefault(f => f.FolderType == FolderType.Pending);

        if (pending is not null)
            return pending;

        pending = new Folder
        {
            AccountId = accountId,
            Name = "Pendências",
            FolderType = FolderType.Pending,
            RemotePath = "Pendencias"
        };

        await _repository.AddFolderAsync(pending, ct);
        return pending;
    }
}
