using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>Um participante de mensagem reduzido ao que a regra de domínio precisa.</summary>
/// <param name="Kind">Em que campo o participante aparece.</param>
/// <param name="Domain">Domínio do participante, já normalizado.</param>
/// <remarks>
/// A avaliação trabalha sobre esta projeção, e não sobre <see cref="Message"/>, para que
/// a camada de persistência possa alimentá-la com uma consulta enxuta sobre
/// <c>MessageAddresses</c> — sem materializar mensagens inteiras só para decidir se uma
/// movimentação é permitida.
/// </remarks>
public readonly record struct MessageParticipant(AddressKind Kind, EmailDomain Domain);

/// <summary>Por que uma mensagem foi aceita ou recusada por um Diretório de Domínio.</summary>
public enum DomainMembershipReason
{
    /// <summary>Nenhum participante pertence ao domínio.</summary>
    NoMatch = 0,

    /// <summary>O remetente pertence ao domínio.</summary>
    SenderMatched = 1,

    /// <summary>Ao menos um destinatário pertence ao domínio.</summary>
    RecipientMatched = 2,

    /// <summary>Remetente e destinatário pertencem ao domínio.</summary>
    SenderAndRecipientMatched = 3,

    /// <summary>Algum participante — inclusive em cópia — pertence ao domínio.</summary>
    ParticipantMatched = 4,

    /// <summary>Uma regra explícita criada pelo usuário determinou o pertencimento.</summary>
    ExplicitRuleMatched = 5,

    /// <summary>O remetente pertence, mas o modo exigia também um destinatário.</summary>
    RecipientMissing = 6,

    /// <summary>Um destinatário pertence, mas o modo exigia também o remetente.</summary>
    SenderMissing = 7,
}

/// <summary>Resultado da avaliação de pertencimento.</summary>
/// <param name="IsMember">Se a mensagem pertence ao Diretório de Domínio.</param>
/// <param name="Reason">O critério que decidiu.</param>
public readonly record struct DomainMembershipResult(bool IsMember, DomainMembershipReason Reason)
{
    /// <summary>Mensagem exibível quando o pertencimento é recusado.</summary>
    public string GetUserMessage()
        => IsMember
            ? string.Empty
            : Exceptions.FolderDomainRestrictionException.RestrictionMessage;
}

/// <summary>
/// Decide se uma mensagem pertence a um Diretório de Domínio.
/// </summary>
/// <remarks>
/// <para>
/// Este avaliador é o <b>único</b> caminho pelo qual uma mensagem pode entrar em uma
/// pasta restrita. Arrastar e soltar, aplicar uma regra automática, mover pelo menu de
/// contexto e classificar durante a sincronização passam todos por aqui — é o que impede
/// que a interface, ou uma regra mal configurada, contorne a restrição.
/// </para>
/// <para>
/// É estático e sem estado de propósito: não toca banco nem relógio, o que o torna
/// exaustivamente testável e seguro para chamar dentro de um laço de sincronização.
/// </para>
/// </remarks>
public static class DomainMembershipEvaluator
{
    /// <summary>Campos que caracterizam o remetente.</summary>
    private static bool IsSender(AddressKind kind) => kind is AddressKind.From or AddressKind.Sender;

    /// <summary>Campos que caracterizam destinatário direto.</summary>
    private static bool IsRecipient(AddressKind kind) => kind is AddressKind.To;

