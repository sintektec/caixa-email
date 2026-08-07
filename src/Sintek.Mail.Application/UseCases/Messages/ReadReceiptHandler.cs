using System.Globalization;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>
/// Responde a um pedido de confirmação de leitura.
/// </summary>
/// <remarks>
/// <para>
/// A confirmação <b>nunca</b> sai sozinha. O cabeçalho
/// <c>Disposition-Notification-To</c> é um pedido, não uma ordem: quem decide é o
/// destinatário, e enviar sem perguntar entregaria ao remetente a informação de que a
/// mensagem foi aberta — que é exatamente o que um remetente hostil quer confirmar.
/// </para>
/// <para>
/// Recusar também é decisão registrada: <c>Message.ReadReceiptHandled</c> impede que a
/// pergunta reapareça a cada abertura, o que transformaria a recusa em um "ainda não".
/// </para>
/// </remarks>
public sealed class ReadReceiptHandler
{
    private readonly IMessageRepository _messages;
    private readonly IAccountRepository _accounts;
    private readonly ComposeMessageHandler _compose;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReadReceiptHandler> _logger;

    public ReadReceiptHandler(
        IMessageRepository messages,
        IAccountRepository accounts,
        ComposeMessageHandler compose,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ReadReceiptHandler> logger)
    {
        _messages = messages;
        _accounts = accounts;
        _compose = compose;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Envia a confirmação ao remetente e registra a decisão.</summary>
    public async Task<ComposeMessageResult> SendAsync(
        Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            return new ComposeMessageResult(false, null, "A mensagem não existe mais.");
        }

        if (message.FromAddress is null)
        {
            return new ComposeMessageResult(false, null, "A mensagem não tem remetente para confirmar.");
        }

        var account = await _accounts.GetByIdAsync(message.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new ComposeMessageResult(false, null, "A conta informada não existe.");
        }

        var now = _timeProvider.GetUtcNow();

        var result = await _compose.SendAsync(new ComposeMessageCommand
        {
            AccountId = account.Id,
            Subject = $"Confirmação de leitura: {message.Subject}",
            TextBody = BuildBody(account.EmailAddress, message.Subject, now),
            Recipients = [new DraftRecipient(AddressKind.To, message.FromAddress, null)],
            // A confirmação encadeia na conversa original: o remetente a vê junto do que
            // enviou, e não como uma mensagem solta sem contexto.
            InReplyTo = message.MessageId,
            ThreadId = message.ThreadId,
        }, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            message.MarkReadReceiptHandled(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Confirmação de leitura da mensagem {MessageId} enfileirada.", messageId);
        }

        return result;
    }

    /// <summary>Registra que o usuário recusou enviar a confirmação.</summary>
    public async Task<bool> DeclineAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return false;
        }

        message.MarkReadReceiptHandled(_timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Corpo da confirmação, em português e com data explícita.
    /// </summary>
    /// <remarks>
    /// Formato invariante com padrão brasileiro escrito à mão: com
    /// <c>InvariantGlobalization</c> ligado, pedir a cultura "pt-BR" lança em tempo de
    /// execução.
    /// </remarks>
    private static string BuildBody(EmailAddress reader, string subject, DateTimeOffset readAt)
        => $"""
            Esta é uma confirmação automática de leitura.

            A mensagem "{subject}" foi exibida em {readAt.ToLocalTime().ToString("dd/MM/yyyy 'às' HH:mm", CultureInfo.InvariantCulture)} por {reader.Value}.

            A exibição não garante que a mensagem tenha sido lida ou compreendida.
            """;
}
