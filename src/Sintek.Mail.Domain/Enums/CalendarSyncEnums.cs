namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Protocolo pelo qual um calendário remoto é sincronizado.
/// </summary>
/// <remarks>
/// Três protocolos, e não um, porque o mercado se dividiu: o Exchange Online <b>nunca</b>
/// implementou CalDAV, e o EWS está sendo desligado (bloqueio automático em 01/10/2026,
/// remoção em 01/04/2027). Para Microsoft 365 o único caminho suportado é o Graph. Ver
/// D-026.
/// </remarks>
public enum CalendarProviderKind
{
    /// <summary>
    /// Sem servidor de agenda. A agenda local continua funcionando por inteiro — convite que
    /// chega por e-mail, movimentação, resposta ao organizador. Só não há espelho remoto.
    /// </summary>
    None = 0,

    /// <summary>
    /// CalDAV (RFC 4791) — o padrão aberto. Cobre Nextcloud, Fastmail, iCloud, SOGo,
    /// Radicale, Baikal e o endpoint de compatibilidade da Google.
    /// </summary>
    CalDav = 1,

    /// <summary>
    /// Microsoft Graph — o único caminho suportado para Microsoft 365 e Exchange Online.
    /// </summary>
    MicrosoftGraph = 2,

    /// <summary>Google Calendar API v3.</summary>
    GoogleCalendar = 3,
}

/// <summary>Estado de sincronização de um compromisso perante o servidor.</summary>
/// <remarks>
/// Espelha <see cref="MessageSyncState"/> de propósito: o vocabulário é o mesmo, e quem já
/// entende a fila de mensagens entende esta sem aprender outro.
/// </remarks>
public enum CalendarSyncState
{
    /// <summary>Idêntico ao servidor.</summary>
    Synced = 0,

    /// <summary>Só existe localmente — nenhum calendário remoto o recebeu ainda.</summary>
    LocalOnly = 1,

    /// <summary>Criado localmente e aguardando o primeiro envio.</summary>
    PendingCreate = 2,

    /// <summary>Alterado localmente e aguardando envio.</summary>
    PendingUpdate = 3,

    /// <summary>Excluído localmente e aguardando o envio da exclusão.</summary>
    PendingDelete = 4,

    /// <summary>
    /// Alterado dos dois lados desde a última sincronização. Exige decisão explícita e fica
    /// visível na interface, em vez de ser resolvido em silêncio.
    /// </summary>
    Conflict = 5,
}

/// <summary>Como um recurso mudou no servidor entre duas sincronizações.</summary>
public enum RemoteChangeKind
{
    /// <summary>Criado ou alterado — o protocolo do CalDAV não distingue os dois.</summary>
    Upserted = 0,

    /// <summary>Removido da coleção.</summary>
    Removed = 1,
}
