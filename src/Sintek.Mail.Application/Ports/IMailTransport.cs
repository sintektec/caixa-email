using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Ports;

/// <summary>
/// Transport layer for sending and receiving e-mail via IMAP/SMTP.
/// </summary>
public interface IMailTransport
{
    /// <summary>Tests the connection and authentication for an account.</summary>
    Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default);

    /// <summary>Fetches folder list from the server.</summary>
    Task<IReadOnlyList<Folder>> FetchFoldersAsync(Account account, CancellationToken ct = default);

    /// <summary>Fetches messages from a folder (incremental sync).</summary>
    Task<IReadOnlyList<Message>> FetchMessagesAsync(Account account, Folder folder, long? lastSeenUid, CancellationToken ct = default);

    /// <summary>Fetches a single message body.</summary>
    Task<MessageBody?> FetchMessageBodyAsync(Account account, Folder folder, long uid, CancellationToken ct = default);

    /// <summary>Sends a message.</summary>
    Task SendMessageAsync(Account account, Message message, CancellationToken ct = default);

    /// <summary>Moves a message to another folder on the server.</summary>
    Task MoveMessageAsync(Account account, long uid, Folder sourceFolder, Folder targetFolder, CancellationToken ct = default);

    /// <summary>Deletes a message on the server.</summary>
    Task DeleteMessageAsync(Account account, long uid, Folder folder, CancellationToken ct = default);

    /// <summary>Marks a message as read/unread on the server.</summary>
    Task SetReadStatusAsync(Account account, long uid, Folder folder, bool isRead, CancellationToken ct = default);

    /// <summary>Flags/unflags a message on the server.</summary>
    Task SetFlagStatusAsync(Account account, long uid, Folder folder, bool isFlagged, CancellationToken ct = default);
}
