using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Application.UseCases.Folders;

/// <summary>Pedido para vincular (ou desvincular) uma pasta a um Diretório de Domínio.</summary>
/// <param name="FolderId">Pasta a configurar.</param>
/// <param name="DomainDirectoryId">
/// Diretório a vincular. <see langword="null"/> remove o vínculo próprio da pasta, que
/// volta a herdar do ancestral, se houver.
/// </param>
public readonly record struct SetFolderRestrictionCommand(Guid FolderId, Guid? DomainDirectoryId);

/// <summary>Resultado da configuração de restrição.</summary>
/// <param name="AffectedFolderIds">Pastas cuja restrição efetiva mudou, incluindo as herdeiras.</param>
public readonly record struct SetFolderRestrictionResult(IReadOnlyList<Guid> AffectedFolderIds);

/// <summary>
/// Define a restrição de domínio de uma pasta e propaga a herança para a subárvore.
/// </summary>
/// <remarks>
/// Implementa a seção 5.4 da especificação: as subpastas herdam automaticamente a regra
/// do Diretório de Domínio pai, e nenhuma pasta pode responder a dois diretórios ao mesmo
/// tempo.
/// </remarks>
public sealed class SetFolderRestrictionHandler
{
    private readonly IFolderRepository _folders;
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SetFolderRestrictionHandler> _logger;

    public SetFolderRestrictionHandler(
        IFolderRepository folders,
        IDomainDirectoryRepository directories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<SetFolderRestrictionHandler> logger)
    {
        _folders = folders;
        _directories = directories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa a configuração.</summary>
    /// <exception cref="InvalidFolderHierarchyException">
    /// A pasta ou uma de suas subpastas já responde a outro Diretório de Domínio.
    /// </exception>
    public async Task<SetFolderRestrictionResult> HandleAsync(
        SetFolderRestrictionCommand command, CancellationToken cancellationToken = default)
    {
        var folder = await _folders.GetByIdAsync(command.FolderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Pasta {command.FolderId} não encontrada.");

        DomainDirectory? directory = null;
        if (command.DomainDirectoryId.HasValue)
        {
            directory = await _directories.GetByIdAsync(command.DomainDirectoryId.Value, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Diretório de Domínio {command.DomainDirectoryId} não encontrado.");
        }

        // A árvore inteira da conta vem em uma consulta: a resolução de herança percorre
        // ancestrais e descendentes, e buscar nó a nó geraria uma cascata de consultas.
        var allFolders = await _folders.ListByAccountAsync(folder.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var byId = allFolders.ToDictionary(f => f.Id);
        var childrenByParent = allFolders
            .Where(f => f.ParentFolderId.HasValue)
            .GroupBy(f => f.ParentFolderId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Folder>)g.ToList());

        // Trabalhamos sobre a instância vinda da lista para que o rastreamento do
        // contexto veja as alterações de todas as pastas da subárvore.
        var target = byId.TryGetValue(folder.Id, out var tracked) ? tracked : folder;

        var now = _timeProvider.GetUtcNow();
        target.SetExplicitRestriction(command.DomainDirectoryId, now);

        var inherited = FolderRestrictionResolver.FindInheritedRestriction(
            target, id => byId.GetValueOrDefault(id));

        var changed = FolderRestrictionResolver.Resolve(
            target,
            f => childrenByParent.TryGetValue(f.Id, out var children) ? children : [],
            inherited,
            now);

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var description = directory is null
                ? $"Restrição de domínio removida da pasta '{target.DisplayName}'."
                : $"Pasta '{target.DisplayName}' vinculada ao Diretório de Domínio " +
                  $"'{directory.DomainName.Value}'. Subpastas afetadas: {changed.Count - 1}.";

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.FolderRestrictionChanged,
                    description,
                    now,
                    entityType: nameof(Folder),
                    entityId: target.Id,
                    accountId: target.AccountId,
                    domainDirectoryId: command.DomainDirectoryId),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Restrição de domínio da pasta {FolderId} alterada; {Count} pasta(s) afetadas.",
            target.Id, changed.Count);

        return new SetFolderRestrictionResult(changed.Select(f => f.Id).ToList());
    }
}
