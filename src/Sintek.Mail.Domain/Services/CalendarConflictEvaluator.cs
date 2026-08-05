using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Services;

/// <summary>O que fazer com um compromisso quando a sincronização o encontra dos dois lados.</summary>
public enum CalendarSyncDecision
{
    /// <summary>Nada mudou de nenhum lado.</summary>
    NoChange = 0,

    /// <summary>Aplicar o que veio do servidor sobre a cópia local.</summary>
    ApplyRemote = 1,

    /// <summary>Enviar a versão local ao servidor.</summary>
    PushLocal = 2,

    /// <summary>Apagar a cópia local: o recurso sumiu do servidor.</summary>
    DeleteLocal = 3,

    /// <summary>Enviar a exclusão local ao servidor.</summary>
    PushDelete = 4,

    /// <summary>
    /// Os dois lados mudaram. Fica visível e espera decisão, em vez de alguém perder
    /// trabalho em silêncio.
    /// </summary>
    Conflict = 5,
}

/// <summary>O que a sincronização sabe sobre um compromisso ao decidir.</summary>
/// <param name="LocalState">Estado local perante o servidor.</param>
/// <param name="KnownETag">ETag que o cliente guardou na última sincronização.</param>
/// <param name="RemoteETag">ETag que o servidor declara agora. Nulo quando removido.</param>
/// <param name="RemoteChange">Como o recurso mudou no servidor.</param>
/// <param name="ExistsLocally">Se o compromisso já está na agenda local.</param>
public readonly record struct CalendarSyncFacts(
    CalendarSyncState LocalState,
    string? KnownETag,
    string? RemoteETag,
    RemoteChangeKind RemoteChange,
    bool ExistsLocally);

/// <summary>
/// Decide o destino de um compromisso quando local e servidor divergem.
/// </summary>
/// <remarks>
/// <para>
/// Puro e sem dependência, como o <see cref="DomainMembershipEvaluator"/> e o
/// <see cref="EventMoveEvaluator"/>: é a regra que decide se alguém perde trabalho, e
/// precisa ser verificável sem rede e sem banco.
/// </para>
/// <para>
/// <b>Conflito não é resolvido em silêncio.</b> Quando os dois lados mudaram, qualquer
/// escolha automática descarta o trabalho de alguém — e a pessoa só descobre quando procura
/// o que escreveu e não acha. O compromisso fica marcado e a interface pergunta. É a mesma
/// postura de <c>InvalidEmailAction.WarnAndConfirm</c> na regra de domínio: onde a decisão
/// custa caro, quem decide é o usuário.
/// </para>
/// <para>
/// <b>O ETag é comparado como texto, com as aspas.</b> O servidor emite <c>"2134-314"</c>,
/// e <c>2134-314</c> é outro valor. Normalizar aqui esconderia a diferença que o
/// <c>If-Match</c> vai enxergar depois.
/// </para>
/// </remarks>
public static class CalendarConflictEvaluator
{
    /// <summary>Decide o que fazer.</summary>
    public static CalendarSyncDecision Evaluate(CalendarSyncFacts facts)
    {
        // O compromisso não existe aqui: o que o servidor tem é novidade, e o que ele
        // removeu já não interessa.
        if (!facts.ExistsLocally)
        {
            return facts.RemoteChange == RemoteChangeKind.Removed
                ? CalendarSyncDecision.NoChange
                : CalendarSyncDecision.ApplyRemote;
        }

        var hasLocalChange = facts.LocalState
            is CalendarSyncState.PendingCreate
            or CalendarSyncState.PendingUpdate
            or CalendarSyncState.PendingDelete;

        if (facts.LocalState == CalendarSyncState.Conflict)
        {
            // Conflito já declarado continua esperando decisão. Reavaliar a cada passada
            // faria a marca sumir sozinha quando o servidor mudasse de novo.
            return CalendarSyncDecision.Conflict;
        }

        if (facts.RemoteChange == RemoteChangeKind.Removed)
        {
            // Excluído no servidor e alterado aqui: apagar descartaria a edição local, e
            // reenviar ressuscitaria o que a outra pessoa apagou de propósito.
            if (facts.LocalState is CalendarSyncState.PendingUpdate or CalendarSyncState.PendingCreate)
            {
                return CalendarSyncDecision.Conflict;
            }

            // Excluído dos dois lados é acordo, não conflito.
            return CalendarSyncDecision.DeleteLocal;
        }

        var remoteChanged = !string.Equals(facts.KnownETag, facts.RemoteETag, StringComparison.Ordinal);

        if (!hasLocalChange)
        {
            return remoteChanged ? CalendarSyncDecision.ApplyRemote : CalendarSyncDecision.NoChange;
        }

        if (!remoteChanged)
        {
            return facts.LocalState == CalendarSyncState.PendingDelete
                ? CalendarSyncDecision.PushDelete
                : CalendarSyncDecision.PushLocal;
        }

        // Os dois lados mudaram.
        return CalendarSyncDecision.Conflict;
    }

    /// <summary>
    /// Se um documento iCalendar recebido pode sobrescrever a versão local.
    /// </summary>
    /// <remarks>
    /// A regra do <c>SEQUENCE</c> (D-024) vale aqui como vale para o convite que chega por
    /// e-mail: versão menor nunca sobrescreve maior. O CalDAV carrega o iCalendar íntegro,
    /// então o <c>SEQUENCE</c> está lá — <b>diferente do Microsoft Graph</b>, que não o
    /// expõe e obriga a decidir por outro critério. Ver D-026.
    /// </remarks>
    public static bool AllowsSequence(int localSequence, int remoteSequence)
        => remoteSequence >= localSequence;
}
