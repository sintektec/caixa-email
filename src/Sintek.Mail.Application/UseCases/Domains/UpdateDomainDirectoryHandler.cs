using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Domains;

/// <summary>Pedido de alteração de um Diretório de Domínio.</summary>
/// <remarks>
/// O nome do domínio <b>não</b> entra aqui de propósito: trocá-lo revalida contas e
/// mensagens e exige relatório de impacto e confirmação, o que é trabalho de
/// <see cref="ChangeDomainNameHandler"/>. Deixar os dois no mesmo comando permitiria trocar
/// o domínio junto com uma edição de descrição, sem passar pela confirmação.
/// </remarks>
public sealed record UpdateDomainDirectoryCommand
{
    /// <summary>Diretório a alterar.</summary>
    public required Guid DomainDirectoryId { get; init; }

    /// <summary>Descrição livre.</summary>
    public string? Description { get; init; }

    /// <summary>Se aparece entre os favoritos da árvore de navegação.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Posição manual na árvore.</summary>
    public int SortOrder { get; init; }

    /// <summary>Quais participantes contam na avaliação de pertencimento.</summary>
    public required DomainValidationMode ValidationMode { get; init; }

    /// <summary>O que fazer com mensagem incompatível em pasta restrita.</summary>
    public required InvalidEmailAction InvalidEmailAction { get; init; }

    /// <summary>Se subdomínios são aceitos.</summary>
    public bool AllowSubdomains { get; init; }

    /// <summary>Se o diretório permanece ativo.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Conjunto completo de domínios adicionais desejado.
    /// </summary>
    /// <remarks>
    /// É o estado final, não um incremento: o que não estiver nesta lista é removido. Um
    /// comando incremental exigiria que a interface soubesse o que já existe, e a diferença
    /// entre o que ela acha que existe e o que existe de fato é onde nascem as remoções
    /// acidentais.
    /// </remarks>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Resultado da alteração.</summary>
/// <param name="Succeeded">Se a alteração foi aplicada.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct UpdateDomainDirectoryResult(bool Succeeded, string? ErrorMessage);

/// <summary>Altera as regras e os metadados de um Diretório de Domínio.</summary>
public sealed class UpdateDomainDirectoryHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateDomainDirectoryHandler> _logger;

    public UpdateDomainDirectoryHandler(
        IDomainDirectoryRepository directories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<UpdateDomainDirectoryHandler> logger)
    {
        _directories = directories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa a alteração.</summary>
    public async Task<UpdateDomainDirectoryResult> HandleAsync(
        UpdateDomainDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var directory = await _directories.GetByIdAsync(command.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        if (directory is null)
        {
            return new UpdateDomainDirectoryResult(false, "O Diretório de Domínio informado não existe.");
        }

        var desiredAliases = new List<EmailDomain>();

        foreach (var raw in command.Aliases)
        {
            if (!EmailDomain.TryParse(raw, out var alias, out var aliasError))
            {
                return new UpdateDomainDirectoryResult(false, aliasError);
            }

            if (alias.Equals(directory.DomainName) || desiredAliases.Contains(alias))
            {
                continue;
            }

            var owner = await _directories.GetByDomainAsync(alias, cancellationToken).ConfigureAwait(false);
            if (owner is not null && owner.Id != directory.Id)
            {
                return new UpdateDomainDirectoryResult(
                    false,
                    $"O domínio '{alias.Value}' já pertence ao Diretório de Domínio '{owner.DomainName.Value}'.");
            }

            desiredAliases.Add(alias);
        }

        var now = _timeProvider.GetUtcNow();
        var removedCount = 0;
        var addedCount = 0;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var existing in directory.Aliases.Select(a => a.DomainName).ToList())
            {
                if (!desiredAliases.Contains(existing) && directory.RemoveAlias(existing, now))
                {
                    removedCount++;
                }
            }

            foreach (var alias in desiredAliases)
            {
                if (directory.Aliases.All(a => !a.DomainName.Equals(alias)))
                {
                    directory.AddAlias(alias, now);
                    addedCount++;
                }
            }

            directory.UpdateRules(
                command.ValidationMode, command.InvalidEmailAction, command.AllowSubdomains, now);

            directory.UpdateDisplay(
                string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim(),
                command.IsFavorite,
                command.SortOrder,
                now);

            directory.SetActive(command.IsActive, now);

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.DomainDirectoryUpdated,
                    $"Diretório de Domínio '{directory.DomainName.Value}' alterado: modo de validação " +
                    $"{command.ValidationMode}, ação {command.InvalidEmailAction}, subdomínios " +
                    $"{(command.AllowSubdomains ? "permitidos" : "recusados")}, ativo " +
                    $"{(command.IsActive ? "sim" : "não")}. Domínios adicionais: +{addedCount}/-{removedCount}.",
                    now,
                    entityType: nameof(DomainDirectory),
                    entityId: directory.Id,
                    domainDirectoryId: directory.Id),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Diretório de Domínio {DomainId} alterado.", directory.Id);

        return new UpdateDomainDirectoryResult(true, null);
    }
}
