using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Organization;

/// <summary>Resultado das gravações de organização.</summary>
public readonly record struct OrganizationResult(bool Succeeded, Guid? EntityId, string? ErrorMessage);

/// <summary>
/// Gestão das categorias coloridas e da sua aplicação em mensagens.
/// </summary>
/// <remarks>
/// Categorias são metadado local — não há palavra-chave IMAP correspondente no escopo
/// atual —, então aplicar e retirar não passa pela fila de saída.
/// </remarks>
public sealed class ManageCategoriesHandler
{
    private readonly ICategoryRepository _categories;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ManageCategoriesHandler(
        ICategoryRepository categories, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _categories = categories;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Lista as categorias visíveis para uma conta.</summary>
    public Task<IReadOnlyList<Category>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default)
        => _categories.ListAsync(accountId, cancellationToken);

    /// <summary>Cria ou atualiza uma categoria.</summary>
    public async Task<OrganizationResult> SaveAsync(
        Guid? categoryId,
        string name,
        string colorHex,
        int? shortcut,
        Guid? accountId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new OrganizationResult(false, null, "Dê um nome à categoria.");
        }

        var now = _timeProvider.GetUtcNow();
        Category category;

        if (categoryId is { } id)
        {
            var existing = await _categories.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                return new OrganizationResult(false, null, "A categoria não existe mais.");
            }

            existing.Update(name, colorHex, shortcut, now);
            category = existing;
        }
        else
        {
            category = Category.Create(name, colorHex, now, accountId, shortcut);
            await _categories.AddAsync(category, cancellationToken).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new OrganizationResult(true, category.Id, null);
    }

    /// <summary>Exclui uma categoria; as associações caem em cascata.</summary>
    public async Task<bool> DeleteAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var category = await _categories.GetByIdAsync(categoryId, cancellationToken).ConfigureAwait(false);

        if (category is null)
        {
            return false;
        }

        _categories.Remove(category);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Aplica uma categoria a uma mensagem, se ainda não aplicada.</summary>
    public async Task<bool> AssignAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        if (await _categories.IsAssignedAsync(messageId, categoryId, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        await _categories.AssignAsync(
            MessageCategory.Create(messageId, categoryId, _timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Retira uma categoria de uma mensagem.</summary>
    public async Task<bool> UnassignAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        if (!await _categories.UnassignAsync(messageId, categoryId, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Gestão dos modelos de mensagem.</summary>
public sealed class ManageTemplatesHandler
{
    private readonly IMessageTemplateRepository _templates;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ManageTemplatesHandler(
        IMessageTemplateRepository templates, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _templates = templates;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Lista os modelos visíveis para uma conta.</summary>
    public Task<IReadOnlyList<MessageTemplate>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default)
        => _templates.ListAsync(accountId, cancellationToken);

    /// <summary>Carrega um modelo, para aplicar no compositor.</summary>
    public Task<MessageTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default)
        => _templates.GetByIdAsync(templateId, cancellationToken);

    /// <summary>Cria ou atualiza um modelo.</summary>
    public async Task<OrganizationResult> SaveAsync(
        Guid? templateId,
        string name,
        string subject,
        string htmlBody,
        Guid? accountId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new OrganizationResult(false, null, "Dê um nome ao modelo.");
        }

        var now = _timeProvider.GetUtcNow();
        MessageTemplate template;

        if (templateId is { } id)
        {
            var existing = await _templates.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                return new OrganizationResult(false, null, "O modelo não existe mais.");
            }

            existing.Update(name, subject, htmlBody, now);
            template = existing;
        }
        else
        {
            template = MessageTemplate.Create(name, subject, htmlBody, now, accountId);
            await _templates.AddAsync(template, cancellationToken).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new OrganizationResult(true, template.Id, null);
    }

    /// <summary>Exclui um modelo.</summary>
    public async Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await _templates.GetByIdAsync(templateId, cancellationToken).ConfigureAwait(false);

        if (template is null)
        {
            return false;
        }

        _templates.Remove(template);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Gestão das listas de remetentes bloqueados e confiáveis.
/// </summary>
/// <remarks>
/// O alvo é digitado como texto: com <c>@</c> vira endereço exato, sem vira domínio
/// inteiro. É como o usuário pensa — "bloqueia fulano@spam.com" ou "bloqueia promo.com".
/// </remarks>
public sealed class ManageSenderReputationHandler
{
    private readonly ISenderReputationRepository _reputations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ManageSenderReputationHandler(
        ISenderReputationRepository reputations, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _reputations = reputations;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Lista as entradas, opcionalmente de um só tipo.</summary>
    public Task<IReadOnlyList<SenderReputation>> ListAsync(
        SenderReputationKind? kind = null, CancellationToken cancellationToken = default)
        => _reputations.ListAsync(kind, cancellationToken);

    /// <summary>Adiciona uma entrada a partir do texto digitado.</summary>
    public async Task<OrganizationResult> AddAsync(
        SenderReputationKind kind,
        string target,
        Guid? accountId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new OrganizationResult(false, null, "Informe um endereço ou um domínio.");
        }

        var trimmed = target.Trim();
        var now = _timeProvider.GetUtcNow();
        SenderReputation entry;

        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            if (!EmailAddress.TryParse(trimmed, out var address))
            {
                return new OrganizationResult(false, null, $"O endereço '{trimmed}' não é válido.");
            }

            entry = SenderReputation.ForAddress(kind, address, now, accountId);
        }
        else
        {
            if (!EmailDomain.TryParse(trimmed, out var domain))
            {
                return new OrganizationResult(false, null, $"O domínio '{trimmed}' não é válido.");
            }

            entry = SenderReputation.ForDomain(kind, domain, now, accountId);
        }

        // Duplicata exata é recusada em silêncio funcional: a entrada já existente cobre.
        var existing = await _reputations.ListAsync(kind, cancellationToken).ConfigureAwait(false);
        if (existing.Any(e => e.Target == entry.Target && e.AccountId == entry.AccountId))
        {
            return new OrganizationResult(false, null, "Esta entrada já existe na lista.");
        }

        await _reputations.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new OrganizationResult(true, entry.Id, null);
    }

    /// <summary>Remove uma entrada.</summary>
    public async Task<bool> DeleteAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await _reputations.GetByIdAsync(entryId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return false;
        }

        _reputations.Remove(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Se o remetente está na lista de confiáveis — libera o conteúdo remoto.</summary>
    public async Task<bool> IsTrustedAsync(
        EmailAddress sender, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);

        var trusted = await _reputations.ListAsync(SenderReputationKind.Trusted, cancellationToken)
            .ConfigureAwait(false);

        return trusted.Any(entry => entry.AppliesTo(sender, accountId));
    }
}
