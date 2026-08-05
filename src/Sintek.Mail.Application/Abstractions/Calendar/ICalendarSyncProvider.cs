using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Abstractions.Calendar;

/// <summary>Uma coleção de calendário encontrada no servidor.</summary>
/// <param name="CollectionUrl">Endereço ou identificador da coleção.</param>
/// <param name="DisplayName">Nome exibido.</param>
/// <param name="Color">Cor declarada, em hexadecimal.</param>
/// <param name="IsReadOnly">Se o servidor só permite leitura.</param>
/// <param name="CTag">Marca de alteração da coleção, quando o servidor a expõe.</param>
/// <param name="SyncToken">Token de sincronização inicial, quando o servidor o expõe.</param>
public readonly record struct RemoteCalendarDescriptor(
    string CollectionUrl,
    string DisplayName,
    string? Color,
    bool IsReadOnly,
    string? CTag,
    string? SyncToken);

/// <summary>Uma alteração de recurso trazida do servidor.</summary>
/// <param name="Href">Endereço do recurso — a identidade de rede.</param>
/// <param name="ETag">ETag declarado, verbatim.</param>
/// <param name="ICalendar">Documento iCalendar, quando já veio junto.</param>
/// <param name="Change">Se foi criado/alterado ou removido.</param>
public readonly record struct RemoteCalendarChange(
    string Href, string? ETag, string? ICalendar, RemoteChangeKind Change);

/// <summary>Resultado de uma passada de leitura no servidor.</summary>
/// <param name="Changes">Alterações trazidas.</param>
/// <param name="SyncToken">Token a guardar para a próxima passada.</param>
/// <param name="CTag">Marca da coleção a guardar.</param>
/// <param name="HasMore">
/// Se o servidor truncou o lote e a passada precisa ser repetida com o token novo.
/// </param>
/// <param name="IsFullEnumeration">
/// Se esta passada listou a coleção <b>inteira</b>, e não só o que mudou desde o último
/// token. É o que autoriza o motor a apagar o que existe aqui e não veio: numa passada
/// incremental a ausência não significa nada, e apagar por ela esvaziaria a agenda.
/// <para>
/// Cuidado com o caso que parece igual e não é: um servidor sem <c>sync-collection</c> que
/// responde "o <c>CTag</c> não mudou" também devolve zero alterações — mas <b>não</b>
/// enumerou nada, e marcar isso como passada completa apagaria a coleção toda.
/// </para>
/// </param>
public readonly record struct RemoteCalendarChanges(
    IReadOnlyList<RemoteCalendarChange> Changes,
    string? SyncToken,
    string? CTag,
    bool HasMore,
    bool IsFullEnumeration);

/// <summary>Resultado da escrita de um recurso.</summary>
/// <param name="Succeeded">Se o servidor aceitou.</param>
/// <param name="Href">Endereço final do recurso.</param>
/// <param name="ETag">ETag devolvido pelo servidor, quando ele o devolve.</param>
/// <param name="IsConflict">
/// Se o servidor recusou por pré-condição — alguém escreveu antes. Distinto de falha de
/// rede: retentar sem reler não vai passar.
/// </param>
/// <param name="ErrorMessage">Motivo exibível.</param>
/// <param name="ICalendar">
/// Documento como ficou gravado, quando o provedor precisou relê-lo. Servidores reescrevem
/// o que recebem — normalizam fuso, injetam <c>SEQUENCE</c>, ajustam <c>PARTSTAT</c> —, e
/// nesse caso a norma <b>proíbe</b> devolver ETag forte no <c>PUT</c>. Reler é o único jeito
/// de saber o que ficou lá; guardar o que foi enviado faria o <c>If-Match</c> seguinte
/// falhar para sempre.
/// </param>
public readonly record struct RemoteWriteResult(
    bool Succeeded,
    string? Href,
    string? ETag,
    bool IsConflict,
    string? ErrorMessage,
    string? ICalendar = null)
{
    /// <summary>Sucesso.</summary>
    public static RemoteWriteResult Success(string href, string? etag, string? iCalendar = null)
        => new(true, href, etag, false, null, iCalendar);

    /// <summary>Recusa por pré-condição: o servidor mudou desde o ETag conhecido.</summary>
    public static RemoteWriteResult Conflict(string message) => new(false, null, null, true, message);

    /// <summary>Falha comum.</summary>
    public static RemoteWriteResult Failure(string message) => new(false, null, null, false, message);
}

