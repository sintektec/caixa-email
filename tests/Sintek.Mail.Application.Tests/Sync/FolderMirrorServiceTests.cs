using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Tests.Sync;

/// <summary>
/// Cobre o espelhamento da árvore de pastas do servidor. O foco está no que o serviço
/// <b>não</b> pode fazer: apagar dados locais por causa de uma listagem incompleta.
/// </summary>
public class FolderMirrorServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private FolderMirrorService CreateService() => new(
        _folders, _unitOfWork, new FakeTimeProvider(Now), NullLogger<FolderMirrorService>.Instance);

    private void ArrangeLocal(params Folder[] folders)
        => _folders.ListByAccountAsync(AccountId, Arg.Any<CancellationToken>()).Returns(folders);

    private static RemoteFolder Remote(string path, string name, char delimiter = '/', FolderType type = FolderType.Custom)
        => new(path, name, delimiter, type, IsSubscribed: true);

    [Fact]
    public async Task Espelhar_PastaNovaNoServidor_ECriadaLocalmente()
    {
        ArrangeLocal();

        var result = await CreateService().MirrorAsync(AccountId, [Remote("Clientes", "Clientes")]);

        result.Created.Should().Be(1);

        await _folders.Received(1).AddAsync(
            Arg.Is<Folder>(f => f.RemotePath == "Clientes" && f.AccountId == AccountId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Espelhar_Subpasta_ELigadaAPastaMae()
    {
        ArrangeLocal();

        var created = new List<Folder>();
        await _folders.AddAsync(Arg.Do<Folder>(created.Add), Arg.Any<CancellationToken>());

        await CreateService().MirrorAsync(AccountId, [
            Remote("Clientes/2026", "2026"),
            Remote("Clientes", "Clientes"),
        ]);

        var parent = created.Single(f => f.RemotePath == "Clientes");
        var child = created.Single(f => f.RemotePath == "Clientes/2026");

        child.ParentFolderId.Should().Be(parent.Id, "a pasta-mãe precisa existir antes da subpasta");
    }

    [Fact]
    public async Task Espelhar_SeparadorDiferenteDeBarra_AindaMontaAHierarquia()
    {
        // Servidores usam '/', '.' e '\'. Assumir um deles chaparia a árvore justamente
        // nos que usam outro, sem erro visível.
        ArrangeLocal();

        var created = new List<Folder>();
        await _folders.AddAsync(Arg.Do<Folder>(created.Add), Arg.Any<CancellationToken>());

        await CreateService().MirrorAsync(AccountId, [
            Remote("INBOX", "INBOX", '.'),
            Remote("INBOX.Clientes", "Clientes", '.'),
        ]);

        var child = created.Single(f => f.RemotePath == "INBOX.Clientes");
        child.ParentFolderId.Should().Be(created.Single(f => f.RemotePath == "INBOX").Id);
    }

    [Fact]
    public async Task Espelhar_PastaLocal_NuncaEDesligada()
    {
        // Pendências e Caixa de Saída não existem no IMAP: nenhuma listagem as menciona, e
        // tratá-las como "sumidas" as desligaria a cada sincronização.
        var pending = Folder.Create(AccountId, "Pendências", FolderType.Pending, Now, isLocalOnly: true);
        ArrangeLocal(pending);

        var result = await CreateService().MirrorAsync(AccountId, [Remote("INBOX", "INBOX")]);

        result.Disabled.Should().Be(0);
        pending.IsLocalOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Espelhar_PastaSumiuDoServidor_DesligaSincronizacaoSemApagar()
    {
        // Uma resposta de LIST incompleta é indistinguível de uma exclusão real, e a
        // diferença entre as duas hipóteses é a caixa postal do usuário.
        var archived = Folder.Create(AccountId, "Arquivo 2019", FolderType.Custom, Now, remotePath: "Arquivo 2019");
        ArrangeLocal(archived);

        var result = await CreateService().MirrorAsync(AccountId, [Remote("INBOX", "INBOX")]);

        result.Disabled.Should().Be(1);
        archived.SyncEnabled.Should().BeFalse();
        _folders.DidNotReceive().Remove(Arg.Any<Folder>());
    }

    [Fact]
    public async Task Espelhar_PastaRenomeadaNoServidor_AtualizaONomeLocal()
    {
        var folder = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        ArrangeLocal(folder);

        var result = await CreateService().MirrorAsync(AccountId, [Remote("Clientes", "Clientes VIP")]);

        result.Renamed.Should().Be(1);
        folder.Name.Should().Be("Clientes VIP");
    }

    [Fact]
    public async Task Espelhar_PastaJaConhecida_NaoECriadaDeNovo()
    {
        var folder = Folder.Create(AccountId, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX");
        ArrangeLocal(folder);

        var result = await CreateService().MirrorAsync(AccountId, [Remote("INBOX", "INBOX", '/', FolderType.Inbox)]);

        result.Created.Should().Be(0);
        await _folders.DidNotReceive().AddAsync(Arg.Any<Folder>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RecalcularHeranca_SubpastaDentroDeRamoRestrito_HerdaARestricao()
    {
        // Sem o recálculo, uma subpasta criada pelo servidor dentro de um ramo restrito
        // nasceria livre — e aceitaria exatamente as mensagens que o ramo existe para recusar.
        var directoryId = Guid.CreateVersion7();

        var root = Folder.Create(AccountId, "Clientes", FolderType.Custom, Now, remotePath: "Clientes");
        root.SetExplicitRestriction(directoryId, Now);

        var child = Folder.Create(
            AccountId, "2026", FolderType.Custom, Now, parentFolderId: root.Id, remotePath: "Clientes/2026");

        FolderMirrorService.ReapplyInheritance([root, child], Now);

        child.EffectiveRestrictionDomainDirectoryId.Should().Be(directoryId);
        child.IsRestrictionInherited.Should().BeTrue();
    }
}
