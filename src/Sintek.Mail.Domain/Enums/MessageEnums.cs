namespace Sintek.Mail.Domain.Enums;

/// <summary>Papel de uma pasta dentro da conta.</summary>
public enum FolderType
{
    /// <summary>Pasta criada pelo usuário.</summary>
    Custom = 0,

    /// <summary>Caixa de Entrada.</summary>
    Inbox = 1,

    /// <summary>Itens Enviados.</summary>
    Sent = 2,

    /// <summary>Rascunhos.</summary>
    Drafts = 3,

    /// <summary>Lixeira. Mensagens aqui podem ser restauradas.</summary>
    Trash = 4,

    /// <summary>Spam / Lixo Eletrônico.</summary>
    Junk = 5,

    /// <summary>Arquivados.</summary>
    Archive = 6,

    /// <summary>
    /// Pendências: destino das mensagens que não satisfazem a regra de domínio de uma
    /// pasta restrita. É uma pasta local — não existe no servidor IMAP.
    /// </summary>
    Pending = 7,

    /// <summary>Modelos de mensagem.</summary>
    Templates = 8,

    /// <summary>Caixa de saída: mensagens aguardando envio na fila offline.</summary>
    Outbox = 9,
}

/// <summary>Em que campo de endereçamento um participante aparece.</summary>
public enum AddressKind
{
    /// <summary>Remetente (cabeçalho From).</summary>
    From = 0,

    /// <summary>Destinatário direto (Para / To).</summary>
    To = 1,

    /// <summary>Cópia (CC).</summary>
    Cc = 2,

    /// <summary>Cópia oculta (CCO / BCC). Só existe nas mensagens que nós enviamos.</summary>
    Bcc = 3,

    /// <summary>Endereço de resposta (Reply-To).</summary>
    ReplyTo = 4,

    /// <summary>Remetente efetivo (Sender), quando difere de From.</summary>
    Sender = 5,
}

/// <summary>Prioridade declarada da mensagem.</summary>
public enum MessageImportance
{
    /// <summary>Baixa.</summary>
    Low = 0,

    /// <summary>Normal. Padrão.</summary>
    Normal = 1,

    /// <summary>Alta.</summary>
    High = 2,
}

/// <summary>
/// Situação de uma mensagem perante o servidor — o que sustenta o modo offline-first.
/// </summary>
/// <remarks>
/// Toda ação do usuário grava imediatamente no banco local e marca a mensagem com o
/// estado pendente correspondente. A fila de saída (<c>OutboxOperation</c>) é quem
/// depois reconcilia com o servidor.
/// </remarks>
public enum MessageSyncState
{
    /// <summary>Idêntica ao servidor.</summary>
    Synced = 0,

    /// <summary>Criada localmente e ainda não existe no servidor (rascunho, por exemplo).</summary>
    LocalOnly = 1,

    /// <summary>Precisa ser enviada ao servidor (APPEND).</summary>
    PendingUpload = 2,

    /// <summary>Teve marcadores ou categorias alterados localmente.</summary>
    PendingUpdate = 3,

    /// <summary>Foi movida localmente para outra pasta.</summary>
    PendingMove = 4,

    /// <summary>Foi excluída localmente.</summary>
    PendingDelete = 5,

    /// <summary>
    /// Alterada dos dois lados desde a última sincronização. Exige decisão explícita e
    /// fica visível na interface em vez de ser resolvida em silêncio.
    /// </summary>
    Conflict = 6,
}
