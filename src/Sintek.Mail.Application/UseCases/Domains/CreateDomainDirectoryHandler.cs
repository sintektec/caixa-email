using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Domains;

/// <summary>Pedido de criação de um Diretório de Domínio.</summary>
public sealed record CreateDomainDirectoryCommand
{
    /// <summary>Domínio representado pelo diretório.</summary>
    public required string DomainName { get; init; }

    /// <summary>Descrição livre, exibida na árvore de navegação.</summary>
    public string? Description { get; init; }

    /// <summary>Quais participantes contam ao decidir se uma mensagem pertence ao domínio.</summary>
    public DomainValidationMode ValidationMode { get; init; } = DomainValidationMode.AnyParticipant;

    /// <summary>O que fazer com uma mensagem incompatível em uma pasta restrita.</summary>
    public InvalidEmailAction InvalidEmailAction { get; init; } = InvalidEmailAction.Block;

    /// <summary>Se contas e mensagens de subdomínios são aceitas.</summary>
    public bool AllowSubdomains { get; init; }

    /// <summary>Domínios adicionais aceitos pelo diretório.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Resultado da criação.</summary>
/// <param name="Succeeded">Se o diretório foi criado.</param>
/// <param name="DomainDirectoryId">Identificador do diretório criado.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct CreateDomainDirectoryResult(
    bool Succeeded, Guid? DomainDirectoryId, string? ErrorMessage);

/// <summary>
/// Cria um Diretório de Domínio.
/// </summary>
/// <remarks>
/// A validação estrita do domínio já vive em <see cref="EmailDomain"/>; aqui se acrescenta
/// o que só a Aplicação enxerga: que nenhum outro diretório já represente o mesmo domínio,
/// direto ou por domínio adicional. Dois diretórios para o mesmo domínio tornariam ambíguo
/// a qual deles uma conta pertence, e a ambiguidade acabaria decidida pela ordem de
/// listagem — que ninguém controla.
/// </remarks>
public sealed class CreateDomainDirectoryHandler
{
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CreateDomainDirectoryHandler> _logger;

    public CreateDomainDirectoryHandler(
        IDomainDirectoryRepository directories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CreateDomainDirectoryHandler> logger)
    {
        _directories = directories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Executa a criação.</summary>
    public async Task<CreateDomainDirectoryResult> HandleAsync(
        CreateDomainDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EmailDomain.TryParse(command.DomainName, out var domain, out var parseError))
        {
            return new CreateDomainDirectoryResult(false, null, parseError);
        }

        var conflict = await FindConflictAsync(domain, cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return new CreateDomainDirectoryResult(false, null, conflict);
        }

        var aliases = new List<EmailDomain>();

        foreach (var raw in command.Aliases)
        {
            if (!EmailDomain.TryParse(raw, out var alias, out var aliasError))
            {
                return new CreateDomainDirectoryResult(false, null, aliasError);
            }

            if (alias.Equals(domain))
            {
                continue;
            }

            var aliasConflict = await FindConflictAsync(alias, cancellationToken).ConfigureAwait(false);
            if (aliasConflict is not null)
            {
                return new CreateDomainDirectoryResult(false, null, aliasConflict);
            }

            aliases.Add(alias);
        }

        var now = _timeProvider.GetUtcNow();

        var directory = DomainDirectory.Create(
            domain,
            now,
            string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim(),
            command.ValidationMode,
            command.InvalidEmailAction,
            command.AllowSubdomains);

        foreach (var alias in aliases)
        {
            directory.AddAlias(alias, now);
        }

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _directories.AddAsync(directory, ct).ConfigureAwait(false);

            await _audit.RecordAsync(
                AuditLogEntry.Record(
                    AuditEventType.DomainDirectoryCreated,
                    $"Diretório de Domínio '{domain.Value}' criado com modo de validação " +
                    $"{command.ValidationMode} e ação {command.InvalidEmailAction}. " +
                    $"Domínios adicionais: {aliases.Count}.",
                    now,
                    entityType: nameof(DomainDirectory),
                    entityId: directory.Id,
                    domainDirectoryId: directory.Id),
                ct).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Diretório de Domínio {DomainId} criado para {Domain}.", directory.Id, domain.Value);

        return new CreateDomainDirectoryResult(true, directory.Id, null);
    }

    /// <summary>
    /// Devolve a mensagem de conflito quando o domínio já pertence a outro diretório.
    /// </summary>
    /// <remarks>
    /// A consulta cobre domínio principal e domínios adicionais. Um domínio já registrado
    /// como adicional de outro diretório é conflito igualmente: as contas daquele domínio
    /// acabariam repartidas entre dois diretórios com regras possivelmente diferentes.
    /// </remarks>
    private async Task<string?> FindConflictAsync(EmailDomain domain, CancellationToken cancellationToken)
    {
        var existing = await _directories.GetByDomainAsync(domain, cancellationToken).ConfigureAwait(false);

        return existing is null
            ? null
            : $"O domínio '{domain.Value}' já pertence ao Diretório de Domínio '{existing.DomainName.Value}'.";
    }
}
