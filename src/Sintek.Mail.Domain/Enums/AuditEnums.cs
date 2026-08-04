namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Tipo de evento registrado na auditoria.
/// </summary>
/// <remarks>
/// A auditoria registra o que aconteceu, não o conteúdo das mensagens. Nenhum evento
/// pode gravar assunto, corpo ou anexo — apenas identificadores e o motivo da decisão.
/// </remarks>
public enum AuditEventType
{
    /// <summary>Diretório de Domínio criado.</summary>
    DomainDirectoryCreated = 0,

    /// <summary>Diretório de Domínio alterado.</summary>
    DomainDirectoryUpdated = 1,

    /// <summary>Diretório de Domínio removido.</summary>
    DomainDirectoryDeleted = 2,

    /// <summary>
    /// O nome do domínio de um diretório mudou, disparando revalidação de contas e
    /// mensagens.
    /// </summary>
    DomainNameChanged = 3,

    /// <summary>Conta vinculada a um Diretório de Domínio.</summary>
    AccountLinked = 4,

    /// <summary>Tentativa de vincular conta recusada por divergência de domínio.</summary>
    AccountRejectedByDomainRule = 5,

    /// <summary>Conta removida.</summary>
    AccountRemoved = 6,

    /// <summary>Movimentação de mensagem recusada pela regra de domínio da pasta.</summary>
    MessageMoveBlockedByDomainRule = 7,

    /// <summary>Mensagem incompatível desviada para a pasta de pendências.</summary>
    MessageMovedToPending = 8,

    /// <summary>
    /// Movimentação incompatível autorizada pelo usuário (modo
    /// <see cref="InvalidEmailAction.WarnAndConfirm"/>) ou apenas registrada (modo
    /// <see cref="InvalidEmailAction.LogOnly"/>).
    /// </summary>
    MessageMoveOverridden = 9,

    /// <summary>Restrição de domínio aplicada a uma pasta.</summary>
    FolderRestrictionChanged = 10,

    /// <summary>Mensagens expurgadas definitivamente.</summary>
    MessagesPurged = 11,

    /// <summary>Dados locais apagados pelo usuário.</summary>
    LocalDataCleared = 12,

    /// <summary>Falha de autenticação em uma conta.</summary>
    AuthenticationFailed = 13,

    /// <summary>Operação da fila de saída esgotou as tentativas.</summary>
    OutboxOperationDead = 14,
}

/// <summary>Gravidade de um evento de auditoria.</summary>
public enum AuditSeverity
{
    /// <summary>Registro informativo do curso normal de uso.</summary>
    Information = 0,

    /// <summary>Algo que o usuário deveria revisar.</summary>
    Warning = 1,

    /// <summary>Falha que impediu a conclusão de uma operação.</summary>
    Error = 2,
}
