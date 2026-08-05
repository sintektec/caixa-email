using System.Globalization;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>Um participante da mensagem, visto pelo motor de regras.</summary>
/// <param name="Kind">Campo em que o participante aparece.</param>
/// <param name="Address">Endereço completo.</param>
/// <param name="Domain">Domínio do endereço.</param>
public sealed record RuleParticipant(AddressKind Kind, string Address, EmailDomain Domain);

/// <summary>
/// O que uma mensagem oferece ao motor de regras — um instantâneo imutável, sem
/// dependência de banco ou de rede.
/// </summary>
/// <remarks>
/// Na chegada, o corpo ainda não foi baixado; <see cref="BodyText"/> recebe a prévia, que
/// é o que existe naquele momento. Uma condição de corpo avalia sobre ela — melhor casar
/// pelo começo do texto do que nunca casar.
/// </remarks>
public sealed record RuleMessageFacts
{
    /// <summary>Conta que recebeu a mensagem.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Assunto.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Corpo em texto — ou a prévia, quando o corpo ainda não foi baixado.</summary>
    public string BodyText { get; init; } = string.Empty;

    /// <summary>Endereço do remetente.</summary>
    public string? FromAddress { get; init; }

    /// <summary>Domínio do remetente.</summary>
    public EmailDomain? FromDomain { get; init; }

    /// <summary>Participantes da mensagem.</summary>
    public IReadOnlyList<RuleParticipant> Participants { get; init; } = [];

    /// <summary>Nomes dos anexos.</summary>
    public IReadOnlyList<string> AttachmentNames { get; init; } = [];

    /// <summary>Se a mensagem tem anexos.</summary>
    public bool HasAttachments { get; init; }

    /// <summary>Tamanho total em bytes.</summary>
    public long Size { get; init; }

    /// <summary>Instante de recebimento.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Prioridade declarada.</summary>
    public MessageImportance Importance { get; init; }
}

/// <summary>
/// Avalia se uma regra automática é satisfeita por uma mensagem.
/// </summary>
/// <remarks>
/// <para>
/// Puro por construção: recebe a regra e um instantâneo dos fatos da mensagem, devolve a
/// decisão. Quem executa as ações — mover, categorizar, marcar — é a camada de Aplicação;
/// mover passa por <c>MoveMessageHandler</c> como qualquer movimentação.
/// </para>
/// <para>
/// Comparações de texto são ordinais (com ou sem distinção de maiúsculas, conforme a
/// condição). Com <c>InvariantGlobalization</c> ligado, qualquer coisa dependente de
/// cultura seria armadilha — ver <c>SenderTrustEvaluator</c>.
/// </para>
/// </remarks>
public static class RuleEvaluator
{
    /// <summary>
    /// Decide se a regra é satisfeita.
    /// </summary>
    /// <remarks>
    /// Regra sem condições é satisfeita por toda mensagem — é o "aplicar a todas" do
    /// Outlook, útil para uma regra de categorização geral da conta.
    /// </remarks>
    public static bool Matches(Rule rule, RuleMessageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        if (!rule.IsEnabled)
        {
            return false;
        }

        if (rule.Conditions.Count == 0)
        {
            return true;
        }

        return rule.MatchType == RuleMatchType.All
            ? rule.Conditions.All(condition => IsSatisfied(condition, facts))
            : rule.Conditions.Any(condition => IsSatisfied(condition, facts));
    }

    /// <summary>Avalia uma condição isolada.</summary>
    public static bool IsSatisfied(RuleCondition condition, RuleMessageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(facts);

        return condition.Field switch
        {
            RuleField.Sender => EvaluateText(
                condition, facts.FromAddress is null ? [] : [facts.FromAddress],
                facts.FromDomain is null ? [] : [facts.FromDomain]),

            RuleField.Recipient => EvaluateParticipants(condition, facts, AddressKind.To),
            RuleField.Cc => EvaluateParticipants(condition, facts, AddressKind.Cc),

            RuleField.Subject => EvaluateText(condition, [facts.Subject], []),
            RuleField.Body => EvaluateText(condition, [facts.BodyText], []),
            RuleField.AttachmentName => EvaluateText(condition, facts.AttachmentNames, []),

            RuleField.HasAttachment => EvaluateBoolean(condition, facts.HasAttachments),

            RuleField.ParticipantDomain => EvaluateText(
                condition,
                facts.Participants.Select(p => p.Domain.Value).ToList(),
                facts.Participants.Select(p => p.Domain).ToList()),

            RuleField.Size => EvaluateNumber(condition, facts.Size),
            RuleField.ReceivedAt => EvaluateDate(condition, facts.ReceivedAt),
            RuleField.Importance => EvaluateImportance(condition, facts.Importance),
            RuleField.Account => EvaluateAccount(condition, facts.AccountId),

            _ => false,
        };
    }

