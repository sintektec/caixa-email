using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Abstractions.Search;

/// <summary>
/// Critérios de uma pesquisa de mensagens — os filtros da seção 6.4 da especificação.
/// </summary>
/// <remarks>
/// <para>
/// Os campos textuais são pesquisados no índice de texto completo, com correspondência por
/// prefixo e sem sensibilidade a acentos ("orcamento" encontra "Orçamento"). Os demais são
/// filtros estruturais aplicados sobre as colunas de <c>Messages</c>.
/// </para>
/// <para>
/// O registro é serializado como JSON em <c>SavedSearch.QueryJson</c>; renomear uma
/// propriedade quebra as pesquisas que os usuários já salvaram.
/// </para>
/// </remarks>
public sealed record MessageSearchQuery
{
    /// <summary>Texto livre, pesquisado em todos os campos indexados.</summary>
    public string? Text { get; init; }

    /// <summary>Remetente: endereço ou nome exibido.</summary>
    public string? From { get; init; }

    /// <summary>Destinatário direto (Para).</summary>
    public string? Recipient { get; init; }

    /// <summary>Participante em cópia (CC).</summary>
    public string? Cc { get; init; }

    /// <summary>Assunto.</summary>
    public string? Subject { get; init; }

    /// <summary>Corpo da mensagem.</summary>
    public string? Body { get; init; }

    /// <summary>Nome de anexo.</summary>
    public string? AttachmentName { get; init; }

    /// <summary>Data de recebimento: início do intervalo, inclusivo.</summary>
    public DateTimeOffset? ReceivedFrom { get; init; }

    /// <summary>Data de recebimento: fim do intervalo, inclusivo.</summary>
    public DateTimeOffset? ReceivedUntil { get; init; }

    /// <summary>Restringe a uma conta.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>Restringe a uma pasta.</summary>
    public Guid? FolderId { get; init; }

    /// <summary>Restringe às contas de um Diretório de Domínio.</summary>
    public Guid? DomainDirectoryId { get; init; }

    /// <summary>Restringe a mensagens com uma categoria aplicada.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Filtra por lida/não lida. Nulo não filtra.</summary>
    public bool? IsRead { get; init; }

    /// <summary>Filtra por sinalizador. Nulo não filtra.</summary>
    public bool? IsFlagged { get; init; }

    /// <summary>Filtra por presença de anexo. Nulo não filtra.</summary>
    public bool? HasAttachments { get; init; }

    /// <summary>Filtra por importância. Nulo não filtra.</summary>
    public MessageImportance? Importance { get; init; }

    /// <summary>Filtra por status de sincronização. Nulo não filtra.</summary>
    public MessageSyncState? SyncState { get; init; }

    /// <summary>Teto de resultados, para a pesquisa não travar em caixas enormes.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Se algum critério foi informado.</summary>
    public bool HasAnyCriteria =>
        !string.IsNullOrWhiteSpace(Text)
        || !string.IsNullOrWhiteSpace(From)
        || !string.IsNullOrWhiteSpace(Recipient)
        || !string.IsNullOrWhiteSpace(Cc)
        || !string.IsNullOrWhiteSpace(Subject)
        || !string.IsNullOrWhiteSpace(Body)
        || !string.IsNullOrWhiteSpace(AttachmentName)
        || ReceivedFrom is not null
        || ReceivedUntil is not null
        || AccountId is not null
        || FolderId is not null
        || DomainDirectoryId is not null
        || CategoryId is not null
        || IsRead is not null
        || IsFlagged is not null
        || HasAttachments is not null
        || Importance is not null
        || SyncState is not null;
}

/// <summary>
/// Pesquisa local de mensagens — rápida e disponível offline, como exige a especificação.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Executa a pesquisa e devolve os identificadores das mensagens encontradas, da mais
    /// recente para a mais antiga.
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchAsync(
        MessageSearchQuery query, CancellationToken cancellationToken = default);
}
