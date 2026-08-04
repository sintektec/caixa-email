using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Handlers;

public sealed record SendMessageCommand(
    Guid AccountId,
    string Subject,
    string? HtmlBody,
    string? TextBody,
    IReadOnlyList<string> To,
    IReadOnlyList<string>? Cc = null,
    IReadOnlyList<string>? Bcc = null,
    DateTime? ScheduledSendAt = null,
    bool ReadReceiptRequested = false
);

public sealed class SendMessageHandler
{
    private readonly IMailRepository _repository;
    private readonly ISyncQueue _syncQueue;

    public SendMessageHandler(IMailRepository repository, ISyncQueue syncQueue)
    {
        _repository = repository;
        _syncQueue = syncQueue;
    }

    public async Task<Guid> HandleAsync(SendMessageCommand command, CancellationToken ct = default)
    {
        var account = await _repository.GetAccountByIdAsync(command.AccountId, ct)
            ?? throw new InvalidOperationException($"Account '{command.AccountId}' not found.");

        // Find drafts folder
        var folders = await _repository.GetFoldersByAccountAsync(command.AccountId, ct);
        var draftsFolder = folders.FirstOrDefault(f => f.FolderType == FolderType.Drafts)
            ?? throw new InvalidOperationException("Drafts folder not found.");

        var message = new Message
        {
            AccountId = command.AccountId,
            FolderId = draftsFolder.Id,
            Subject = command.Subject,
            SubjectNormalized = command.Subject.ToLowerInvariant(),
            FromAddress = account.EmailAddress,
            SentAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            IsDraft = true,
            SyncState = SyncState.PendingCreate,
            ScheduledSendAt = command.ScheduledSendAt,
            ReadReceiptRequested = command.ReadReceiptRequested
        };

        // Add addresses
        foreach (var to in command.To)
        {
            message.Addresses.Add(new MessageAddress
            {
                MessageId = message.Id,
                Kind = AddressKind.To,
                Address = to,
                Domain = ExtractDomain(to)
            });
        }

        if (command.Cc is not null)
        {
            foreach (var cc in command.Cc)
            {
                message.Addresses.Add(new MessageAddress
                {
                    MessageId = message.Id,
                    Kind = AddressKind.Cc,
                    Address = cc,
                    Domain = ExtractDomain(cc)
                });
            }
        }

        if (command.Bcc is not null)
        {
            foreach (var bcc in command.Bcc)
            {
                message.Addresses.Add(new MessageAddress
                {
                    MessageId = message.Id,
                    Kind = AddressKind.Bcc,
                    Address = bcc,
                    Domain = ExtractDomain(bcc)
                });
            }
        }

        // Create body
        message.Body = new MessageBody
        {
            MessageId = message.Id,
            HtmlBody = command.HtmlBody,
            TextBody = command.TextBody
        };

        await _repository.AddMessageAsync(message, ct);

        // Enqueue send operation
        await _syncQueue.EnqueueAsync(new OutboxOperation
        {
            AccountId = command.AccountId,
            OperationType = OutboxOperationType.SendMessage,
            EntityId = message.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                scheduledSendAt = command.ScheduledSendAt
            })
        }, ct);

        await _repository.SaveChangesAsync(ct);

        return message.Id;
    }

    private static string ExtractDomain(string address)
    {
        var atIndex = address.LastIndexOf('@');
        return atIndex >= 0 ? address[(atIndex + 1)..].ToLowerInvariant() : string.Empty;
    }
}
