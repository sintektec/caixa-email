using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Organization;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a ordem manual dos Diretórios de Domínio e das contas na árvore.
/// </summary>
/// <remarks>
/// O caso que mais importa é a <b>recusa de lista incompleta</b>. Gravar uma lista parcial
/// não deixaria a ordem pela metade: deixaria os ausentes com a posição antiga, embaralhando
/// o resultado — e o usuário veria a árvore reorganizada de um jeito que ele não pediu.
/// </remarks>
public class ReorderNavigationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public ReorderNavigationHandlerTests()
        => _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

    private ReorderNavigationHandler Handler() => new(
        _directories, _accounts, _unitOfWork, new FakeTimeProvider(Now),
        NullLogger<ReorderNavigationHandler>.Instance);

    private static DomainDirectory Directory(string domain)
        => DomainDirectory.Create(EmailDomain.Parse(domain), Now, domain);

    private static Account AccountIn(Guid directoryId, string address)
        => Account.Create(directoryId, EmailAddress.Parse(address), address, Now);

    private IReadOnlyList<DomainDirectory> ArrangeDirectories(params string[] domains)
    {
        var directories = domains.Select(Directory).ToList();

        for (var i = 0; i < directories.Count; i++)
        {
            directories[i].SetSortOrder(i, Now);
        }

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns(directories);
        return directories;
    }

    [Fact]
    public async Task ReordenarDiretorios_NovaOrdem_GravaAsPosicoesNaSequenciaRecebida()
    {
        var directories = ArrangeDirectories("a.com", "b.com", "c.com");
        var invertida = new[] { directories[2].Id, directories[0].Id, directories[1].Id };

        var result = await Handler().ReorderDirectoriesAsync(invertida);

        result.Succeeded.Should().BeTrue();
        directories[2].SortOrder.Should().Be(0);
        directories[0].SortOrder.Should().Be(1);
        directories[1].SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task ReordenarDiretorios_GravaDentroDeUmaTransacaoSo()
    {
        var directories = ArrangeDirectories("a.com", "b.com");

        await Handler().ReorderDirectoriesAsync([directories[1].Id, directories[0].Id]);

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Lista parcial não deixa a ordem pela metade: deixa os ausentes com a posição antiga,
    /// que é embaralhar em vez de reordenar.
    /// </summary>
    [Fact]
    public async Task ReordenarDiretorios_ListaIncompleta_RecusaSemGravar()
    {
        var directories = ArrangeDirectories("a.com", "b.com", "c.com");

        var result = await Handler().ReorderDirectoriesAsync([directories[1].Id, directories[0].Id]);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("mudou");
        directories[0].SortOrder.Should().Be(0, "nada pode ter sido gravado");

        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Identificador desconhecido é sinal de que a tela olha um estado que não existe mais —
    /// outra janela criou ou removeu um diretório.
    /// </summary>
    [Fact]
    public async Task ReordenarDiretorios_ComIdentificadorDesconhecido_Recusa()
    {
        var directories = ArrangeDirectories("a.com", "b.com");

        var result = await Handler()
            .ReorderDirectoriesAsync([directories[0].Id, Guid.CreateVersion7()]);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ReordenarDiretorios_ComItemRepetido_Recusa()
    {
        var directories = ArrangeDirectories("a.com", "b.com");

        var result = await Handler()
            .ReorderDirectoriesAsync([directories[0].Id, directories[0].Id]);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("repete");
    }

    [Fact]
    public async Task ReordenarContas_NovaOrdem_GravaAsPosicoesDentroDoDiretorio()
    {
        var directoryId = Guid.CreateVersion7();
        var contas = new[]
        {
            AccountIn(directoryId, "um@a.com"),
            AccountIn(directoryId, "dois@a.com"),
        };

        _accounts.ListByDomainAsync(directoryId, Arg.Any<CancellationToken>()).Returns(contas);

        var result = await Handler()
            .ReorderAccountsAsync(directoryId, [contas[1].Id, contas[0].Id]);

        result.Succeeded.Should().BeTrue();
        contas[1].SortOrder.Should().Be(0);
        contas[0].SortOrder.Should().Be(1);
    }

    /// <summary>
    /// Uma conta de outro diretório na lista seria pedido de mudança de diretório disfarçado
    /// de reordenação — e mudar de diretório passa pela regra de pertinência, não por aqui.
    /// </summary>
    [Fact]
    public async Task ReordenarContas_ComContaDeOutroDiretorio_Recusa()
    {
        var directoryId = Guid.CreateVersion7();
        var propria = AccountIn(directoryId, "um@a.com");
        var alheia = AccountIn(Guid.CreateVersion7(), "outro@b.com");

        _accounts.ListByDomainAsync(directoryId, Arg.Any<CancellationToken>()).Returns(new[] { propria });

        var result = await Handler().ReorderAccountsAsync(directoryId, [alheia.Id, propria.Id]);

        result.Succeeded.Should().BeFalse();
        propria.SortOrder.Should().Be(0);
    }
}
