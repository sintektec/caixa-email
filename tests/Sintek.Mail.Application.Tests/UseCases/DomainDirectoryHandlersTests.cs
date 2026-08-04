using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Domains;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a criação, alteração e remoção de Diretórios de Domínio — o agregado que decide a
/// qual domínio cada conta pertence.
/// </summary>
public class DomainDirectoryHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IOAuthProviderRegistry _oauth = Substitute.For<IOAuthProviderRegistry>();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public DomainDirectoryHandlersTests()
    {
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _accounts.ListByDomainAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Account>());

        _folders.ListByAccountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());
    }

    private CreateDomainDirectoryHandler CreateHandler() => new(
        _directories, _audit, _unitOfWork, _clock, NullLogger<CreateDomainDirectoryHandler>.Instance);

    private UpdateDomainDirectoryHandler UpdateHandler() => new(
        _directories, _audit, _unitOfWork, _clock, NullLogger<UpdateDomainDirectoryHandler>.Instance);

    private RemoveDomainDirectoryHandler RemoveHandler() => new(
        _directories,
        _accounts,
        _audit,
        _unitOfWork,
        new AccountRemover(
            _accounts,
            _folders,
            new AccountCredentialRevoker(_credentials, _oauth, NullLogger<AccountCredentialRevoker>.Instance),
            NullLogger<AccountRemover>.Instance),
        _clock,
        NullLogger<RemoveDomainDirectoryHandler>.Instance);

    // ----- Criação -------------------------------------------------------------------

    [Fact]
    public async Task CriarDiretorio_DominioValido_RegistraEAuditaCriacao()
    {
        var result = await CreateHandler().HandleAsync(new CreateDomainDirectoryCommand
        {
            DomainName = "SINTEK.com.br.",
            Description = " Matriz ",
            ValidationMode = DomainValidationMode.SenderOnly,
            InvalidEmailAction = InvalidEmailAction.MoveToPending,
        });

        result.Succeeded.Should().BeTrue();
        result.DomainDirectoryId.Should().NotBeNull();

        await _directories.Received(1).AddAsync(
            Arg.Is<DomainDirectory>(d =>
                d.DomainName.Value == "sintek.com.br"
                && d.Description == "Matriz"
                && d.ValidationMode == DomainValidationMode.SenderOnly
                && d.InvalidEmailAction == InvalidEmailAction.MoveToPending),
            Arg.Any<CancellationToken>());

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.DomainDirectoryCreated),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("contato@sintek.com.br")]
    [InlineData("sintek..com.br")]
    [InlineData("-sintek.com.br")]
    public async Task CriarDiretorio_DominioInvalido_RecusaComMotivo(string domainName)
    {
        var result = await CreateHandler().HandleAsync(new CreateDomainDirectoryCommand
        {
            DomainName = domainName,
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        await _directories.DidNotReceive().AddAsync(Arg.Any<DomainDirectory>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CriarDiretorio_DominioJaRepresentado_RecusaIndicandoODono()
    {
        // Dois diretórios para o mesmo domínio tornariam ambíguo a qual deles uma conta
        // pertence, e a ambiguidade acabaria decidida pela ordem da consulta.
        var existing = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

        _directories
            .GetByDomainAsync(Arg.Is<EmailDomain>(d => d.Value == "sintek.com.br"), Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await CreateHandler().HandleAsync(new CreateDomainDirectoryCommand
        {
            DomainName = "sintek.com.br",
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("sintek.com.br");
    }

    [Fact]
    public async Task CriarDiretorio_DominioAdicionalDeOutroDiretorio_Recusa()
    {
        var owner = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

        _directories
            .GetByDomainAsync(Arg.Is<EmailDomain>(d => d.Value == "sintek.net.br"), Arg.Any<CancellationToken>())
            .Returns(owner);

        var result = await CreateHandler().HandleAsync(new CreateDomainDirectoryCommand
        {
            DomainName = "outraempresa.com.br",
            Aliases = ["sintek.net.br"],
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("sintek.net.br");
    }

    [Fact]
    public async Task CriarDiretorio_DominioAdicionalIgualAoPrincipal_IgnoraDuplicata()
    {
        var result = await CreateHandler().HandleAsync(new CreateDomainDirectoryCommand
        {
            DomainName = "sintek.com.br",
            Aliases = ["SINTEK.COM.BR", "sintek.net.br"],
        });

        result.Succeeded.Should().BeTrue();

        await _directories.Received(1).AddAsync(
            Arg.Is<DomainDirectory>(d => d.Aliases.Count == 1),
            Arg.Any<CancellationToken>());
    }

    // ----- Alteração -----------------------------------------------------------------

    [Fact]
    public async Task AlterarDiretorio_ListaDeAdicionais_EOEstadoFinalNaoUmIncremento()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        directory.AddAlias(EmailDomain.Parse("antigo.com.br"), Now);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        var result = await UpdateHandler().HandleAsync(new UpdateDomainDirectoryCommand
        {
            DomainDirectoryId = directory.Id,
            ValidationMode = DomainValidationMode.AnyParticipant,
            InvalidEmailAction = InvalidEmailAction.Block,
            Aliases = ["novo.com.br"],
        });

        result.Succeeded.Should().BeTrue();
        directory.Aliases.Select(a => a.DomainName.Value).Should().BeEquivalentTo(["novo.com.br"]);
    }

    [Fact]
    public async Task AlterarDiretorio_RegrasNovas_SaoAplicadasEAuditadas()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        await UpdateHandler().HandleAsync(new UpdateDomainDirectoryCommand
        {
            DomainDirectoryId = directory.Id,
            ValidationMode = DomainValidationMode.SenderAndRecipient,
            InvalidEmailAction = InvalidEmailAction.WarnAndConfirm,
            AllowSubdomains = true,
            IsActive = false,
        });

        directory.ValidationMode.Should().Be(DomainValidationMode.SenderAndRecipient);
        directory.InvalidEmailAction.Should().Be(InvalidEmailAction.WarnAndConfirm);
        directory.AllowSubdomains.Should().BeTrue();
        directory.IsActive.Should().BeFalse();

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.DomainDirectoryUpdated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlterarDiretorio_AdicionalDeOutroDono_RecusaSemAlterarNada()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var other = DomainDirectory.Create(EmailDomain.Parse("outra.com.br"), Now);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);
        _directories
            .GetByDomainAsync(Arg.Is<EmailDomain>(d => d.Value == "outra.com.br"), Arg.Any<CancellationToken>())
            .Returns(other);

        var result = await UpdateHandler().HandleAsync(new UpdateDomainDirectoryCommand
        {
            DomainDirectoryId = directory.Id,
            ValidationMode = DomainValidationMode.SenderOnly,
            InvalidEmailAction = InvalidEmailAction.Block,
            Aliases = ["outra.com.br"],
        });

        result.Succeeded.Should().BeFalse();
        directory.ValidationMode.Should().Be(DomainValidationMode.AnyParticipant, "nada pode ser aplicado");
    }

    // ----- Remoção -------------------------------------------------------------------

    [Fact]
    public async Task RemoverDiretorio_ComContasESemConfirmacao_RecusaEExplicaOQueSeriaPerdido()
    {
        var (directory, _) = ArrangeDirectoryWithAccount(messageCount: 42);

        var result = await RemoveHandler().HandleAsync(directory.Id, confirmed: false);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("42");
        result.Impact!.AccountCount.Should().Be(1);

        _directories.DidNotReceive().Remove(Arg.Any<DomainDirectory>());
    }

    [Fact]
    public async Task RemoverDiretorio_Confirmado_ApagaContasCredenciaisEDiretorio()
    {
        var (directory, account) = ArrangeDirectoryWithAccount(messageCount: 3);
        await _credentials.SetSecretAsync(account.CredentialKey, FakeSecret.For("conta"));

        var result = await RemoveHandler().HandleAsync(directory.Id, confirmed: true);

        result.Succeeded.Should().BeTrue();

        _accounts.Received(1).Remove(account);
        _directories.Received(1).Remove(directory);
        _credentials.Keys.Should().NotContain(account.CredentialKey);

        await _audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventType.DomainDirectoryDeleted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoverDiretorio_SemContas_DispensaConfirmacao()
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);

        var result = await RemoveHandler().HandleAsync(directory.Id, confirmed: false);

        result.Succeeded.Should().BeTrue();
        _directories.Received(1).Remove(directory);
    }

    [Fact]
    public async Task RemoverDiretorio_Inexistente_RecusaComMotivo()
    {
        var result = await RemoveHandler().HandleAsync(Guid.CreateVersion7(), confirmed: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    private (DomainDirectory Directory, Account Account) ArrangeDirectoryWithAccount(int messageCount)
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        var inbox = Folder.Create(account.Id, "Caixa de Entrada", FolderType.Inbox, Now);

        _directories.GetByIdAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(directory);
        _accounts.ListByDomainAsync(directory.Id, Arg.Any<CancellationToken>()).Returns(new[] { account });
        _folders.ListByAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(new[] { inbox });
        _folders.CountMessagesAsync(inbox.Id, Arg.Any<CancellationToken>()).Returns(messageCount);

        return (directory, account);
    }
}
