using Moq;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Domain.Entities;
using Xunit;

namespace Sintek.Mail.ApplicationTests;

public class CreateDomainDirectoryHandlerTests
{
    private readonly Mock<IMailRepository> _repositoryMock;
    private readonly CreateDomainDirectoryHandler _handler;

    public CreateDomainDirectoryHandlerTests()
    {
        _repositoryMock = new Mock<IMailRepository>();
        _handler = new CreateDomainDirectoryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task HandleAsync_ValidInput_ReturnsDto()
    {
        _repositoryMock.Setup(r => r.GetDomainByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainDirectory?)null);
        _repositoryMock.Setup(r => r.AddDomainAsync(It.IsAny<DomainDirectory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repositoryMock.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var command = new CreateDomainDirectoryCommand("example.com", "Test domain");
        var result = await _handler.HandleAsync(command);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("example.com", result.DomainName);
    }

    [Fact]
    public async Task HandleAsync_DuplicateDomain_Throws()
    {
        var existing = new DomainDirectory("example.com");
        _repositoryMock.Setup(r => r.GetDomainByNameAsync("example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var command = new CreateDomainDirectoryCommand("example.com", "Test domain");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.HandleAsync(command));
    }

    // "invalid" saiu daqui: por D-008 o Diretorio aceita qualquer rotulo, entao
    // esse nome e valido. So vazio/branco continua sendo recusado.
    // ThrowsAnyAsync porque InvalidEmailDomainException deriva de
    // ArgumentException (D-007) e ThrowsAsync exige tipo exato.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_DominioVazio_Throws(string domainName)
    {
        var command = new CreateDomainDirectoryCommand(domainName, "Test domain");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _handler.HandleAsync(command));
    }

    [Theory]
    [InlineData("intranet")]
    [InlineData("localhost")]
    public async Task HandleAsync_RotuloSemPonto_ECriado(string domainName)
    {
        _repositoryMock.Setup(r => r.GetDomainByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainDirectory?)null);
        _repositoryMock.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.HandleAsync(new CreateDomainDirectoryCommand(domainName));

        Assert.Equal(domainName, result.DomainName);
    }
}
