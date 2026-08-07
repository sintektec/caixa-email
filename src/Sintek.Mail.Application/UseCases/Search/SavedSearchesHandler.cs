using System.Text.Json;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.UseCases.Search;

/// <summary>
/// Gerencia as pesquisas salvas: listar, salvar e excluir.
/// </summary>
/// <remarks>
/// Salvar com um nome que já existe atualiza a pesquisa existente em vez de criar outra:
/// o nome é a identidade que o usuário enxerga, e duas entradas homônimas na barra
/// lateral seriam indistinguíveis.
/// </remarks>
public sealed class SavedSearchesHandler
{
    // Os nomes das propriedades são o contrato de QueryJson: uma pesquisa salva hoje
    // precisa continuar legível nas versões futuras da aplicação.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISavedSearchRepository _savedSearches;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public SavedSearchesHandler(
        ISavedSearchRepository savedSearches,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _savedSearches = savedSearches;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Lista as pesquisas salvas, fixadas primeiro.</summary>
    public Task<IReadOnlyList<SavedSearch>> ListAsync(CancellationToken cancellationToken = default)
        => _savedSearches.ListAsync(cancellationToken);

    /// <summary>Salva os critérios com um nome, atualizando se o nome já existir.</summary>
    public async Task<SavedSearch> SaveAsync(
        string name,
        MessageSearchQuery query,
        bool isPinned = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(query);

        var now = _timeProvider.GetUtcNow();
        var json = Serialize(query);

        var existing = await _savedSearches.GetByNameAsync(name.Trim(), cancellationToken)
            .ConfigureAwait(false);

        SavedSearch saved;
        if (existing is null)
        {
            saved = SavedSearch.Create(name, json, now, isPinned);
            await _savedSearches.AddAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.Update(name, json, isPinned, now);
            saved = existing;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Exclui uma pesquisa salva.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var search = await _savedSearches.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (search is null)
        {
            return false;
        }

        _savedSearches.Remove(search);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Serializa os critérios para <see cref="SavedSearch.QueryJson"/>.</summary>
    public static string Serialize(MessageSearchQuery query)
        => JsonSerializer.Serialize(query, SerializerOptions);

    /// <summary>
    /// Reconstrói os critérios a partir do JSON gravado.
    /// </summary>
    /// <remarks>
    /// JSON inválido devolve uma pesquisa vazia em vez de lançar: uma entrada corrompida no
    /// banco não pode impedir a lista de pesquisas salvas de abrir.
    /// </remarks>
    public static MessageSearchQuery Deserialize(string queryJson)
    {
        if (string.IsNullOrWhiteSpace(queryJson))
        {
            return new MessageSearchQuery();
        }

        try
        {
            return JsonSerializer.Deserialize<MessageSearchQuery>(queryJson, SerializerOptions)
                ?? new MessageSearchQuery();
        }
        catch (JsonException)
        {
            return new MessageSearchQuery();
        }
    }
}