/// <summary>
/// Fala com um servidor de calendário.
/// </summary>
/// <remarks>
/// <para>
/// Uma porta, três implementações — e a divisão não foi escolha de gosto. O Exchange Online
/// <b>nunca</b> implementou CalDAV, e o EWS está sendo desligado (bloqueio automático em
/// 01/10/2026, remoção em 01/04/2027): para Microsoft 365 o único caminho suportado é o
/// Microsoft Graph. A Google mantém CalDAV como compatibilidade declaradamente parcial e
/// recomenda a Calendar API. CalDAV continua sendo o padrão aberto que cobre todo o resto —
/// Nextcloud, Fastmail, iCloud, SOGo, Radicale. Ver D-026.
/// </para>
/// <para>
/// <b>O token de sincronização é opaco em todos os três</b>, e nos três existe o caso de o
/// servidor recusá-lo e obrigar a uma passada completa. Por isso ele é
/// <see cref="string"/> na porta, e por isso <see cref="RemoteCalendarChanges"/> tem
/// <c>IsFullEnumeration</c>: sem esse sinal, o motor não sabe distinguir "nada mudou" de
/// "listei tudo, e o que não veio foi apagado lá".
/// </para>
/// </remarks>
public interface ICalendarSyncProvider
{
    /// <summary>Protocolo que esta implementação fala.</summary>
    CalendarProviderKind Provider { get; }

    /// <summary>
    /// Descobre as coleções de calendário da conta.
    /// </summary>
    /// <remarks>
    /// Devolve lista vazia quando a conta não tem servidor de agenda configurado ou quando
    /// a descoberta falha — a agenda local continua funcionando sem servidor, e uma exceção
    /// aqui derrubaria o ciclo de sincronização inteiro da conta.
    /// </remarks>
    Task<IReadOnlyList<RemoteCalendarDescriptor>> DiscoverAsync(
        Account account, CancellationToken cancellationToken = default);

    /// <summary>Testa credencial e endereço, para o assistente de configuração.</summary>
    Task<Abstractions.Mail.ConnectionTestResult> TestAsync(
        Account account, CancellationToken cancellationToken = default);

    /// <summary>Traz as alterações do servidor desde o token informado.</summary>
    Task<RemoteCalendarChanges> FetchChangesAsync(
        Account account,
        RemoteCalendar calendar,
        CancellationToken cancellationToken = default);

    /// <summary>Busca o conteúdo de recursos que a listagem trouxe sem o documento.</summary>
    Task<IReadOnlyList<RemoteCalendarChange>> FetchResourcesAsync(
        Account account,
        RemoteCalendar calendar,
        IReadOnlyCollection<string> hrefs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cria um recurso novo no servidor.
    /// </summary>
    /// <param name="iCalendar">Documento completo a gravar.</param>
    Task<RemoteWriteResult> CreateAsync(
        Account account,
        RemoteCalendar calendar,
        string iCalendar,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atualiza um recurso existente.
    /// </summary>
    /// <param name="knownETag">
    /// ETag conhecido, que vai na pré-condição. Nulo escreve sem condição — só aceitável
    /// quando o usuário já decidiu sobrescrever.
    /// </param>
    Task<RemoteWriteResult> UpdateAsync(
        Account account,
        RemoteCalendar calendar,
        string href,
        string? knownETag,
        string iCalendar,
        CancellationToken cancellationToken = default);

    /// <summary>Exclui um recurso.</summary>
    Task<RemoteWriteResult> DeleteAsync(
        Account account,
        RemoteCalendar calendar,
        string href,
        string? knownETag,
        CancellationToken cancellationToken = default);
}