    /// <summary>
    /// Avalia se os participantes informados fazem a mensagem pertencer ao diretório.
    /// </summary>
    /// <param name="directory">Diretório de Domínio cuja regra será aplicada.</param>
    /// <param name="participants">Participantes da mensagem.</param>
    /// <param name="matchedExplicitRule">
    /// Verdadeiro quando uma regra criada pelo usuário já determinou que a mensagem
    /// pertence a este domínio. A especificação lista esse caso como suficiente por si
    /// só, então ele curto-circuita a avaliação por participantes.
    /// </param>
    public static DomainMembershipResult Evaluate(
        DomainDirectory directory,
        IReadOnlyCollection<MessageParticipant> participants,
        bool matchedExplicitRule = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(participants);

        if (matchedExplicitRule)
        {
            return new DomainMembershipResult(true, DomainMembershipReason.ExplicitRuleMatched);
        }

        // Cópias (CC/CCO) contam apenas para AnyParticipant, através de 'anyMatches':
        // os modos de destinatário se referem a quem está em Para, como manda a
        // especificação ao tratar destinatário e cópia como critérios distintos.
        var senderMatches = false;
        var recipientMatches = false;
        var anyMatches = false;

        foreach (var participant in participants)
        {
            // AcceptsDomain já cobre o domínio principal, os domínios adicionais e a
            // permissão de subdomínios — a regra inteira do diretório em uma chamada.
            if (!directory.AcceptsDomain(participant.Domain))
            {
                continue;
            }

            anyMatches = true;

            if (IsSender(participant.Kind))
            {
                senderMatches = true;
            }
            else if (IsRecipient(participant.Kind))
            {
                recipientMatches = true;
            }
        }

        return directory.ValidationMode switch
        {
            DomainValidationMode.SenderOnly => senderMatches
                ? new DomainMembershipResult(true, DomainMembershipReason.SenderMatched)
                : new DomainMembershipResult(false, DomainMembershipReason.NoMatch),

            DomainValidationMode.RecipientOnly => recipientMatches
                ? new DomainMembershipResult(true, DomainMembershipReason.RecipientMatched)
                : new DomainMembershipResult(false, DomainMembershipReason.NoMatch),

            DomainValidationMode.SenderOrRecipient => (senderMatches, recipientMatches) switch
            {
                (true, _) => new DomainMembershipResult(true, DomainMembershipReason.SenderMatched),
                (_, true) => new DomainMembershipResult(true, DomainMembershipReason.RecipientMatched),
                _ => new DomainMembershipResult(false, DomainMembershipReason.NoMatch),
            },

            // Quando só metade da exigência é cumprida, devolvemos qual metade faltou:
            // é o que permite à interface explicar a recusa em vez de apenas negá-la.
            DomainValidationMode.SenderAndRecipient => (senderMatches, recipientMatches) switch
            {
                (true, true) => new DomainMembershipResult(true, DomainMembershipReason.SenderAndRecipientMatched),
                (true, false) => new DomainMembershipResult(false, DomainMembershipReason.RecipientMissing),
                (false, true) => new DomainMembershipResult(false, DomainMembershipReason.SenderMissing),
                _ => new DomainMembershipResult(false, DomainMembershipReason.NoMatch),
            },

            DomainValidationMode.AnyParticipant => anyMatches
                ? new DomainMembershipResult(true, DomainMembershipReason.ParticipantMatched)
                : new DomainMembershipResult(false, DomainMembershipReason.NoMatch),

            _ => throw new ArgumentOutOfRangeException(
                nameof(directory),
                directory.ValidationMode,
                "Modo de validação de domínio desconhecido."),
        };
    }

    /// <summary>
    /// Avalia uma mensagem já materializada, com seus participantes carregados.
    /// </summary>
    /// <remarks>
    /// Exige que <see cref="Message.Addresses"/> tenha sido carregado; uma mensagem sem
    /// participantes carregados seria avaliada como não pertencente, o que silenciaria a
    /// regra em vez de aplicá-la. Por isso a coleção vazia é recusada explicitamente.
    /// </remarks>
    public static DomainMembershipResult Evaluate(
        DomainDirectory directory,
        Message message,
        bool matchedExplicitRule = false)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Addresses.Count == 0)
        {
            throw new InvalidOperationException(
                $"A mensagem {message.Id} não teve seus participantes carregados. " +
                "Avaliar a regra de domínio sem eles produziria uma recusa falsa.");
        }

        var participants = message.Addresses
            .Select(a => new MessageParticipant(a.Kind, a.Domain))
            .ToArray();

        return Evaluate(directory, participants, matchedExplicitRule);
    }
}
