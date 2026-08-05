using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Calendar;
using Sintek.Mail.Application.UseCases.Contacts;
using Sintek.Mail.Application.UseCases.Messages;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Monta o compositor e o histórico de destinatários para os testes de apresentação.
/// </summary>
/// <remarks>
/// O <see cref="ComposeMessageHandler"/> alimenta o histórico no envio. O encadeamento é
/// mantido de pé com repositórios substituídos, e não removido: um caminho que só existe
/// em produção é um caminho que ninguém verifica.
/// </remarks>
internal static class ComposeFactory
{
    public static ComposeMessageHandler Create(
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        RecipientHistoryHandler recipientHistory,
        TimeProvider clock)
        => new(
            messages,
            folders,
            accounts,
            unitOfWork,
            outbox,
            recipientHistory,
            clock,
            NullLogger<ComposeMessageHandler>.Instance);

    public static RecipientHistoryHandler RecipientHistory(
        IRecipientHistoryRepository history,
        IContactRepository contacts,
        IAccountRepository accounts,
        IDomainDirectoryRepository directories,
        IUnitOfWork unitOfWork,
        TimeProvider clock)
        => new(
            history,
            contacts,
            accounts,
            directories,
            unitOfWork,
            clock,
            NullLogger<RecipientHistoryHandler>.Instance);

    public static RecipientHistoryHandler InertRecipientHistory(
        IUnitOfWork unitOfWork, TimeProvider clock)
        => RecipientHistory(
            Substitute.For<IRecipientHistoryRepository>(),
            Substitute.For<IContactRepository>(),
            Substitute.For<IAccountRepository>(),
            Substitute.For<IDomainDirectoryRepository>(),
            unitOfWork,
            clock);

    /// <summary>Importador de convites inerte — o serializador substituído nada lê.</summary>
    public static ImportInvitationHandler InertInvitations(
        IUnitOfWork unitOfWork, TimeProvider clock)
        => new(
            Substitute.For<ICalendarRepository>(),
            Substitute.For<IAccountRepository>(),
            Substitute.For<IDomainDirectoryRepository>(),
            Substitute.For<Sintek.Mail.Application.Abstractions.Calendar.ICalendarSerializer>(),
            Substitute.For<IAuditLogRepository>(),
            unitOfWork,
            clock,
            NullLogger<ImportInvitationHandler>.Instance);

    /// <summary>Download de conteúdo com o importador de convites inerte.</summary>
    public static DownloadMessageContentHandler Download(
        IMessageRepository messages,
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        IHtmlSanitizer sanitizer,
        IAttachmentStore attachmentStore,
        TimeProvider clock)
        => new(
            messages, folders, unitOfWork, imapClient, sanitizer, attachmentStore,
            InertInvitations(unitOfWork, clock),
            clock,
            NullLogger<DownloadMessageContentHandler>.Instance);
}
