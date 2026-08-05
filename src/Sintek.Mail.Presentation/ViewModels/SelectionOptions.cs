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

/// <summary>Opção de filtro em três estados: sim, não ou indiferente.</summary>
/// <param name="Value">Valor do filtro; nulo não filtra.</param>
/// <param name="Label">Texto apresentado ao usuário.</param>
public sealed record TriStateFilterOption(bool? Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record ImportanceFilterOption(MessageImportance? Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record SyncStateFilterOption(MessageSyncState? Value, string Label);

/// <summary>Opção de conta ou de Diretório de Domínio no filtro de pesquisa.</summary>
/// <param name="Value">Identificador; nulo significa todas.</param>
/// <param name="Label">Texto apresentado ao usuário.</param>
public sealed record ScopeFilterOption(Guid? Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record RuleFieldOption(RuleField Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record RuleOperatorOption(RuleOperator Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record RuleActionOption(RuleActionType Value, string Label);

/// <inheritdoc cref="ValidationModeOption" />
public sealed record RuleMatchTypeOption(RuleMatchType Value, string Label);

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

    /// <summary>Filtro de leitura da pesquisa avançada.</summary>
    public static IReadOnlyList<TriStateFilterOption> ReadStateFilters { get; } =
    [
        new(null, "Lidas e não lidas"),
        new(false, "Somente não lidas"),
        new(true, "Somente lidas"),
    ];

    /// <summary>Filtro de sinalizador da pesquisa avançada.</summary>
    public static IReadOnlyList<TriStateFilterOption> FlagStateFilters { get; } =
    [
        new(null, "Com e sem sinalizador"),
        new(true, "Somente sinalizadas"),
        new(false, "Somente sem sinalizador"),
    ];

    /// <summary>Filtro de anexos da pesquisa avançada.</summary>
    public static IReadOnlyList<TriStateFilterOption> AttachmentFilters { get; } =
    [
        new(null, "Com e sem anexo"),
        new(true, "Somente com anexo"),
        new(false, "Somente sem anexo"),
    ];

    /// <summary>Filtro de importância da pesquisa avançada.</summary>
    public static IReadOnlyList<ImportanceFilterOption> ImportanceFilters { get; } =
    [
        new(null, "Qualquer importância"),
        new(MessageImportance.High, "Alta"),
        new(MessageImportance.Normal, "Normal"),
        new(MessageImportance.Low, "Baixa"),
    ];

    /// <summary>Campos disponíveis nas condições de regra, na ordem da seção 6.5.</summary>
    public static IReadOnlyList<RuleFieldOption> RuleFields { get; } =
    [
        new(RuleField.Sender, "Remetente"),
        new(RuleField.Recipient, "Destinatário (Para)"),
        new(RuleField.Cc, "Em cópia (CC)"),
        new(RuleField.Subject, "Assunto"),
        new(RuleField.Body, "Corpo da mensagem"),
        new(RuleField.AttachmentName, "Nome do anexo"),
        new(RuleField.HasAttachment, "Presença de anexo"),
        new(RuleField.ParticipantDomain, "Domínio de participante"),
        new(RuleField.Size, "Tamanho (bytes)"),
        new(RuleField.Importance, "Importância"),
    ];

    /// <summary>Operadores de comparação das condições.</summary>
    public static IReadOnlyList<RuleOperatorOption> RuleOperators { get; } =
    [
        new(RuleOperator.Contains, "Contém"),
        new(RuleOperator.NotContains, "Não contém"),
        new(RuleOperator.Equals, "É igual a"),
        new(RuleOperator.NotEquals, "É diferente de"),
        new(RuleOperator.StartsWith, "Começa com"),
        new(RuleOperator.EndsWith, "Termina com"),
        new(RuleOperator.InDomain, "Pertence ao domínio"),
        new(RuleOperator.IsTrue, "Sim"),
        new(RuleOperator.IsFalse, "Não"),
        new(RuleOperator.GreaterThan, "Maior que"),
        new(RuleOperator.LessThan, "Menor que"),
    ];

    /// <summary>Ações disponíveis nas regras.</summary>
    public static IReadOnlyList<RuleActionOption> RuleActions { get; } =
    [
        new(RuleActionType.MoveToFolder, "Mover para a pasta"),
        new(RuleActionType.CopyToFolder, "Copiar para a pasta"),
        new(RuleActionType.Forward, "Encaminhar para o endereço"),
        new(RuleActionType.ApplyCategory, "Aplicar a categoria"),
        new(RuleActionType.MarkAsRead, "Marcar como lida"),
        new(RuleActionType.MarkAsImportant, "Marcar como importante"),
        new(RuleActionType.Flag, "Aplicar sinalizador"),
        new(RuleActionType.MoveToPending, "Mover para pendências"),
        new(RuleActionType.Delete, "Mover para a lixeira"),
        new(RuleActionType.StopProcessing, "Interromper as regras seguintes"),
    ];

    /// <summary>Modos de combinação das condições.</summary>
    public static IReadOnlyList<RuleMatchTypeOption> RuleMatchTypes { get; } =
    [
        new(RuleMatchType.All, "Todas as condições (E)"),
        new(RuleMatchType.Any, "Qualquer condição (OU)"),
    ];

    /// <summary>Filtro de status de sincronização da pesquisa avançada.</summary>
    public static IReadOnlyList<SyncStateFilterOption> SyncStateFilters { get; } =
    [
        new(null, "Qualquer situação"),
        new(MessageSyncState.Synced, "Sincronizada"),
        new(MessageSyncState.LocalOnly, "Somente local"),
        new(MessageSyncState.PendingUpload, "Envio pendente"),
        new(MessageSyncState.PendingUpdate, "Alteração pendente"),
        new(MessageSyncState.PendingMove, "Movimentação pendente"),
        new(MessageSyncState.PendingDelete, "Exclusão pendente"),
        new(MessageSyncState.Conflict, "Em conflito"),
    ];
}
