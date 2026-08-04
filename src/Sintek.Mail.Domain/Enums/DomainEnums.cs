namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Define quais participantes de uma mensagem são considerados ao decidir se ela
/// pertence a um Diretório de Domínio.
/// </summary>
/// <remarks>
/// Os valores são numerados explicitamente porque são persistidos como inteiros no
/// banco: reordenar a declaração não pode remapear dados já gravados.
/// </remarks>
public enum DomainValidationMode
{
    /// <summary>Só o remetente precisa pertencer ao domínio.</summary>
    SenderOnly = 0,

    /// <summary>Só os destinatários (Para) precisam pertencer ao domínio.</summary>
    RecipientOnly = 1,

    /// <summary>Basta que o remetente OU um destinatário pertença ao domínio.</summary>
    SenderOrRecipient = 2,

    /// <summary>Exige que o remetente E ao menos um destinatário pertençam ao domínio.</summary>
    SenderAndRecipient = 3,

    /// <summary>
    /// Aceita qualquer participante: remetente, destinatários, cópia e cópia oculta.
    /// É o modo mais permissivo.
    /// </summary>
    AnyParticipant = 4,
}

/// <summary>
/// O que fazer quando o usuário tenta colocar em uma pasta restrita uma mensagem que
/// não pertence ao domínio configurado.
/// </summary>
public enum InvalidEmailAction
{
    /// <summary>Impede a operação e explica o motivo. Padrão.</summary>
    Block = 0,

    /// <summary>Alerta o usuário e conclui a operação apenas se ele confirmar.</summary>
    WarnAndConfirm = 1,

    /// <summary>Desvia a mensagem para a pasta de pendências do diretório.</summary>
    MoveToPending = 2,

    /// <summary>
    /// Permite a operação e apenas registra a ocorrência na auditoria. Útil em uma
    /// migração, para medir o impacto de uma regra antes de passar a aplicá-la.
    /// </summary>
    LogOnly = 3,
}
