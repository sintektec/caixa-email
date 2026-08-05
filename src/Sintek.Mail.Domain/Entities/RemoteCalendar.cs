using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Uma coleção de calendário no servidor, espelhada localmente.
/// </summary>
/// <remarks>
/// <para>
/// É para a agenda o que <see cref="Folder"/> é para as mensagens: o nó remoto de onde o
/// conteúdo vem e para onde as alterações voltam. Pertence a uma conta, como tudo neste
/// produto.
/// </para>
/// <para>
/// <b>O token de sincronização é opaco.</b> No CalDAV é uma URI (<c>DAV:sync-token</c>), no
/// Graph um <c>deltaLink</c>, na Google um <c>syncToken</c> — os três são cadeias que o
/// servidor emite e o cliente devolve sem interpretar. Extrair número, comparar ordem ou
/// gerar um valor quebra nos três, e quebra em silêncio: o servidor aceita o token
/// inventado e devolve o conjunto errado de mudanças.
/// </para>
/// </remarks>
public sealed class RemoteCalendar : Entity
{
    private RemoteCalendar(
        Guid id, Guid accountId, CalendarProviderKind provider, string collectionUrl,
        string displayName, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        Provider = provider;
        CollectionUrl = collectionUrl;
        DisplayName = displayName;
    }

    private RemoteCalendar()
    {
    }

    /// <summary>Conta dona.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Protocolo do servidor.</summary>
    public CalendarProviderKind Provider { get; private set; }

    /// <summary>
    /// Endereço da coleção no servidor.
    /// </summary>
    /// <remarks>
    /// No CalDAV é a URL absoluta da coleção; no Graph e na Google, o identificador do
    /// calendário. É a identidade de rede — distinta do identificador local, que o servidor
    /// desconhece.
    /// </remarks>
    public string CollectionUrl { get; private set; } = string.Empty;

    /// <summary>Nome exibido, como o servidor o declara.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Cor declarada pelo servidor, em formato hexadecimal.</summary>
    public string? Color { get; private set; }

    /// <summary>
    /// Se o servidor só permite leitura.
    /// </summary>
    /// <remarks>
    /// Calendário compartilhado por outra pessoa costuma chegar assim. Tentar escrever nele
    /// devolve 403 a cada tentativa, e a fila ficaria retentando para sempre uma operação
    /// que nunca vai passar.
    /// </remarks>
    public bool IsReadOnly { get; private set; }

    /// <summary>Se este calendário participa do ciclo de sincronização.</summary>
    public bool SyncEnabled { get; private set; } = true;

    /// <summary>
    /// Token de sincronização incremental, opaco.
    /// </summary>
    /// <remarks>
    /// Nulo significa "nunca sincronizado" ou "token invalidado pelo servidor" — nos dois
    /// casos a próxima passada é completa.
    /// </remarks>
    public string? SyncToken { get; private set; }

    /// <summary>
    /// Marca de alteração da coleção, para o caminho de reserva.
    /// </summary>
    /// <remarks>
    /// O <c>CS:getctag</c> muda sempre que qualquer recurso da coleção muda. Serve aos
    /// servidores que não implementam <c>sync-collection</c>: comparar duas cadeias é muito
    /// mais barato do que listar a coleção inteira.
    /// </remarks>
    public string? CTag { get; private set; }

    /// <summary>Instante da última sincronização bem-sucedida.</summary>
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>Motivo exibível da última falha.</summary>
    /// <remarks>
    /// Quem grava é responsável por não incluir credencial nem conteúdo de compromisso —
    /// mesma regra de <see cref="Account.LastSyncError"/>.
    /// </remarks>
    public string? LastSyncError { get; private set; }

    /// <summary>Registra um calendário remoto.</summary>
    public static RemoteCalendar Create(
        Guid accountId,
        CalendarProviderKind provider,
        string collectionUrl,
        string displayName,
        DateTimeOffset createdAt,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionUrl);

        return new RemoteCalendar(
            id ?? Guid.CreateVersion7(), accountId, provider, collectionUrl.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? collectionUrl.Trim() : displayName.Trim(),
            createdAt);
    }

    /// <summary>Atualiza os metadados vindos do servidor.</summary>
    public void Describe(string displayName, string? color, bool isReadOnly, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
        }

        Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        IsReadOnly = isReadOnly;
        Touch(now);
    }

    /// <summary>Liga ou desliga a sincronização deste calendário.</summary>
    public void SetSyncEnabled(bool enabled, DateTimeOffset now)
    {
        SyncEnabled = enabled;
        Touch(now);
    }

    /// <summary>Registra uma passada bem-sucedida.</summary>
    public void MarkSynced(string? syncToken, string? cTag, DateTimeOffset now)
    {
        SyncToken = string.IsNullOrWhiteSpace(syncToken) ? null : syncToken;
        CTag = string.IsNullOrWhiteSpace(cTag) ? null : cTag;
        LastSyncAt = now;
        LastSyncError = null;
        Touch(now);
    }

    /// <summary>Registra falha, preservando o token para a próxima tentativa.</summary>
    public void MarkSyncFailed(string error, DateTimeOffset now)
    {
        LastSyncError = error;
        Touch(now);
    }

    /// <summary>
    /// Descarta o token porque o servidor o recusou.
    /// </summary>
    /// <remarks>
    /// O servidor esquece tokens antigos — por limpeza, por atualização de versão, por
    /// política. A resposta correta é refazer a sincronização completa, e <b>não</b> apagar
    /// o que já está local: a passada completa devolve href e ETag de tudo, e só o que
    /// divergir precisa ser baixado de novo. Tratar como erro transitório produz um laço que
    /// nunca converge.
    /// </remarks>
    public void InvalidateSyncToken(DateTimeOffset now)
    {
        SyncToken = null;
        Touch(now);
    }
}
