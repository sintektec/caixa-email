using System.Globalization;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Mail;

/// <summary>O que os cabeçalhos do servidor dizem sobre a mensagem.</summary>
/// <param name="Spf">Resultado do SPF.</param>
/// <param name="Dkim">Resultado do DKIM.</param>
/// <param name="Dmarc">Resultado do DMARC.</param>
/// <param name="IsFlaggedAsSpam">Se o servidor a classificou como lixo eletrônico.</param>
/// <param name="SpamScore">Pontuação informada, quando houver.</param>
public readonly record struct ServerVerdict(
    AuthenticationResult Spf,
    AuthenticationResult Dkim,
    AuthenticationResult Dmarc,
    bool IsFlaggedAsSpam,
    double? SpamScore);

/// <summary>
/// Lê o que o servidor de recebimento apurou sobre a mensagem.
/// </summary>
/// <remarks>
/// <para>
/// SPF, DKIM e DMARC só podem ser verificados <b>no momento em que a mensagem chega</b>:
/// dependem de consultar o DNS do remetente naquele instante. Refazer a verificação do lado
/// do cliente, dias depois, daria resultado diferente e errado — chaves DKIM rotacionam e
/// registros SPF mudam. Por isso este analisador apenas lê o veredito alheio.
/// </para>
/// <para>
/// O formato do <c>Authentication-Results</c> (RFC 8601) admite variações entre
/// implementações, e o campo é texto livre na prática. A leitura é tolerante de propósito:
/// o que não se reconhece vira <see cref="AuthenticationResult.Unknown"/>, que a interface
/// trata como "nada a destacar" — bem melhor do que inventar um veredito.
/// </para>
/// </remarks>
public static class AuthenticationResultsParser
{
    /// <summary>Lê os cabeçalhos relevantes.</summary>
    /// <param name="authenticationResults">Valor de <c>Authentication-Results</c>.</param>
    /// <param name="spamFlag">Valor de <c>X-Spam-Flag</c>.</param>
    /// <param name="spamStatus">Valor de <c>X-Spam-Status</c>.</param>
    /// <param name="spamScore">Valor de <c>X-Spam-Score</c>.</param>
    public static ServerVerdict Parse(
        string? authenticationResults,
        string? spamFlag = null,
        string? spamStatus = null,
        string? spamScore = null)
    {
        var spf = ReadMethod(authenticationResults, "spf");
        var dkim = ReadMethod(authenticationResults, "dkim");
        var dmarc = ReadMethod(authenticationResults, "dmarc");

        return new ServerVerdict(
            spf, dkim, dmarc, IsFlaggedAsSpam(spamFlag, spamStatus), ReadScore(spamScore, spamStatus));
    }

    /// <summary>
    /// Extrai o resultado de um método específico do cabeçalho.
    /// </summary>
    /// <remarks>
    /// A busca exige o sinal de igual logo depois do nome do método para não confundir
    /// <c>dkim</c> com <c>dkim-adsp</c>, que é um método diferente e obsoleto ainda emitido
    /// por servidores antigos.
    /// </remarks>
    internal static AuthenticationResult ReadMethod(string? header, string method)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return AuthenticationResult.Unknown;
        }

        var span = header.AsSpan();
        var needle = method + "=";
        var index = 0;

        while (index < span.Length)
        {
            var found = span[index..].IndexOf(needle, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return AuthenticationResult.Unknown;
            }

            var start = index + found;

            // O método precisa começar palavra: "xspf=" não é "spf=".
            if (start == 0 || !char.IsLetterOrDigit(span[start - 1]))
            {
                var valueStart = start + needle.Length;
                var value = ReadToken(span[valueStart..]);

                return Translate(value);
            }

            index = start + needle.Length;
        }

        return AuthenticationResult.Unknown;
    }

    private static ReadOnlySpan<char> ReadToken(ReadOnlySpan<char> span)
    {
        var length = 0;

        while (length < span.Length && (char.IsLetter(span[length]) || span[length] == '-'))
        {
            length++;
        }

        return span[..length];
    }

    private static AuthenticationResult Translate(ReadOnlySpan<char> value) => value switch
    {
        _ when value.Equals("pass", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.Pass,
        _ when value.Equals("fail", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.Fail,
        _ when value.Equals("softfail", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.SoftFail,
        _ when value.Equals("neutral", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.Neutral,
        _ when value.Equals("none", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.None,
        _ when value.Equals("temperror", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.TemporaryError,
        _ when value.Equals("permerror", StringComparison.OrdinalIgnoreCase) => AuthenticationResult.PermanentError,
        _ => AuthenticationResult.Unknown,
    };

    /// <summary>
    /// Decide se o servidor marcou a mensagem como lixo eletrônico.
    /// </summary>
    /// <remarks>
    /// Duas convenções coexistem: <c>X-Spam-Flag: YES</c> e <c>X-Spam-Status: Yes, score=…</c>.
    /// Ler só uma delas deixaria metade dos servidores sem veredito.
    /// </remarks>
    internal static bool IsFlaggedAsSpam(string? spamFlag, string? spamStatus)
    {
        if (!string.IsNullOrWhiteSpace(spamFlag)
            && spamFlag.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(spamStatus)
            && spamStatus.TrimStart().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lê a pontuação, do cabeçalho próprio ou de dentro do <c>X-Spam-Status</c>.
    /// </summary>
    /// <remarks>
    /// A escala varia entre implementações — SpamAssassin e Rspamd usam faixas diferentes —,
    /// então o número serve para exibir, nunca para comparar com um limiar próprio. Quem
    /// decide o que é spam é o servidor.
    /// </remarks>
    internal static double? ReadScore(string? spamScore, string? spamStatus)
    {
        if (!string.IsNullOrWhiteSpace(spamScore)
            && double.TryParse(
                spamScore.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        if (string.IsNullOrWhiteSpace(spamStatus))
        {
            return null;
        }

        var marker = spamStatus.IndexOf("score=", StringComparison.OrdinalIgnoreCase);

        if (marker < 0)
        {
            return null;
        }

        var rest = spamStatus.AsSpan(marker + "score=".Length);
        var length = 0;

        while (length < rest.Length && (char.IsDigit(rest[length]) || rest[length] is '.' or '-' or '+'))
        {
            length++;
        }

        return double.TryParse(
            rest[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
