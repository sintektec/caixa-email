namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Operação registrada na fila de saída para ser reconciliada com o servidor quando
/// houver conexão.
/// </summary>
public enum OutboxOperationType
{
    /// <summary>Enviar uma mensagem via SMTP.</summary>
    SendMessage = 0,

    /// <summary>Marcar como lida (flag \Seen).</summary>
    MarkAsRead = 1,

    /// <summary>Marcar como não lida.</summary>
    MarkAsUnread = 2,

    /// <summary>Aplicar sinalizador (flag \Flagged).</summary>
    SetFlag = 3,

    /// <summary>Remover sinalizador.</summary>
    ClearFlag = 4,

    /// <summary>Mover entre pastas.</summary>
    MoveMessage = 5,

    /// <summary>Copiar para outra pasta.</summary>
    CopyMessage = 6,

    /// <summary>Marcar para exclusão (flag \Deleted).</summary>
    DeleteMessage = 7,

    /// <summary>Expurgar definitivamente (EXPUNGE).</summary>
    ExpungeFolder = 8,

    /// <summary>Criar pasta no servidor.</summary>
    CreateFolder = 9,

    /// <summary>Renomear pasta no servidor.</summary>
    RenameFolder = 10,

    /// <summary>Excluir pasta no servidor.</summary>
    DeleteFolder = 11,

    /// <summary>Assinar/desassinar pasta.</summary>
    SetFolderSubscription = 12,

    /// <summary>Gravar rascunho no servidor (APPEND).</summary>
    AppendDraft = 13,
}

/// <summary>Situação de uma operação na fila de saída.</summary>
public enum OutboxOperationStatus
{
    /// <summary>Aguardando processamento.</summary>
    Pending = 0,

    /// <summary>Sendo processada agora.</summary>
    InProgress = 1,

    /// <summary>Concluída com sucesso.</summary>
    Completed = 2,

    /// <summary>
    /// Falhou de forma temporária e será tentada de novo em <c>NextAttemptAt</c>.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Falhou de forma definitiva (esgotou as tentativas ou o servidor recusou de modo
    /// permanente). Exige intervenção do usuário e fica visível na fila.
    /// </summary>
    Dead = 4,

    /// <summary>Cancelada pelo usuário antes de ser executada.</summary>
    Cancelled = 5,
}
