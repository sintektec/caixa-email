using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre a seção 5.4 da especificação: herança de regras pelas subpastas e a proibição de
/// vincular uma pasta a mais de um Diretório de Domínio.
/// </summary>
public class FolderRestrictionResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private sealed class Tree
    {
        private readonly Dictionary<Guid, List<Folder>> _children = [];
        private readonly Dictionary<Guid, Folder> _byId = [];

        public Folder Add(string name, Folder? parent = null)
        {
            var folder = Folder.Create(AccountId, name, FolderType.Custom, Now, parent?.Id);
            if (parent is not null)
            {
                folder.Reparent(parent, Now);
                if (!_children.TryGetValue(parent.Id, out var siblings))
                {
                    siblings = [];
                    _children[parent.Id] = siblings;
                }

                siblings.Add(folder);
            }

            _byId[folder.Id] = folder;
            return folder;
        }

        public IEnumerable<Folder> ChildrenOf(Folder folder)
            => _children.TryGetValue(folder.Id, out var children) ? children : [];

        public Folder? ById(Guid id) => _byId.GetValueOrDefault(id);
    }

    [Fact]
    public void Resolve_PropagaRestricaoParaTodaASubarvore()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);
        var grandChild = tree.Add("Contratos", child);

        var domainId = Guid.CreateVersion7();
        root.SetExplicitRestriction(domainId, Now);

        FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, inheritedFromAncestor: null, Now);

        root.EffectiveRestrictionDomainDirectoryId.Should().Be(domainId);
        child.EffectiveRestrictionDomainDirectoryId.Should().Be(domainId);
        grandChild.EffectiveRestrictionDomainDirectoryId.Should().Be(domainId);
    }

    [Fact]
    public void Resolve_MarcaSubpastasComoHerdandoARestricao()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);

        root.SetExplicitRestriction(Guid.CreateVersion7(), Now);
        FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        root.IsRestrictionInherited.Should().BeFalse();
        child.IsRestrictionInherited.Should().BeTrue();
        child.IsDomainRestricted.Should().BeTrue();
    }

    [Fact]
    public void Resolve_RemoveARestricaoDaSubarvoreQuandoOVinculoDoPaiSai()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);

        root.SetExplicitRestriction(Guid.CreateVersion7(), Now);
        FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        root.SetExplicitRestriction(null, Now);
        FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        root.IsDomainRestricted.Should().BeFalse();
        child.IsDomainRestricted.Should().BeFalse();
    }

    [Fact]
    public void Resolve_Recusa_PastaVinculadaADoisDiretorios()
    {
        // A especificação é explícita: "Não permitir que uma pasta seja vinculada
        // simultaneamente a mais de um Diretório de Domínio."
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);

        root.SetExplicitRestriction(Guid.CreateVersion7(), Now);
        child.SetExplicitRestriction(Guid.CreateVersion7(), Now);

        var act = () => FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        act.Should().Throw<InvalidFolderHierarchyException>()
            .WithMessage("*um único Diretório de Domínio*");
    }

    [Fact]
    public void Resolve_PermitePastaFilhaRepetirOMesmoDiretorioDoPai()
    {
        // Repetir o mesmo diretório é redundante, mas não é conflito — e acontece quando
        // o usuário aplica a restrição a uma subpasta antes de aplicá-la ao ramo inteiro.
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);

        var domainId = Guid.CreateVersion7();
        root.SetExplicitRestriction(domainId, Now);
        child.SetExplicitRestriction(domainId, Now);

        var act = () => FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        act.Should().NotThrow();
        child.EffectiveRestrictionDomainDirectoryId.Should().Be(domainId);
    }

    [Fact]
    public void Resolve_DevolveApenasAsPastasQueMudaram()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        tree.Add("2026", root);

        root.SetExplicitRestriction(Guid.CreateVersion7(), Now);
        var firstPass = FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);
        var secondPass = FolderRestrictionResolver.Resolve(root, tree.ChildrenOf, null, Now);

        firstPass.Should().HaveCount(2);
        secondPass.Should().BeEmpty();
    }

    [Fact]
    public void FindInheritedRestriction_EncontraOAncestralMaisProximoComVinculo()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var middle = tree.Add("2026", root);
        var leaf = tree.Add("Contratos", middle);

        var domainId = Guid.CreateVersion7();
        root.SetExplicitRestriction(domainId, Now);

        FolderRestrictionResolver.FindInheritedRestriction(leaf, tree.ById).Should().Be(domainId);
    }

    [Fact]
    public void FindInheritedRestriction_DevolveNulo_QuandoNenhumAncestralRestringe()
    {
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var leaf = tree.Add("2026", root);

        FolderRestrictionResolver.FindInheritedRestriction(leaf, tree.ById).Should().BeNull();
    }

    [Fact]
    public void Reparent_Recusa_MoverPastaParaDentroDeSiMesma()
    {
        var tree = new Tree();
        var folder = tree.Add("Clientes");

        var act = () => folder.Reparent(folder, Now);

        act.Should().Throw<InvalidFolderHierarchyException>();
    }

    [Fact]
    public void Reparent_Recusa_MoverPastaParaDentroDeUmaSubpasta()
    {
        // Sem esta guarda o ramo inteiro se desconectaria da árvore e as pastas
        // sumiriam da navegação sem serem excluídas.
        var tree = new Tree();
        var root = tree.Add("Clientes");
        var child = tree.Add("2026", root);

        var act = () => root.Reparent(child, Now);

        act.Should().Throw<InvalidFolderHierarchyException>();
    }
}
