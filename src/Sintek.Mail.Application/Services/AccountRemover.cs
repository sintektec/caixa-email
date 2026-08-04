using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.Services;

/// <summary>Contagem do que a remoção de uma conta levaria junto.</summary>
/// <param name="FolderCount">Pastas locais da conta.</param>
/// <param name="MessageCount">Mensagens armazenadas localmente.</param>
public readonly record struct AccountRemovalImpact(int FolderCount, int MessageCount);

/// <summary>
/// Apaga uma conta e tudo que pertence a ela.
/// </summary>
/// <remarks>
/// Existe separado dos casos de uso porque duas operações removem contas: a remoção direta
/// e a remoção do Diretório de Domínio que as contém. A auditoria fica com quem chama —
/// cada uma registra um evento diferente —, mas a mecânica da exclusão é a mesma e precisa
/// ser.
/// </remarks>
public sealed class AccountRemover
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly AccountCredentialRevoker _revoker;
    private readonly ILogger<AccountRemover> _logger;

    public AccountRemover(
        IAccountRepository accounts,
        IFolderRepository folders,
        AccountCredentialRevoker revoker,
        ILogger<AccountRemover> logger)
    {
        _accounts = accounts;
        _folders = folders;
        _revoker = revoker;
        _logger = logger;
    }

    /// <summary>Mede o que seria perdido, sem alterar nada.</summary>
    public async Task<AccountRemovalImpact> MeasureAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var folders = await _folders.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        var messageCount = 0;

        foreach (var folder in folders)
        {
            messageCount += await _folders.CountMessagesAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        }

        return new AccountRemovalImpact(folders.Count, messageCount);
    }

    /// <summary>Revoga as credenciais e apaga a conta com suas pastas e mensagens.</summary>
    public async Task RemoveAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await _revoker.RevokeAsync(account, cancellationToken).ConfigureAwait(false);

        var folders = await _folders.ListByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);

        // Da folha para a raiz. A relação de pasta-mãe é Restrict, não Cascade — apagar uma
        // pasta com subpastas precisa ser decisão explícita —, e remover na ordem errada
        // esbarraria nessa restrição de chave estrangeira.
        foreach (var folder in OrderByDepthDescending(folders))
        {
            _folders.Remove(folder);
        }

        _accounts.Remove(account);

        _logger.LogInformation(
            "Conta {AccountId} removida com {FolderCount} pasta(s).", account.Id, folders.Count);
    }

    /// <summary>Ordena as pastas da mais profunda para a mais rasa.</summary>
    /// <remarks>
    /// A profundidade é calculada com proteção contra ciclo: um <c>ParentFolderId</c>
    /// apontando de volta para dentro da própria cadeia — dado corrompido, mas possível —
    /// travaria a remoção em laço infinito em vez de apenas falhar.
    /// </remarks>
    internal static IEnumerable<Folder> OrderByDepthDescending(IReadOnlyList<Folder> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);
        var depths = new Dictionary<Guid, int>(folders.Count);

        foreach (var folder in folders)
        {
            var depth = 0;
            var visited = new HashSet<Guid> { folder.Id };
            var current = folder;

            while (current.ParentFolderId is { } parentId
                && byId.TryGetValue(parentId, out var parent)
                && visited.Add(parentId))
            {
                depth++;
                current = parent;
            }

            depths[folder.Id] = depth;
        }

        return folders.OrderByDescending(f => depths[f.Id]);
    }
}