    /// <summary>
    /// Operadores de texto sobre uma lista de valores: basta um valor satisfazer.
    /// </summary>
    /// <remarks>
    /// Os operadores negativos exigem que <b>nenhum</b> valor case: "CC não contém
    /// fulano" com dois endereços em cópia só é verdade se nenhum deles for o fulano.
    /// </remarks>
    private static bool EvaluateText(
        RuleCondition condition, IReadOnlyList<string> values, IReadOnlyList<EmailDomain> domains)
    {
        var expected = condition.Value ?? string.Empty;
        var comparison = condition.IsCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return condition.Operator switch
        {
            RuleOperator.Contains => values.Any(v => v.Contains(expected, comparison)),
            RuleOperator.NotContains => !values.Any(v => v.Contains(expected, comparison)),
            RuleOperator.Equals => values.Any(v => v.Equals(expected, comparison)),
            RuleOperator.NotEquals => !values.Any(v => v.Equals(expected, comparison)),
            RuleOperator.StartsWith => values.Any(v => v.StartsWith(expected, comparison)),
            RuleOperator.EndsWith => values.Any(v => v.EndsWith(expected, comparison)),
            RuleOperator.InDomain => EvaluateInDomain(expected, domains),
            _ => false,
        };
    }

    /// <summary>
    /// Pertencimento a domínio de verdade, não comparação de texto: usa a regra de
    /// domínio, com subdomínios incluídos — "sintek.com.br" cobre "vendas.sintek.com.br".
    /// </summary>
    private static bool EvaluateInDomain(string expected, IReadOnlyList<EmailDomain> domains)
        => EmailDomain.TryParse(expected, out var target)
            && domains.Any(d => d.IsSameOrSubdomainOf(target, allowSubdomains: true));

    private static bool EvaluateParticipants(
        RuleCondition condition, RuleMessageFacts facts, AddressKind kind)
    {
        var matching = facts.Participants.Where(p => p.Kind == kind).ToList();

        return EvaluateText(
            condition,
            matching.Select(p => p.Address).ToList(),
            matching.Select(p => p.Domain).ToList());
    }

    private static bool EvaluateBoolean(RuleCondition condition, bool actual) => condition.Operator switch
    {
        RuleOperator.IsTrue => actual,
        RuleOperator.IsFalse => !actual,
        _ => false,
    };

    private static bool EvaluateNumber(RuleCondition condition, long actual)
    {
        if (!long.TryParse(condition.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        return condition.Operator switch
        {
            RuleOperator.GreaterThan => actual > expected,
            RuleOperator.LessThan => actual < expected,
            RuleOperator.Equals => actual == expected,
            RuleOperator.NotEquals => actual != expected,
            _ => false,
        };
    }

    private static bool EvaluateDate(RuleCondition condition, DateTimeOffset actual)
    {
        if (!DateTimeOffset.TryParse(
            condition.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expected))
        {
            return false;
        }

        return condition.Operator switch
        {
            RuleOperator.GreaterThan => actual > expected,
            RuleOperator.LessThan => actual < expected,
            _ => false,
        };
    }

    private static bool EvaluateImportance(RuleCondition condition, MessageImportance actual)
    {
        if (!Enum.TryParse<MessageImportance>(condition.Value, ignoreCase: true, out var expected))
        {
            return false;
        }

        return condition.Operator switch
        {
            RuleOperator.Equals => actual == expected,
            RuleOperator.NotEquals => actual != expected,
            _ => false,
        };
    }

    private static bool EvaluateAccount(RuleCondition condition, Guid accountId)
    {
        if (!Guid.TryParse(condition.Value, out var expected))
        {
            return false;
        }

        return condition.Operator switch
        {
            RuleOperator.Equals => accountId == expected,
            RuleOperator.NotEquals => accountId != expected,
            _ => false,
        };
    }
}
