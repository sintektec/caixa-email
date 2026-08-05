using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>De onde veio a sugestão de destinatário.</summary>
public enum RecipientSuggestionSource
{
    /// <summary>Do catálogo de contatos, mantido pelo usuário.</summary>
    Contact = 0,

    /// <summary>Do histórico automático de quem já recebeu mensagem.</summary>
    History = 1,
}

/// <summary>
/// Uma sugestão de destinatário apresentada ao digitar.
/// </summary>
/// <param name="Address">Endereço sugerido.</param>
/// <param name="DisplayName">Nome exibido, quando conhecido.</param>
/// <param name="Source">Origem da sugestão.</param>
/// <param name="BelongsToAccountDomain">
/// Se o endereço pertence ao Diretório de Domínio da conta que escreve.
/// </param>
/// <param name="Score">Peso usado na ordenação, exposto para tornar a ordem verificável.</param>
public sealed record RecipientSuggestion(
    EmailAddress Address,
    string? DisplayName,
    RecipientSuggestionSource Source,
    bool BelongsToAccountDomain,
    double Score)
{
    /// <summary>Texto exibido: nome e endereço, ou só o endereço.</summary>
    public string DisplayText => DisplayName is null
        ? Address.Value
        : $"{DisplayName} <{Address.Value}>";
}

/// <summary>
/// Ordena as sugestões de destinatário a partir do catálogo e do histórico.
/// </summary>
/// <remarks>
/// <para>
/// Puro e sem dependência: recebe os candidatos já carregados e devolve a lista ordenada.
/// É o que permite verificar a ordem — que é a única coisa que o usuário percebe — sem
/// banco nem interface.
/// </para>
/// <para>
/// <b>Sugestão fora do domínio da conta é marcada, nunca escondida.</b> Esconder quebraria
/// o e-mail externo legítimo, que é a maior parte do trabalho de quem atende clientes; não
/// marcar deixaria enviar para um domínio sósia sem perceber — o mesmo vetor que o
/// <see cref="SenderTrustEvaluator"/> cobre na leitura. Marcar é o meio-termo que informa
/// sem atrapalhar.
/// </para>
/// </remarks>
public static class RecipientSuggestionRanker
{
    /// <summary>Quantas sugestões apresentar. Além disso a lista deixa de ser útil.</summary>
    public const int DefaultLimit = 8;

    /// <summary>
    /// Peso de um contato do catálogo perante o histórico.
    /// </summary>
    /// <remarks>
    /// Contato foi cadastrado por decisão do usuário; entrada de histórico apareceu
    /// sozinha. Empatados no resto, o cadastrado vem primeiro.
    /// </remarks>
    private const double ContactBonus = 1000d;

    /// <summary>
    /// Monta a lista ordenada.
    /// </summary>
    /// <param name="term">O que o usuário digitou. Vazio devolve os mais usados.</param>
    /// <param name="history">Entradas de histórico da conta.</param>
    /// <param name="contacts">Contatos da conta.</param>
    /// <param name="accountDirectory">
    /// Diretório de Domínio da conta que escreve, para marcar o que está fora dele. Nulo
    /// desliga a marcação.
    /// </param>
    /// <param name="now">Instante atual, para o cálculo de recência.</param>
    /// <param name="limit">Teto de sugestões.</param>
    public static IReadOnlyList<RecipientSuggestion> Rank(
        string? term,
        IReadOnlyCollection<RecipientHistory> history,
        IReadOnlyCollection<Contact> contacts,
        DomainDirectory? accountDirectory,
        DateTimeOffset now,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(contacts);

        var needle = term?.Trim() ?? string.Empty;
        var suggestions = new Dictionary<string, RecipientSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var contact in contacts)
        {
            foreach (var email in contact.Emails)
            {
                if (!Matches(needle, email.Address, contact.DisplayName))
                {
                    continue;
                }

                // Endereço principal antes dos secundários do mesmo contato.
                var score = ContactBonus + (email.IsPrimary ? 10d : 0d);

                Offer(suggestions, new RecipientSuggestion(
                    email.Address,
                    contact.DisplayName,
                    RecipientSuggestionSource.Contact,
                    BelongsTo(accountDirectory, email.Address),
                    score));
            }
        }

        foreach (var entry in history)
        {
            if (!Matches(needle, entry.Address, entry.DisplayName))
            {
                continue;
            }

            Offer(suggestions, new RecipientSuggestion(
                entry.Address,
                entry.DisplayName,
                RecipientSuggestionSource.History,
                BelongsTo(accountDirectory, entry.Address),
                ScoreOf(entry, now)));
        }

        return suggestions.Values
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Address.Value, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(limit, 1))
            .ToList();
    }

    /// <summary>
    /// Peso de uma entrada de histórico: frequência com decaimento por recência.
    /// </summary>
    /// <remarks>
    /// Só frequência faria o endereço muito usado há dois anos vencer para sempre quem você
    /// escreve toda semana hoje. Só recência faria o último endereço digitado dominar a
    /// lista. O decaimento pela metade a cada 30 dias equilibra os dois: quem some da rotina
    /// desce sozinho, sem nunca ser apagado.
    /// </remarks>
    private static double ScoreOf(RecipientHistory entry, DateTimeOffset now)
    {
        var days = Math.Max((now - entry.LastUsedAt).TotalDays, 0);
        var recency = Math.Pow(0.5, days / 30d);

        return entry.UseCount * recency;
    }

    /// <summary>
    /// Casa o que foi digitado com o endereço ou o nome.
    /// </summary>
    /// <remarks>
    /// Comparação ordinal sem distinção de maiúsculas, nunca dependente de cultura: com
    /// <c>InvariantGlobalization</c> ligado, comparação sensível a cultura é armadilha
    /// documentada.
    /// </remarks>
    private static bool Matches(string term, EmailAddress address, string? displayName)
    {
        if (term.Length == 0)
        {
            return true;
        }

        return address.Value.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (displayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool BelongsTo(DomainDirectory? directory, EmailAddress address)
        => directory is null || directory.Accepts(address);

    /// <summary>
    /// Registra a sugestão, mantendo a de maior peso quando o endereço se repete.
    /// </summary>
    /// <remarks>
    /// O mesmo endereço costuma estar no catálogo e no histórico. Exibi-lo duas vezes
    /// gastaria metade da lista com repetição.
    /// </remarks>
    private static void Offer(
        Dictionary<string, RecipientSuggestion> suggestions, RecipientSuggestion candidate)
    {
        if (!suggestions.TryGetValue(candidate.Address.Value, out var existing)
            || candidate.Score > existing.Score)
        {
            suggestions[candidate.Address.Value] = candidate;
        }
    }
}
