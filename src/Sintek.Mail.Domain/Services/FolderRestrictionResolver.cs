using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.Services;

/// <summary>
/// Resolve a herança de restrição de domínio ao longo da árvore de pastas.
/// </summary>
/// <remarks>
/// A especificação determina que "as subpastas de uma pasta restrita por domínio devem
/// herdar automaticamente as regras do Diretório de Domínio pai" e que uma pasta nunca
/// pode responder a dois diretórios. Este resolvedor é quem faz as duas coisas valerem.
///
/// Ele opera sobre pastas já em memória: quem chama carrega a subárvore afetada e aplica
/// o resultado. Manter a travessia aqui, e não em uma consulta recursiva, permite testar
/// a regra de herança sem banco algum.
/// </remarks>
public static class FolderRestrictionResolver
{
    /// <summary>
    /// Recalcula a restrição efetiva de <paramref name="root"/> e de toda a sua subárvore.
    /// </summary>
    /// <param name="root">Pasta a partir da qual recalcular.</param>
    /// <param name="childrenOf">
    /// Devolve as subpastas diretas de uma pasta. Injetado para que a persistência possa
    /// alimentar a travessia a partir de uma única consulta por conta.
    /// </param>
    /// <param name="inheritedFromAncestor">
    /// Restrição vinda de um ancestral de <paramref name="root"/>. Nulo quando
    /// <paramref name="root"/> é a raiz da conta ou nenhum ancestral impõe restrição.
    /// </param>
    /// <param name="now">Instante da alteração.</param>
    /// <returns>As pastas cuja restrição efetiva mudou.</returns>
    /// <exception cref="InvalidFolderHierarchyException">
    /// Uma pasta da subárvore tem vínculo explícito com um diretório diferente do herdado.
    /// </exception>
    public static IReadOnlyList<Folder> Resolve(
        Folder root,
        Func<Folder, IEnumerable<Folder>> childrenOf,
        Guid? inheritedFromAncestor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(childrenOf);

        var changed = new List<Folder>();

        // Travessia iterativa: uma hierarquia de pastas criada por sincronização pode
        // ficar surpreendentemente profunda, e recursão aqui arriscaria estourar a pilha.
        var stack = new Stack<(Folder Folder, Guid? Inherited)>();
        stack.Push((root, inheritedFromAncestor));

        // Guarda contra ciclos: a árvore vem do banco e um dado corrompido não pode
        // transformar esta travessia em laço infinito.
        var visited = new HashSet<Guid>();

        while (stack.Count > 0)
        {
            var (folder, inherited) = stack.Pop();

            if (!visited.Add(folder.Id))
            {
                throw new InvalidFolderHierarchyException(
                    $"A hierarquia de pastas contém um ciclo em '{folder.DisplayName}'.");
            }

            var previous = folder.EffectiveRestrictionDomainDirectoryId;
            folder.ApplyEffectiveRestriction(inherited, now);

            if (previous != folder.EffectiveRestrictionDomainDirectoryId)
            {
                changed.Add(folder);
            }

            // O que os filhos herdam é a restrição EFETIVA desta pasta, não a herdada:
            // assim uma pasta com vínculo próprio passa o dela adiante.
            var inheritedByChildren = folder.EffectiveRestrictionDomainDirectoryId;

            foreach (var child in childrenOf(folder))
            {
                stack.Push((child, inheritedByChildren));
            }
        }

        return changed;
    }

    /// <summary>
    /// Sobe a árvore a partir de <paramref name="folder"/> e devolve a restrição imposta
    /// pelo ancestral mais próximo que tenha uma.
    /// </summary>
    /// <remarks>
    /// Usado ao criar uma pasta nova: ela precisa nascer já herdando a restrição do ramo
    /// em que foi criada, sem esperar por um recálculo global.
    /// </remarks>
    public static Guid? FindInheritedRestriction(Folder folder, Func<Guid, Folder?> folderById)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(folderById);

        var visited = new HashSet<Guid> { folder.Id };
        var parentId = folder.ParentFolderId;

        while (parentId.HasValue)
        {
            if (!visited.Add(parentId.Value))
            {
                throw new InvalidFolderHierarchyException(
                    "A hierarquia de pastas contém um ciclo entre as pastas ancestrais.");
            }

            var parent = folderById(parentId.Value);
            if (parent is null)
            {
                break;
            }

            if (parent.RestrictedToDomainDirectoryId.HasValue)
            {
                return parent.RestrictedToDomainDirectoryId;
            }

            parentId = parent.ParentFolderId;
        }

        return null;
    }
}
