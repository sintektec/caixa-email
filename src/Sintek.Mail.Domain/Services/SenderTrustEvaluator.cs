using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Services;

/// <summary>Um contato conhecido, usado para detectar disfarce de identidade.</summary>
/// <param name="DisplayName">Nome como o contato aparece nas mensagens legítimas.</param>
/// <param name="Domain">Domínio de onde ele realmente escreve.</param>
public readonly record struct KnownCorrespondent(string DisplayName, EmailDomain Domain);

/// <summary>Veredito sobre uma mensagem recebida.</summary>
/// <param name="Level">Grau de confiança.</param>
/// <param name="Reason">Explicação em português, para exibição.</param>
/// <param name="ImpersonatedName">
/// Nome do contato imitado, quando o veredito é <see cref="SenderTrustLevel.DisplayNameSpoofing"/>.
/// </param>
public readonly record struct SenderTrustVerdict(
    SenderTrustLevel Level,
    string Reason,
    string? ImpersonatedName = null);

/// <summary>
/// Decide o que exibir sobre a procedência de uma mensagem.
/// </summary>
/// <remarks>
/// <para>
/// Pura e sem dependências, como todo o domínio. Ela <b>não</b> classifica spam: quem faz
/// isso é o servidor, com volume global e reputação de IP que nenhum cliente desktop tem. O
/// que esta classe faz é ler o veredito alheio e acrescentar a única verificação que o
/// cliente pode fazer melhor do que o servidor — comparar o nome exibido com os contatos que
/// este usuário de fato tem.
/// </para>
/// <para>
/// A ordem dos testes é por gravidade decrescente. Uma mensagem marcada como spam pelo
/// servidor e ainda por cima disfarçada precisa mostrar o disfarce: é a informação que muda
/// o comportamento de quem já ia ignorar o aviso de spam.
/// </para>
/// </remarks>
public static class SenderTrustEvaluator
{
    /// <summary>Avalia a mensagem.</summary>
    /// <param name="message">Mensagem recebida.</param>
    /// <param name="knownCorrespondents">
    /// Contatos com quem o usuário já trocou mensagens legítimas. Vazio desativa a detecção
    /// de disfarce, que é o comportamento correto numa caixa recém-configurada: sem histórico
    /// não há como saber quem é quem, e chutar produziria alarme falso em massa.
    /// </param>
    public static SenderTrustVerdict Evaluate(
        Message message, IReadOnlyCollection<KnownCorrespondent> knownCorrespondents)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(knownCorrespondents);

        var impersonated = FindImpersonatedCorrespondent(message, knownCorrespondents);

        if (impersonated is { } victim)
        {
            return new SenderTrustVerdict(
                SenderTrustLevel.DisplayNameSpoofing,
                $"Esta mensagem se apresenta como '{victim.DisplayName}', mas foi enviada do domínio " +
                $"'{message.FromAddress!.Domain.Value}', e não de '{victim.Domain.Value}'. " +
                "Confirme por outro meio antes de responder ou clicar em qualquer link.",
                victim.DisplayName);
        }

        if (message.IsFlaggedAsSpamByServer)
        {
            return new SenderTrustVerdict(
                SenderTrustLevel.FlaggedAsSpam,
                "O servidor classificou esta mensagem como lixo eletrônico.");
        }

        if (HasFailedAuthentication(message))
        {
            return new SenderTrustVerdict(
                SenderTrustLevel.AuthenticationFailed,
                "A verificação de origem desta mensagem falhou. Ela pode não ter sido enviada por " +
                "quem diz tê-la enviado.");
        }

        if (IsFullyAuthenticated(message))
        {
            return new SenderTrustVerdict(
                SenderTrustLevel.Authenticated,
                "A origem desta mensagem foi verificada pelo servidor.");
        }

        return new SenderTrustVerdict(SenderTrustLevel.Neutral, string.Empty);
    }

    /// <summary>
    /// Autenticação completa: SPF e DKIM passaram e o DMARC não contradiz.
    /// </summary>
    /// <remarks>
    /// O DMARC pode vir como <see cref="AuthenticationResult.Unknown"/> em servidores que não
    /// o avaliam, e exigir aprovação dele apagaria o selo de praticamente toda mensagem
    /// legítima. O que não se aceita é DMARC reprovado com SPF e DKIM passando — combinação
    /// que indica alinhamento quebrado.
    /// </remarks>
    private static bool IsFullyAuthenticated(Message message)
        => message.SpfResult == AuthenticationResult.Pass
            && message.DkimResult == AuthenticationResult.Pass
            && message.DmarcResult is AuthenticationResult.Pass
                or AuthenticationResult.Unknown
                or AuthenticationResult.None;

    /// <summary>
    /// Falha de autenticação que vale um aviso.
    /// </summary>
    /// <remarks>
    /// Erro temporário fica de fora de propósito: ele diz que a verificação não pôde ser
    /// feita, não que ela reprovou. Tratá-lo como falha encheria a caixa de avisos toda vez
    /// que um DNS ficasse instável, e aviso que aparece sempre deixa de ser lido.
    /// </remarks>
    private static bool HasFailedAuthentication(Message message)
        => message.DmarcResult == AuthenticationResult.Fail
            || message.SpfResult is AuthenticationResult.Fail
            || message.DkimResult == AuthenticationResult.Fail;

    /// <summary>
    /// Procura um contato conhecido cujo nome esteja sendo imitado.
    /// </summary>
    /// <remarks>
    /// A comparação de nome é sem diferenciar maiúsculas e acentos porque o disfarce não
    /// precisa ser exato para funcionar — "joao silva" engana tão bem quanto "João Silva".
    /// Já o domínio é comparado de forma exata: subdomínio legítimo do mesmo domínio não é
    /// disfarce, e é por isso que a comparação usa a regra do próprio value object.
    /// </remarks>
    private static KnownCorrespondent? FindImpersonatedCorrespondent(
        Message message, IReadOnlyCollection<KnownCorrespondent> knownCorrespondents)
    {
        if (message.FromAddress is null || string.IsNullOrWhiteSpace(message.FromDisplayName))
        {
            return null;
        }

        var displayName = Normalize(message.FromDisplayName);

        foreach (var known in knownCorrespondents)
        {
            if (!Normalize(known.DisplayName).Equals(displayName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!message.FromAddress.Domain.IsSameOrSubdomainOf(known.Domain, allowSubdomains: true))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>
    /// Reduz o nome a minúsculas sem acento, para comparação.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O mapeamento é explícito, e não via <c>string.Normalize(FormD)</c>, porque o projeto
    /// compila com <c>InvariantGlobalization</c>: nesse modo a normalização não decompõe
    /// nada, o acento sobrevive como caractere único e "JOAO SILVA" deixaria de casar com
    /// "João Silva" — justamente o disfarce que esta classe existe para pegar.
    /// </para>
    /// <para>
    /// A tabela cobre o Latin-1 acentuado, que é o alcance de qualquer disfarce escrito em
    /// português, espanhol, francês ou alemão. Alfabetos não latinos não são transliterados:
    /// ali a comparação exata já basta.
    /// </para>
    /// </remarks>
    internal static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(RemoveDiacritic(character));
        }

        return builder.ToString();
    }

    private static char RemoveDiacritic(char character) => character switch
    {
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        'ç' => 'c',
        'ñ' => 'n',
        'ý' or 'ÿ' => 'y',
        _ => character,
    };
}
