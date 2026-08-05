using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Application.UseCases.Rules;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a filtragem local na chegada: a lista de bloqueados antes das regras, as ações do
/// motor e o respeito à regra de domínio nas movimentações decididas por regra.
/// </summary>
public class ApplyArrivalRulesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly IRuleRepository _rules = Substitute.For<IRuleRepository>();
    private readonly ISenderReputationRepository _reputations = Substitute.For<ISenderReputationRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly Abstractions.Mail.IImapClient _imap = Substitute.For<Abstractions.Mail.IImapClient>();
    private readonly IHtmlSanitizer _sanitizer = Substitute.For<IHtmlSanitizer>();
    private readonly IAttachmentStore _attachments = Substitute.For<IAttachmentStore>();
    private readonly FakeTimeProvider _clock = new(Now);

    private readonly DomainDirectory _directory;
    private readonly Account _account;
    private readonly Folder _inbox;
    private readonly Message _message;

    public ApplyArrivalRulesHandlerTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _account = Account.Create(_directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);
        _inbox = Folder.Create(_account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        _message = Message.Create(_account.Id, _inbox.Id, "<chegada@ext>", Now, Now, Now);
        _message.SetHeaders(
            "Proposta comercial", EmailAddress.Parse("cliente@promo.com"), "Cliente", null, null, Now);

        _accounts.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _messages.GetWithParticipantsAsync(_message.Id, Arg.Any<CancellationToken>()).Returns(_message);
        _messages.GetByIdAsync(_message.Id, Arg.Any<CancellationToken>()).Returns(_message);
        _messages.GetParticipantsAsync(_message.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MessageParticipant>());

        _reputations.ListAsync(Arg.Any<SenderReputationKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SenderReputation>());
        _rules.ListEnabledForAccountAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Rule>());
    }

    private ApplyArrivalRulesHandler CreateHandler()
    {
        var enqueuer = new OutboxEnqueuer(_outbox, _clock);
        var moveMessage = new MoveMessageHandler(
            _messages, _folders, _directories, _audit, _unitOfWork,
            enqueuer, _clock, NullLogger<MoveMessageHandler>.Instance);

        return new ApplyArrivalRulesHandler(
            _rules,
            _reputations,
            _messages,
            _folders,
            _accounts,
            _categories,
            _audit,
            _unitOfWork,
            moveMessage,
            new MarkAsSpamHandler(
                _messages, _folders, moveMessage, enqueuer, _unitOfWork,
                NullLogger<MarkAsSpamHandler>.Instance),
            new DownloadMessageContentHandler(
                _messages, _folders, _unitOfWork, _imap, _sanitizer, _attachments, _clock,
                NullLogger<DownloadMessageContentHandler>.Instance),
            TestFactories.Compose(_messages, _folders, _accounts, _unitOfWork, enqueuer, _clock),
            enqueuer,
            _clock,
            NullLogger<ApplyArrivalRulesHandler>.Instance);
    }

    private Folder ArrangeCustomFolder(string name = "Clientes")
    {
        var folder = Folder.Create(_account.Id, name, FolderType.Custom, Now, remotePath: name);
        _folders.GetByIdAsync(folder.Id, Arg.Any<CancellationToken>()).Returns(folder);
        return folder;
    }

    private Rule ArrangeRule(Action<Rule> configure, string name = "Regra de teste")
    {
        var rule = Rule.Create(name, Now);
        configure(rule);
        _rules.ListEnabledForAccountAsync(_account.Id, _account.DomainDirectoryId, Arg.Any<CancellationToken>())
            .Returns([rule]);
        return rule;
    }

    // ----- Remetentes bloqueados -------------------------------------------------------

    [Fact]
    public async Task RemetenteBloqueado_VaiParaOLixoEletronico_SemAvaliarRegras()
    {
        var junk = Folder.Create(_account.Id, "Lixo Eletrônico", FolderType.Junk, Now, remotePath: "Junk");
        _folders.GetByTypeAsync(_account.Id, FolderType.Junk, Arg.Any<CancellationToken>()).Returns(junk);
        _folders.GetByIdAsync(junk.Id, Arg.Any<CancellationToken>()).Returns(junk);

        _reputations.ListAsync(SenderReputationKind.Blocked, Arg.Any<CancellationToken>())
            .Returns([SenderReputation.ForDomain(SenderReputationKind.Blocked, EmailDomain.Parse("promo.com"), Now)]);

        // Uma regra que casaria — não pode ser avaliada para mensagem bloqueada.
        ArrangeRule(r => r.AddAction(RuleActionType.MarkAsRead, Now));

        var result = await CreateHandler().HandleAsync(_message.Id);

        result.WasBlocked.Should().BeTrue();
        result.AppliedRuleCount.Should().Be(0);
        _message.FolderId.Should().Be(junk.Id, "bloqueado vai direto para o lixo eletrônico");
        _message.IsRead.Should().BeFalse("as regras não rodam sobre mensagem bloqueada");

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.SenderBlocked),
            Arg.Any<CancellationToken>());
    }

    // ----- Ações das regras ------------------------------------------------------------

    [Fact]
    public async Task RegraDeMover_PassaPeloMoveMessageHandler()
    {
        var target = ArrangeCustomFolder();

        ArrangeRule(r =>
        {
            r.AddCondition(RuleField.Subject, RuleOperator.Contains, "proposta", Now);
            r.AddAction(RuleActionType.MoveToFolder, Now, targetFolderId: target.Id);
        });

        var result = await CreateHandler().HandleAsync(_message.Id);

        result.AppliedRuleCount.Should().Be(1);
        _message.FolderId.Should().Be(target.Id);

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.RuleApplied),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegraDeCategorizar_NaoDuplicaCategoriaJaAplicada()
    {
        var categoryId = Guid.CreateVersion7();

        ArrangeRule(r => r.AddAction(RuleActionType.ApplyCategory, Now, targetCategoryId: categoryId));

        _categories.IsAssignedAsync(_message.Id, categoryId, Arg.Any<CancellationToken>())
            .Returns(false, true);

        await CreateHandler().HandleAsync(_message.Id);
        await CreateHandler().HandleAsync(_message.Id);

        await _categories.Received(1).AssignAsync(
            Arg.Is<MessageCategory>(mc => mc.MessageId == _message.Id && mc.CategoryId == categoryId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegraDeMarcarComoLida_PropagaPelaFila()
    {
        ArrangeRule(r => r.AddAction(RuleActionType.MarkAsRead, Now));

        await CreateHandler().HandleAsync(_message.Id);

        _message.IsRead.Should().BeTrue();

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.MarkAsRead),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegraComInterromper_ParaACadeia()
    {
        var first = Rule.Create("Primeira", Now, priority: 0);
        first.AddAction(RuleActionType.MarkAsImportant, Now);
        first.AddAction(RuleActionType.StopProcessing, Now);

        var second = Rule.Create("Segunda", Now, priority: 1);
        second.AddAction(RuleActionType.Flag, Now);

        _rules.ListEnabledForAccountAsync(_account.Id, _account.DomainDirectoryId, Arg.Any<CancellationToken>())
            .Returns([first, second]);

        var result = await CreateHandler().HandleAsync(_message.Id);

        result.AppliedRuleCount.Should().Be(1);
        _message.Importance.Should().Be(MessageImportance.High);
        _message.IsFlagged.Should().BeFalse("a segunda regra não pode rodar após o StopProcessing");
    }

    [Fact]
    public async Task RegraQueNaoCasa_NaoFazNada()
    {
        ArrangeRule(r =>
        {
            r.AddCondition(RuleField.Subject, RuleOperator.Contains, "fatura", Now);
            r.AddAction(RuleActionType.MarkAsRead, Now);
        });

        var result = await CreateHandler().HandleAsync(_message.Id);

        result.AppliedRuleCount.Should().Be(0);
        _message.IsRead.Should().BeFalse();
        await _audit.DidNotReceive().RecordAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ----- Regra de domínio prevalece --------------------------------------------------

    [Fact]
    public async Task MoverParaPastaRestrita_MensagemIncompativel_RegistraAcaoIgnorada()
    {
        // Diretório que exige confirmação — e não há usuário durante a sincronização.
        var restricted = DomainDirectory.Create(
            EmailDomain.Parse("cliente.com"), Now,
            invalidEmailAction: InvalidEmailAction.WarnAndConfirm);
        _directories.GetByIdAsync(restricted.Id, Arg.Any<CancellationToken>()).Returns(restricted);

        var target = ArrangeCustomFolder("Só do cliente");
        target.SetExplicitRestriction(restricted.Id, Now);
        target.ApplyEffectiveRestriction(null, Now);

        // O remetente é promo.com: não pertence a cliente.com.
        _messages.GetParticipantsAsync(_message.Id, Arg.Any<CancellationToken>())
            .Returns([new MessageParticipant(AddressKind.From, EmailDomain.Parse("promo.com"))]);

        ArrangeRule(r => r.AddAction(RuleActionType.MoveToFolder, Now, targetFolderId: target.Id));

        await CreateHandler().HandleAsync(_message.Id);

        _message.FolderId.Should().Be(_inbox.Id, "a regra de domínio prevalece sobre a regra do usuário");

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.RuleActionSkipped),
            Arg.Any<CancellationToken>());
    }

    // ----- Copiar para pasta -----------------------------------------------------------

    [Fact]
    public async Task CopiarParaPasta_EnfileiraACopiaSemTirarAMensagemDoLugar()
    {
        var target = ArrangeCustomFolder("Arquivo do cliente");

        ArrangeRule(r => r.AddAction(RuleActionType.CopyToFolder, Now, targetFolderId: target.Id));

        await CreateHandler().HandleAsync(_message.Id);

        // Copiar não é mover: a mensagem continua na Caixa de Entrada e o servidor cria a
        // segunda instância quando a fila drenar.
        _message.FolderId.Should().Be(_inbox.Id);

        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.CopyMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CopiarParaPastaRestrita_MensagemIncompativel_ERecusadaEAuditada()
    {
        var restricted = DomainDirectory.Create(EmailDomain.Parse("cliente.com"), Now);
        _directories.GetByIdAsync(restricted.Id, Arg.Any<CancellationToken>()).Returns(restricted);

        var target = ArrangeCustomFolder("Só do cliente");
        target.SetExplicitRestriction(restricted.Id, Now);
        target.ApplyEffectiveRestriction(null, Now);

        _messages.GetParticipantsAsync(_message.Id, Arg.Any<CancellationToken>())
            .Returns([new MessageParticipant(AddressKind.From, EmailDomain.Parse("promo.com"))]);

        ArrangeRule(r => r.AddAction(RuleActionType.CopyToFolder, Now, targetFolderId: target.Id));

        await CreateHandler().HandleAsync(_message.Id);

        // Cópia incompatível é recusada em qualquer modo: não existe "desviar a cópia para
        // pendências", que criaria no servidor uma cópia que ninguém pediu.
        await _outbox.DidNotReceive().AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.CopyMessage),
            Arg.Any<CancellationToken>());

        await _audit.Received().RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.RuleActionSkipped),
            Arg.Any<CancellationToken>());
    }

    // ----- Encaminhamento automático ---------------------------------------------------

    [Fact]
    public async Task Encaminhar_ComCorpoDisponivel_EntregaOEnvioAFila()
    {
        ArrangeDownloadedBody();
        ArrangeOutboxFolder();

        ArrangeRule(r => r.AddAction(RuleActionType.Forward, Now, value: "destino@parceiro.com"));

        await CreateHandler().HandleAsync(_message.Id);

        // Encaminhar é entregar à fila, como qualquer envio (D-014): funciona offline dali
        // em diante e aparece na fila visível.
        await _outbox.Received(1).AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Encaminhar_EnderecoInvalido_NaoEnviaERegistraOMotivo()
    {
        ArrangeDownloadedBody();
        ArrangeOutboxFolder();

        ArrangeRule(r => r.AddAction(RuleActionType.Forward, Now, value: "isto-nao-e-um-endereco"));

        await CreateHandler().HandleAsync(_message.Id);

        await _outbox.DidNotReceive().AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());

        await _audit.Received().RecordAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventType.RuleActionSkipped
                && e.Severity == AuditSeverity.Warning),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Encaminhar_CorpoIndisponivel_NaoEncaminhaPelaMetade()
    {
        // Sem corpo baixado e sem UID válido não há o que buscar no servidor. Encaminhar
        // assim entregaria ao destinatário algo diferente do que o remetente mandou.
        ArrangeOutboxFolder();

        ArrangeRule(r => r.AddAction(RuleActionType.Forward, Now, value: "destino@parceiro.com"));

        await CreateHandler().HandleAsync(_message.Id);

        await _outbox.DidNotReceive().AddAsync(
            Arg.Is<OutboxOperation>(o => o.OperationType == OutboxOperationType.SendMessage),
            Arg.Any<CancellationToken>());

        await _audit.Received().RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.RuleActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CondicaoDeCorpo_BaixaOCorpoEAvaliaSobreOTextoCompleto()
    {
        // A prévia chega vazia da sincronização; o termo só existe no corpo.
        _message.SetRemoteIdentity(42, null, Now);
        _folders.GetByIdAsync(_inbox.Id, Arg.Any<CancellationToken>()).Returns(_inbox);

        _imap.FetchBodyAsync("INBOX", 42, Arg.Any<CancellationToken>())
            .Returns(new Abstractions.Mail.FetchedBody
            {
                TextBody = "Segue o orçamento revisado.",
            });

        _sanitizer.Sanitize(Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(new SanitizedHtmlResult(string.Empty, false, 0));

        ArrangeRule(r =>
        {
            r.AddCondition(RuleField.Body, RuleOperator.Contains, "orçamento", Now);
            r.AddAction(RuleActionType.MarkAsRead, Now);
        });

        var result = await CreateHandler().HandleAsync(_message.Id);

        await _imap.Received(1).FetchBodyAsync("INBOX", 42, Arg.Any<CancellationToken>());
        result.AppliedRuleCount.Should().Be(1, "a condição casou sobre o corpo baixado");
        _message.IsRead.Should().BeTrue();
    }

    /// <summary>Deixa a mensagem com corpo já baixado, dispensando ida ao servidor.</summary>
    private void ArrangeDownloadedBody()
    {
        var body = MessageBody.Create(_message.Id, Now);
        body.SetContent("<p>Conteúdo</p>", "Conteúdo", "<p>Conteúdo</p>", false, Now);
        _message.SetBody(body, Now);
    }

    private void ArrangeOutboxFolder()
    {
        var outbox = Folder.Create(_account.Id, "Caixa de Saída", FolderType.Outbox, Now, isLocalOnly: true);
        _folders.GetByTypeAsync(_account.Id, FolderType.Outbox, Arg.Any<CancellationToken>())
            .Returns(outbox);
        _folders.GetByIdAsync(outbox.Id, Arg.Any<CancellationToken>()).Returns(outbox);
    }
}
