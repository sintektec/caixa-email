using Moq;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Xunit;

namespace Sintek.Mail.ApplicationTests;

public class AddAccountHandlerTests
{
    private readonly Mock<IMailRepository> _repositoryMock;
    private readonly AddAccountHandler _handler;

    public AddAccountHandlerTests()
    {
        _repositoryMock = new Mock<IMailRepository>();
        _handler = new AddAccountHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task HandleAsync_ValidInput_ReturnsDto()
    {
        var domain = new DomainDirectory("example.com");
        _repositoryMock.Setup(r => r.GetDomainByIdAsync(domain.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);
        _repositoryMock.Setup(r => r.AddAccountAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repositoryMock.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var command = new AddAccountCommand(
            domain.Id, "user@example.com", "User",
            "imap.example.com", 993, "smtp.example.com", 465,
            true, SecurityProtocol.Ssl, SecurityProtocol.Ssl,
            AuthenticationType.Basic);
        var result = await _handler.HandleAsync(command);

        // Parte local em maiuscula por D-009: o handler grava
        // EmailAddress.FullAddress, ja normalizado.
        Assert.Equal("USER@example.com", result.EmailAddress);
    }

    [Fact]
    public async Task HandleAsync_DomainNotFound_Throws()
    {
        _repositoryMock.Setup(r => r.GetDomainByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainDirectory?)null);

        var command = new AddAccountCommand(
            Guid.NewGuid(), "user@example.com", "User",
            "imap.example.com", 993, "smtp.example.com", 465,
            true, SecurityProtocol.Ssl, SecurityProtocol.Ssl,
            AuthenticationType.Basic);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.HandleAsync(command));
    }

    [Fact]
    public async Task HandleAsync_InvalidEmailForDomain_Throws()
    {
        var domain = new DomainDirectory("example.com");
        _repositoryMock.Setup(r => r.GetDomainByIdAsync(domain.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);

        var command = new AddAccountCommand(
            domain.Id, "user@other.com", "User",
            "imap.other.com", 993, "smtp.other.com", 465,
            true, SecurityProtocol.Ssl, SecurityProtocol.Ssl,
            AuthenticationType.Basic);
        await Assert.ThrowsAsync<DomainMismatchException>(() => _handler.HandleAsync(command));
    }
}
