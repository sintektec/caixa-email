using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Folders;

/// <summary>Resultado de uma operação de pasta.</summary>
/// <param name="Succeeded">Se a operação concluiu.</param>
/// <param name="FolderId">Pasta criada ou alterada.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct ManageFolderResult(bool Succeeded, Guid? FolderId, string? ErrorMessage);

/// <summary>O que a exclusão de uma pasta levaria junto.</summary>
/// <param name="FolderName">Nome exibido da pasta.</param>
/// <param name="SubfolderCount">Subpastas na árvore abaixo dela.</param>
/// <param name="MessageCount">Mensagens somadas da subárvore.</param>
public readonly record struct DeleteFolderImpact(string FolderName, int SubfolderCount, int MessageCount)
{
    /// <summary>Se a exclusão dispensa confirmação — pasta vazia e sem subpastas.</summary>
    public bool IsEmpty => SubfolderCount == 0 && MessageCount == 0;
}

/// <summary>
/// Cria, renomeia, exclui e favorita pastas, sempre pelo caminho offline-first.
/// </summary>
/// <remarks>
/// <para>
/// Cada alteração vale localmente na hora e enfileira o comando IMAP correspondente. Pastas
/// padrão (Caixa de Entrada, Enviados…) não podem ser renomeadas nem excluídas: o servidor
/// as recriaria na sincronização seguinte, e o usuário veria a pasta "voltar" sem entender.
/// </para>
/// <para>
/// A especificação manda excluir "pastas vazias ou com confirmação". <see cref="AnalyzeDeleteAsync"/>
/// mede o impacto e <see cref="DeleteAsync"/> exige a confirmação quando ele não é vazio.
/// </para>
/// </remarks>
public sealed class ManageFolderHandler
{
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxEnqueuer _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManageFolderHandler> _logger;

    public ManageFolderHandler(
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        OutboxEnqueuer outbox,
        TimeProvider timeProvider,
        ILogger<ManageFolderHandler> logger)
    {
        _folders = folders;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Cria uma pasta, opcionalmente dentro de outra.</summary>
    public async Task<ManageFolderResult> CreateAsync(
        Guid accountId, string name, Guid? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ManageFolderResult(false, null, "Informe o nome da pasta.");
        }

        var all = await _folders.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var parent = parentFolderId is { } parentId ? all.FirstOrDefault(f => f.Id == parentId) : null;

        if (parentFolderId is not null && parent is null)
        {
            return new ManageFolderResult(false, null, "A pasta de destino não existe mais.");
        }

        if (parent is { IsLocalOnly: true })
        {
            return new ManageFolderResult(
                false, null, "Não é possível criar subpastas dentro de uma pasta local.");
        }

        var trimmed = name.Trim();
        var remotePath = parent is null
            ? trimmed
            : parent.RemotePath + parent.Delimiter + trimmed;

        if (all.Any(f => !f.IsLocalOnly && string.Equals(f.RemotePath, remotePath, StringComparison.Ordinal)))
        {
            return new ManageFolderResult(false, null, $"Já existe uma pasta chamada '{trimmed}' nesse local.");
        }

        var now = _timeProvider.GetUtcNow();
        var folder = Folder.Create(accountId, trimmed, FolderType.Custom, now, parentFolderId, remotePath);

        ManageFolderResult result = default;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _folders.AddAsync(folder, ct).ConfigureAwait(false);

            await _outbox.EnqueueAsync(
                accountId,
                OutboxOperationType.CreateFolder,
                folder.Id,
                new FolderOperationPayload(remotePath),
                ct).ConfigureAwait(false);

            // A pasta nova precisa herdar a restrição do ramo em que nasceu. Sem isto, uma
            // subpasta criada dentro de um ramo restrito aceitaria o que o ramo proíbe.
            FolderMirrorService.ReapplyInheritance([.. all, folder], now);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            result = new ManageFolderResult(true, folder.Id, null);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Pasta '{RemotePath}' criada na conta {AccountId}.", remotePath, accountId);

        return result;
    }

    /// <summary>Renomeia uma pasta criada pelo usuário.</summary>
    public async Task<ManageFolderResult> RenameAsync(
        Guid folderId, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new ManageFolderResult(false, folderId, "Informe o novo nome da pasta.");
        }

        var folder = await _folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(false);

        if (folder is null)
        {
            return new ManageFolderResult(false, null, "A pasta não existe mais.");
        }

        if (folder.FolderType != FolderType.Custom)
        {
            // O servidor recria as pastas padrão na sincronização seguinte; renomeá-las
            // localmente faria a pasta "voltar" com o nome antigo, parecendo defeito.
            return new ManageFolderResult(
                false, folderId, "As pastas padrão da conta não podem ser renomeadas.");
        }

        var all = await _folders.ListByAccountAsync(folder.AccountId, cancellationToken).ConfigureAwait(false);
        var trimmed = newName.Trim();

        var separatorIndex = folder.RemotePath.LastIndexOf(folder.Delimiter);
        var newRemotePath = separatorIndex > 0
            ? folder.RemotePath[..(separatorIndex + 1)] + trimmed
            : trimmed;

        if (all.Any(f => f.Id != folder.Id
            && !f.IsLocalOnly
            && string.Equals(f.RemotePath, newRemotePath, StringComparison.Ordinal)))
        {
            return new ManageFolderResult(false, folderId, $"Já existe uma pasta chamada '{trimmed}' nesse local.");
        }

