using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;

namespace Sintek.Mail.Application.UseCases.Organization;

/// <summary>Resultado de uma reordenação.</summary>
/// <param name="Succeeded">Se a nova ordem foi gravada.</param>
/// <param name="ErrorMessage">Motivo da recusa, quando houver.</param>
public readonly record struct ReorderResult(bool Succeeded, string? ErrorMessage = null)
{
    /// <summary>Reordenação concluída.</summary>
    public static ReorderResult Success() => new(true);

    /// <summary>Reordenação recusada.</summary>
    public static ReorderResult Failure(string message) => new(false, message);
}

/// <summary>
/// Grava a ordem manual dos Diretórios de Domínio e das contas na árvore de navegação.
/// </summary>
/// <remarks>
/// <para>
/// A operação recebe a <b>lista inteira já na ordem desejada</b>, não "mova este item para
/// a posição N". Reposicionar um item sozinho obriga quem chama a decidir o que acontece com
/// os vizinhos, e duas telas fariam essa conta de jeitos diferentes; a lista completa não tem
/// essa ambiguidade. É também o formato que o arrastar e soltar produz naturalmente — a
/// árvore já sabe a ordem final quando o item é solto.
/// </para>
/// <para>
/// <b>A lista precisa conter exatamente os itens existentes.</b> Uma lista parcial deixaria os
/// ausentes com a posição antiga, embaralhando o resultado em vez de reordená-lo; uma lista com
/// identificador desconhecido é sinal de que a tela está olhando um estado que não existe mais.
/// Nos dois casos a recusa é preferível a gravar uma ordem que ninguém pediu.
/// </para>
/// </remarks>
public sealed class ReorderNavigationHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReorderNavigationHandler> _logger;

    public ReorderNavigationHandler(
        IDomainDirectoryRepository directories,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ReorderNavigationHandler> logger)
    {
        _directories = directories;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Reordena os Diretórios de Domínio.</summary>
    public async Task<ReorderResult> ReorderDirectoriesAsync(
        IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        var directories = await _directories.ListAsync(cancellationToken).ConfigureAwait(false);

        if (Validate(orderedIds, directories.Select(d => d.Id)) is { } error)
        {
            return ReorderResult.Failure(error);
        }

        var byId = directories.ToDictionary(d => d.Id);
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async _ =>
        {
            for (var position = 0; position < orderedIds.Count; position++)
            {
                byId[orderedIds[position]].SetSortOrder(position, now);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Ordem dos Diretórios de Domínio atualizada.");
        return ReorderResult.Success();
    }

    /// <summary>Reordena as contas de um Diretório de Domínio.</summary>
    /// <remarks>
    /// A ordem é relativa ao diretório. Uma conta de outro diretório na lista seria pedido de
    /// mudança de diretório disfarçado de reordenação — e mudar de diretório passa pela regra
    /// de pertinência, que não vive aqui.
    /// </remarks>
    public async Task<ReorderResult> ReorderAccountsAsync(
        Guid domainDirectoryId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        var accounts = await _accounts
            .ListByDomainAsync(domainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        if (Validate(orderedIds, accounts.Select(a => a.Id)) is { } error)
        {
            return ReorderResult.Failure(error);
        }

        var byId = accounts.ToDictionary(a => a.Id);
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async _ =>
        {
            for (var position = 0; position < orderedIds.Count; position++)
            {
                byId[orderedIds[position]].SetSortOrder(position, now);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Ordem das contas de um Diretório de Domínio atualizada.");
        return ReorderResult.Success();
    }

    /// <summary>
    /// Confere que a lista recebida é uma permutação exata do que existe.
    /// </summary>
    private static string? Validate(IReadOnlyList<Guid> orderedIds, IEnumerable<Guid> existing)
    {
        var known = existing.ToHashSet();

        if (orderedIds.Distinct().Count() != orderedIds.Count)
        {
            return "A nova ordem repete um item.";
        }

        if (orderedIds.Count != known.Count || !orderedIds.All(known.Contains))
        {
            // A tela está olhando um estado que não existe mais — outra janela criou ou
            // removeu algo. Gravar assim deixaria os ausentes com a posição antiga.
            return "A lista mudou enquanto era reordenada. Atualize a árvore e tente de novo.";
        }

        return null;
    }
}
