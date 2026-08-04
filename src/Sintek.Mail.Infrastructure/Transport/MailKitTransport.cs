using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Transport;

public sealed class MailKitTransport : Sintek.Mail.Application.Ports.IMailTransport
{
    public async Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(account.ImapHost, account.ImapPort, GetSecureSocketOptions(account.ImapSecurity), ct);
            await client.AuthenticateAsync(account.EmailAddress, await GetPasswordAsync(account, ct), ct);
            await client.DisconnectAsync(true, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Folder>> FetchFoldersAsync(Account account, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var folders = new List<Folder>();
        var root = await client.GetFolderAsync(client.PersonalNamespaces[0].Path, ct);
        await FetchFoldersRecursiveAsync(client, root, account.Id, null, folders, ct);

        await client.DisconnectAsync(true, ct);
        return folders;
    }

    public async Task<IReadOnlyList<Message>> FetchMessagesAsync(Account account, Folder folder, long? lastSeenUid, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var imapFolder = await client.GetFolderAsync(folder.RemotePath, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        var messages = new List<Message>();
        var query = lastSeenUid.HasValue
            ? SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)lastSeenUid.Value + 1), UniqueId.MaxValue))
            : SearchQuery.All;

        var uids = await imapFolder.SearchAsync(query, ct);
        var summaries = await imapFolder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size, ct);

        foreach (var summary in summaries)
        {
            var message = MapToMessage(summary, account.Id, folder.Id);
            messages.Add(message);
        }

        await client.DisconnectAsync(true, ct);
        return messages;
    }

    public async Task<MessageBody?> FetchMessageBodyAsync(Account account, Folder folder, long uid, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var imapFolder = await client.GetFolderAsync(folder.RemotePath, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        var mimeMessage = await imapFolder.GetMessageAsync(new UniqueId((uint)uid), ct);

        var body = new MessageBody
        {
            HtmlBody = mimeMessage.HtmlBody,
            TextBody = mimeMessage.TextBody,
            DownloadedAt = DateTime.UtcNow
        };

        await client.DisconnectAsync(true, ct);
        return body;
    }

    public async Task SendMessageAsync(Account account, Message message, CancellationToken ct = default)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, GetSecureSocketOptions(account.SmtpSecurity), ct);
        await client.AuthenticateAsync(account.EmailAddress, await GetPasswordAsync(account, ct), ct);

        var mimeMessage = MapToMimeMessage(message);
        await client.SendAsync(mimeMessage, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task MoveMessageAsync(Account account, long uid, Folder sourceFolder, Folder targetFolder, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var source = await client.GetFolderAsync(sourceFolder.RemotePath, ct);
        var target = await client.GetFolderAsync(targetFolder.RemotePath, ct);

        await source.OpenAsync(FolderAccess.ReadWrite, ct);
        await source.MoveToAsync(new UniqueId((uint)uid), target, ct);

        await client.DisconnectAsync(true, ct);
    }

    public async Task DeleteMessageAsync(Account account, long uid, Folder folder, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var imapFolder = await client.GetFolderAsync(folder.RemotePath, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadWrite, ct);
        await imapFolder.AddFlagsAsync(new UniqueId((uint)uid), MessageFlags.Deleted, true, ct);
        await imapFolder.ExpungeAsync(ct);

        await client.DisconnectAsync(true, ct);
    }

    public async Task SetReadStatusAsync(Account account, long uid, Folder folder, bool isRead, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var imapFolder = await client.GetFolderAsync(folder.RemotePath, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadWrite, ct);

        if (isRead)
            await imapFolder.AddFlagsAsync(new UniqueId((uint)uid), MessageFlags.Seen, true, ct);
        else
            await imapFolder.RemoveFlagsAsync(new UniqueId((uint)uid), MessageFlags.Seen, true, ct);

        await client.DisconnectAsync(true, ct);
    }

    public async Task SetFlagStatusAsync(Account account, long uid, Folder folder, bool isFlagged, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, account, ct);

        var imapFolder = await client.GetFolderAsync(folder.RemotePath, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadWrite, ct);

        if (isFlagged)
            await imapFolder.AddFlagsAsync(new UniqueId((uint)uid), MessageFlags.Flagged, true, ct);
        else
            await imapFolder.RemoveFlagsAsync(new UniqueId((uint)uid), MessageFlags.Flagged, true, ct);

        await client.DisconnectAsync(true, ct);
    }

    private async Task ConnectAndAuthenticateAsync(ImapClient client, Account account, CancellationToken ct)
    {
        await client.ConnectAsync(account.ImapHost, account.ImapPort, GetSecureSocketOptions(account.ImapSecurity), ct);
        await client.AuthenticateAsync(account.EmailAddress, await GetPasswordAsync(account, ct), ct);
    }

    private static SecureSocketOptions GetSecureSocketOptions(SecurityProtocol protocol) => protocol switch
    {
        SecurityProtocol.Ssl => SecureSocketOptions.SslOnConnect,
        SecurityProtocol.StartTls => SecureSocketOptions.StartTls,
        SecurityProtocol.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto
    };

    private static async Task<string> GetPasswordAsync(Account account, CancellationToken ct)
    {
        // TODO: Integrate with ICredentialStore
        // For now, return empty — real implementation will use Credential Manager
        await Task.CompletedTask;
        return string.Empty;
    }

    private static Message MapToMessage(IMessageSummary summary, Guid accountId, Guid folderId)
    {
        var envelope = summary.Envelope;
        return new Message
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = (long)summary.UniqueId.Id,
            MessageId = envelope?.MessageId,
            Subject = envelope?.Subject ?? string.Empty,
            SubjectNormalized = (envelope?.Subject ?? string.Empty).ToLowerInvariant(),
            FromAddress = envelope?.From?.FirstOrDefault()?.ToString() ?? string.Empty,
            SentAt = envelope?.Date?.UtcDateTime ?? DateTime.UtcNow,
            ReceivedAt = summary.InternalDate?.UtcDateTime ?? DateTime.UtcNow,
            Size = (long)(summary.Size ?? 0),
            IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            IsFlagged = summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            HasAttachments = summary.Attachments?.Any() ?? false
        };
    }

    private static MimeMessage MapToMimeMessage(Message message)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.FromAddress));
        mime.Subject = message.Subject;
        mime.Date = new DateTimeOffset(message.SentAt);

        foreach (var addr in message.Addresses)
        {
            var mailbox = MailboxAddress.Parse(addr.Address);
            switch (addr.Kind)
            {
                case AddressKind.To: mime.To.Add(mailbox); break;
                case AddressKind.Cc: mime.Cc.Add(mailbox); break;
                case AddressKind.Bcc: mime.Bcc.Add(mailbox); break;
                case AddressKind.ReplyTo: mime.ReplyTo.Add(mailbox); break;
            }
        }

        var bodyBuilder = new BodyBuilder();
        if (message.Body?.HtmlBody is not null)
            bodyBuilder.HtmlBody = message.Body.HtmlBody;
        if (message.Body?.TextBody is not null)
            bodyBuilder.TextBody = message.Body.TextBody;

        mime.Body = bodyBuilder.ToMessageBody();
        return mime;
    }

    private static async Task FetchFoldersRecursiveAsync(ImapClient client, IMailFolder imapFolder, Guid accountId, Guid? parentId, List<Folder> folders, CancellationToken ct)
    {
        var folder = new Folder
        {
            AccountId = accountId,
            ParentFolderId = parentId,
            Name = imapFolder.Name,
            RemotePath = imapFolder.FullName,
            FolderType = MapFolderType(imapFolder)
        };
        folders.Add(folder);

        var subfolders = await imapFolder.GetSubfoldersAsync(false, ct);
        foreach (var sub in subfolders)
        {
            await FetchFoldersRecursiveAsync(client, sub, accountId, folder.Id, folders, ct);
        }
    }

    private static FolderType MapFolderType(IMailFolder folder)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.Sent)) return FolderType.Sent;
        if (folder.Attributes.HasFlag(FolderAttributes.Drafts)) return FolderType.Drafts;
        if (folder.Attributes.HasFlag(FolderAttributes.Trash)) return FolderType.Trash;
        if (folder.Attributes.HasFlag(FolderAttributes.Junk)) return FolderType.Junk;
        if (folder.Attributes.HasFlag(FolderAttributes.Archive)) return FolderType.Archive;
        if (folder.Attributes.HasFlag(FolderAttributes.All)) return FolderType.Custom;
        if (folder.Attributes.HasFlag(FolderAttributes.Flagged)) return FolderType.Custom;
        return FolderType.Custom;
    }
}
