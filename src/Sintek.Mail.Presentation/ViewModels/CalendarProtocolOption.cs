using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Um protocolo de agenda oferecido no assistente de contas.</summary>
/// <param name="Provider">Protocolo.</param>
/// <param name="Label">Rótulo exibido.</param>
/// <param name="Hint">O que o usuário precisa saber antes de escolher.</param>
/// <remarks>
/// Os três aparecem sempre, inclusive quando o provedor de OAuth correspondente não está
/// configurado nesta instalação: sumir com a opção faria parecer que o produto não a tem. O
/// que muda é a explicação — a mesma postura de <c>OAuthUnavailableReason</c>.
/// </remarks>
public sealed record CalendarProtocolOption(
    CalendarProviderKind Provider, string Label, string Hint)
{
    /// <summary>Os protocolos oferecidos, na ordem em que aparecem.</summary>
    /// <remarks>
    /// CalDAV primeiro por ser o padrão aberto e o único que funciona com senha comum. Os
    /// outros dois exigem OAuth do provedor correspondente — não é preferência do produto, é
    /// o que cada serviço aceita.
    /// </remarks>
    public static IReadOnlyList<CalendarProtocolOption> Options { get; } =
    [
        new(
            CalendarProviderKind.CalDav,
            "CalDAV (padrão aberto)",
            "Funciona com Nextcloud, Fastmail, iCloud, SOGo, Radicale e outros. Informe o "
            + "endereço inicial do servidor; os calendários são descobertos a partir dele."),
        new(
            CalendarProviderKind.MicrosoftGraph,
            "Microsoft 365 / Outlook.com",
            "O Exchange Online não oferece CalDAV. A agenda usa o Microsoft Graph e exige "
            + "que a conta se autentique pela Microsoft."),
        new(
            CalendarProviderKind.GoogleCalendar,
            "Google Agenda",
            "Usa a Calendar API, que a Google recomenda no lugar do CalDAV. Exige que a "
            + "conta se autentique pelo Google."),
    ];
}
