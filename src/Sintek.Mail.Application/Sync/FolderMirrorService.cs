using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.Sync;

/// <summary>Resultado do espelhamento de pastas.</summary>
/// <param name="Created">Pastas novas trazidas do servidor.</param>
/// <param name="Renamed">Pastas cujo nome mudou no servidor.</param>
/// <param name="Disabled">Pastas que sumiram do servidor e tiveram a sincronização desligada.</param>
public readonly record struct FolderMirrorResult(int Created, int Renamed, int Disabled);

/// <summary>
/// Reflete localmente a árvore de pastas do servidor.
/// </summary>
/// <remarks>
/// <para>
/// Duas regras governam o que este serviço <b>não</b> faz.
/// </para>
/// <para>
/// <b>Pastas locais nunca são tocadas.</b> Pendências e Caixa de Saída existem para conter
/// o que o IMAP desconhece; uma listagem do servidor jamais as menciona, e tratá-las como
/// "sumiram" as desligaria a cada sincronização.
/// </para>
/// <para>
/// <b>Pasta que desaparece do servidor não é apagada.</b> Ela perde a sincronização e
/// permanece com o conteúdo local. Uma resposta de LIST incompleta — servidor sob carga,
/// conexão cortada no meio — é indistinguível de uma exclusão real, e a diferença entre as
/// duas hipóteses é a caixa postal inteira do usuário. Excluir de fato é decisão do
/// usuário, pela interface de pastas.
/// </para>
/// </remarks>
public sealed class FolderMirrorService
{
    private readonly IFolderRepository _folders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FolderMirrorService> _logger;

    public FolderMirrorService(
        IFolderRepository folders,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<FolderMirrorService> logger)
    {
        _folders = folders;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Aplica a listagem do servidor à árvore local da conta.</summary>
    public async Task<FolderMirrorResult> MirrorAsync(
        Guid accountId,
        IReadOnlyList<RemoteFolder> remoteFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteFolders);

        var local = await _folders.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var byRemotePath = local
            .Where(f => !f.IsLocalOnly)
            .ToDictionary(f => f.RemotePath, StringComparer.Ordinal);

        var created = 0;
        var renamed = 0;

        // Do caminho mais curto para o mais longo: a pasta-mãe precisa existir antes da
        // subpasta, e o servidor não garante ordem alguma na listagem.
        foreach (var remote in remoteFolders.OrderBy(r => r.RemotePath.Length).ThenBy(r => r.RemotePath, StringComparer.Ordinal))
        {
            if (byRemotePath.TryGetValue(remote.RemotePath, out var existing))
            {
                if (UpdateExisting(existing, remote, now))
                {
                    renamed++;
                }

                continue;
            }

            var folder = Folder.Create(
                accountId,
                remote.Name,
                remote.FolderType,
                now,
                parentFolderId: ResolveParentId(byRemotePath, remote),
                remotePath: remote.RemotePath);

            folder.ConfigureSync(syncEnabled: remote.IsSubscribed, isSubscribed: remote.IsSubscribed, now);

            await _folders.AddAsync(folder, cancellationToken).ConfigureAwait(false);
            byRemotePath[remote.RemotePath] = folder;
            created++;
        }

        var remotePaths = remoteFolders.Select(r => r.RemotePath).ToHashSet(StringComparer.Ordinal);
        var disabled = 0;

        foreach (var folder in local.Where(f => !f.IsLocalOnly && f.SyncEnabled))
        {
            if (remotePaths.Contains(folder.RemotePath))
            {
                continue;
            }

            folder.ConfigureSync(syncEnabled: false, isSubscribed: false, now);
            disabled++;

            _logger.LogInformation(
                "A pasta '{RemotePath}' não veio na listagem do servidor; sincronização desligada e conteúdo local preservado.",
                folder.RemotePath);
        }

        if (created > 0)
        {
            ReapplyInheritance(byRemotePath.Values.Concat(local).DistinctBy(f => f.Id).ToList(), now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (created > 0 || renamed > 0 || disabled > 0)
        {
            _logger.LogInformation(
                "Espelhamento da conta {AccountId}: {Created} criada(s), {Renamed} renomeada(s), {Disabled} desligada(s).",
                accountId, created, renamed, disabled);
        }

        return new FolderMirrorResult(created, renamed, disabled);
    }

    /// <summary>
    /// Recalcula a restrição efetiva de toda a árvore da conta.
    /// </summary>
    /// <remarks>
    /// Necessário porque pasta nova entra na árvore sem valor efetivo nenhum. Sem isto, uma
    /// subpasta criada pelo servidor dentro de um ramo restrito nasceria livre — e passaria
    /// a aceitar exatamente as mensagens que o ramo existe para recusar.
    /// </remarks>
    internal static void ReapplyInheritance(IReadOnlyList<Folder> folders, DateTimeOffset now)
    {
        var childrenByParent = folders
            .Where(f => f.ParentFolderId.HasValue)
            .GroupBy(f => f.ParentFolderId!.Value)
            .ToDictionary(g => g.Key, g => (IEnumerable<Folder>)g.ToList());

        IEnumerable<Folder> ChildrenOf(Folder folder)
            => childrenByParent.TryGetValue(folder.Id, out var children) ? children : [];

        foreach (var root in folders.Where(f => f.ParentFolderId is null))
        {
            FolderRestrictionResolver.Resolve(root, ChildrenOf, inheritedFromAncestor: null, now);
        }
    }

    /// <summary>
    /// Atualiza uma pasta já conhecida e informa se o nome mudou.
    /// </summary>
    /// <remarks>
    /// O papel da pasta (<see cref="FolderType"/>) é reaplicado porque servidores passam a
    /// anunciar atributos especiais depois de uma migração — uma caixa que era
    /// <c>Custom</c> vira <c>Archive</c> sem que nada tenha mudado do lado do usuário.
    /// </remarks>
    private static bool UpdateExisting(Folder folder, RemoteFolder remote, DateTimeOffset now)
    {
        var renamed = !string.Equals(folder.Name, remote.Name, StringComparison.Ordinal);

        if (renamed)
        {
            folder.Rename(remote.Name, remote.RemotePath, now);
        }

        if (folder.IsSubscribed != remote.IsSubscribed)
        {
            folder.ConfigureSync(remote.IsSubscribed, remote.IsSubscribed, now);
        }

        return renamed;
    }

    /// <summary>
    /// Encontra a pasta-mãe pelo caminho, cortando o último segmento.
    /// </summary>
    /// <remarks>
    /// O separador vem do servidor e varia — <c>/</c>, <c>.</c> e <c>\</c> são todos usados
    /// na prática. Assumir um deles quebraria a hierarquia justamente nos servidores que
    /// usam outro, e o sintoma seria uma árvore chapada sem explicação aparente.
    /// </remarks>
    private static Guid? ResolveParentId(IReadOnlyDictionary<string, Folder> byRemotePath, RemoteFolder remote)
    {
        var separatorIndex = remote.RemotePath.LastIndexOf(remote.Delimiter);

        if (separatorIndex <= 0)
        {
            return null;
        }

        var parentPath = remote.RemotePath[..separatorIndex];

        return byRemotePath.TryGetValue(parentPath, out var parent) ? parent.Id : null;
    }
}