        var oldRemotePath = folder.RemotePath;
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            folder.Rename(trimmed, newRemotePath, now);

            // O RENAME do IMAP renomeia a subárvore inteira no servidor; os caminhos locais
            // das descendentes precisam acompanhar, senão a próxima sincronização as trata
            // como pastas sumidas e desliga a sincronização delas.
            var oldPrefix = oldRemotePath + folder.Delimiter;

            foreach (var descendant in all.Where(f
                => !f.IsLocalOnly && f.RemotePath.StartsWith(oldPrefix, StringComparison.Ordinal)))
            {
                descendant.Rename(
                    descendant.Name,
                    newRemotePath + folder.Delimiter + descendant.RemotePath[oldPrefix.Length..],
                    now);
            }

            await _outbox.EnqueueAsync(
                folder.AccountId,
                OutboxOperationType.RenameFolder,
                folder.Id,
                new FolderOperationPayload(oldRemotePath, newRemotePath),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Pasta '{Old}' renomeada para '{New}'.", oldRemotePath, newRemotePath);

        return new ManageFolderResult(true, folderId, null);
    }

    /// <summary>Mede o que a exclusão levaria junto. Não altera nada.</summary>
    public async Task<DeleteFolderImpact?> AnalyzeDeleteAsync(
        Guid folderId, CancellationToken cancellationToken = default)
    {
        var folder = await _folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(false);

        if (folder is null)
        {
            return null;
        }

        var all = await _folders.ListByAccountAsync(folder.AccountId, cancellationToken).ConfigureAwait(false);
        var subtree = CollectSubtree(folder, all);

        var messages = 0;

        foreach (var member in subtree)
        {
            messages += await _folders.CountMessagesAsync(member.Id, cancellationToken).ConfigureAwait(false);
        }

        return new DeleteFolderImpact(folder.DisplayName, subtree.Count - 1, messages);
    }

    /// <summary>Exclui a pasta e sua subárvore.</summary>
    /// <param name="confirmed">
    /// Confirmação explícita. Obrigatória quando a pasta não está vazia — a exclusão apaga
    /// as mensagens locais e a contrapartida no servidor.
    /// </param>
    public async Task<ManageFolderResult> DeleteAsync(
        Guid folderId, bool confirmed, CancellationToken cancellationToken = default)
    {
        var folder = await _folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(false);

        if (folder is null)
        {
            return new ManageFolderResult(false, null, "A pasta não existe mais.");
        }

        if (folder.FolderType != FolderType.Custom)
        {
            return new ManageFolderResult(
                false, folderId, "As pastas padrão da conta não podem ser excluídas.");
        }

        var impact = await AnalyzeDeleteAsync(folderId, cancellationToken).ConfigureAwait(false);

        if (impact is { IsEmpty: false } && !confirmed)
        {
            return new ManageFolderResult(
                false,
                folderId,
                $"Excluir '{impact.Value.FolderName}' apagará {impact.Value.SubfolderCount} subpasta(s) e " +
                $"{impact.Value.MessageCount} mensagem(ns), aqui e no servidor. Confirme para prosseguir.");
        }

        var all = await _folders.ListByAccountAsync(folder.AccountId, cancellationToken).ConfigureAwait(false);
        var subtree = CollectSubtree(folder, all);
        var now = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // O comando ao servidor sai antes de a pasta local sumir, porque o payload
            // carrega o caminho — e é uma operação só: o DELETE do IMAP derruba a subárvore.
            await _outbox.EnqueueAsync(
                folder.AccountId,
                OutboxOperationType.DeleteFolder,
                folder.Id,
                new FolderOperationPayload(folder.RemotePath, IsLocalOnly: folder.IsLocalOnly),
                ct).ConfigureAwait(false);

            // Da folha para a raiz, por causa da restrição de chave estrangeira entre
            // pasta-mãe e subpasta.
            foreach (var member in subtree.OrderByDescending(f => f.RemotePath.Length))
            {
                _folders.Remove(member);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Pasta '{RemotePath}' excluída com {Count} pasta(s) na subárvore.",
            folder.RemotePath, subtree.Count);

        return new ManageFolderResult(true, folderId, null);
    }

    /// <summary>Marca ou desmarca a pasta como favorita. Preferência puramente local.</summary>
    public async Task<ManageFolderResult> SetFavoriteAsync(
        Guid folderId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        var folder = await _folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(false);

        if (folder is null)
        {
            return new ManageFolderResult(false, null, "A pasta não existe mais.");
        }

        folder.SetFavorite(isFavorite, _timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ManageFolderResult(true, folderId, null);
    }

    /// <summary>
    /// Junta a pasta e todas as descendentes, com proteção contra ciclo.
    /// </summary>
    private static List<Folder> CollectSubtree(Folder root, IReadOnlyList<Folder> all)
    {
        var childrenByParent = all
            .Where(f => f.ParentFolderId.HasValue)
            .GroupBy(f => f.ParentFolderId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var subtree = new List<Folder>();
        var stack = new Stack<Folder>();
        var visited = new HashSet<Guid>();

        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (!visited.Add(current.Id))
            {
                continue;
            }

            subtree.Add(current);

            if (childrenByParent.TryGetValue(current.Id, out var children))
            {
                children.ForEach(stack.Push);
            }
        }

        return subtree;
    }
}
