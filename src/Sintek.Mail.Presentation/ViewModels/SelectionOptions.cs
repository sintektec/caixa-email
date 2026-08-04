using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Uma opção de lista, com o valor e o rótulo exibido.</summary>
/// <param name="Value">Valor selecionado.</param>
/// <param name="Label">Texto apresentado ao usuário.</param>
public sealed record ValidationModeOption(DomainValidationMode Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record InvalidEmailActionOption(InvalidEmailAction Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record SecurityModeOption(SecureSocketMode Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record AuthenticationOption(AuthenticationType Value, OAuthProviderKind Provider, string Label);

/// <summary>
/// Rótulos das opções apresentadas nas listas de configuração.
/// </summary>
/// <remarks>
/// Ficam aqui, e não no XAML, por dois motivos. O texto passa a ser testável junto com o
/// resto do assistente; e uma lista montada no XAML precisaria ser repetida em cada tela
/// que a usasse, com o risco de as versões divergirem — o mesmo item significando coisas
/// diferentes em telas diferentes.
/// </remarks>
public static class SelectionOptions
{
    /// <summary>Modos de validação de pertencimento, na ordem da especificação.</summary>
    public static IReadOnlyList<ValidationModeOption> ValidationModes { get; } =
    [
        new(DomainValidationMode.AnyParticipant,
            "Qualquer participante — inclui cópia e cópia oculta (mais permissivo)"),
        new(DomainValidationMode.SenderOrRecipient,
            "Remetente ou destinatário"),
        new(DomainValidationMode.SenderOnly,
            "Somente o remetente"),
        new(DomainValidationMode.RecipientOnly,
            "Somente os destinatários"),
        new(DomainValidationMode.SenderAndRecipient,
            "Remetente e destinatário — os dois lados precisam pertencer (mais restritivo)"),
    ];

    /// <summary>O que fazer com mensagem incompatível em pasta restrita.</summary>
    public static IReadOnlyList<InvalidEmailActionOption> InvalidEmailActions { get; } =
    [
        new(InvalidEmailAction.Block, "Bloquear a movimentação"),
        new(InvalidEmailAction.WarnAndConfirm, "Avisar e pedir confirmação"),
        new(InvalidEmailAction.MoveToPending, "Desviar para a pasta de pendências"),
        new(InvalidEmailAction.LogOnly, "Permitir e apenas registrar em auditoria"),
    ];

    /// <summary>
    /// Modos de proteção oferecidos.
    /// </summary>
    /// <remarks>
    /// A conexão sem criptografia aparece com aviso explícito no rótulo. Ela existe porque
    /// servidores internos antigos ainda a exigem, mas quem a escolher precisa saber o que
    /// está escolhendo — a senha trafega legível.
    /// </remarks>
    public static IReadOnlyList<SecurityModeOption> SecurityModes { get; } =
    [
        new(SecureSocketMode.SslOnConnect, "TLS desde a conexão (recomendado)"),
        new(SecureSocketMode.StartTls, "STARTTLS"),
        new(SecureSocketMode.StartTlsWhenAvailable, "STARTTLS quando disponível"),
        new(SecureSocketMode.Auto, "Detectar pela porta"),
        new(SecureSocketMode.None, "Sem criptografia — a senha trafega legível"),
    ];

    /// <summary>Formas de autenticação oferecidas no assistente.</summary>
    public static IReadOnlyList<AuthenticationOption> AuthenticationOptions { get; } =
    [
        new(AuthenticationType.Password, OAuthProviderKind.None, "Senha"),
        new(AuthenticationType.OAuth2, OAuthProviderKind.Microsoft, "Conta Microsoft (OAuth 2.0)"),
        new(AuthenticationType.OAuth2, OAuthProviderKind.Google, "Conta Google (OAuth 2.0)"),
    ];
}
