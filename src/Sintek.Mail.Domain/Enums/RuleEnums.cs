namespace Sintek.Mail.Domain.Enums;

/// <summary>Como combinar as condições de uma regra.</summary>
public enum RuleMatchType
{
    /// <summary>Todas as condições precisam ser satisfeitas (E).</summary>
    All = 0,

    /// <summary>Basta uma condição ser satisfeita (OU).</summary>
    Any = 1,
}

/// <summary>Campo da mensagem avaliado por uma condição de regra.</summary>
public enum RuleField
{
    /// <summary>Endereço do remetente.</summary>
    Sender = 0,

    /// <summary>Endereços em Para.</summary>
    Recipient = 1,

    /// <summary>Endereços em CC.</summary>
    Cc = 2,

    /// <summary>Assunto.</summary>
    Subject = 3,

    /// <summary>Corpo em texto.</summary>
    Body = 4,

    /// <summary>Nome de qualquer anexo.</summary>
    AttachmentName = 5,

    /// <summary>Presença de anexo (condição booleana).</summary>
    HasAttachment = 6,

    /// <summary>Domínio de qualquer participante.</summary>
    ParticipantDomain = 7,

    /// <summary>Tamanho total da mensagem em bytes.</summary>
    Size = 8,

    /// <summary>Data de recebimento.</summary>
    ReceivedAt = 9,

    /// <summary>Prioridade declarada.</summary>
    Importance = 10,

    /// <summary>Conta que recebeu a mensagem.</summary>
    Account = 11,
}

/// <summary>Operador de comparação de uma condição de regra.</summary>
public enum RuleOperator
{
    /// <summary>Contém o texto.</summary>
    Contains = 0,

    /// <summary>Não contém o texto.</summary>
    NotContains = 1,

    /// <summary>É exatamente igual.</summary>
    Equals = 2,

    /// <summary>É diferente.</summary>
    NotEquals = 3,

    /// <summary>Começa com.</summary>
    StartsWith = 4,

    /// <summary>Termina com.</summary>
    EndsWith = 5,

    /// <summary>
    /// Pertence ao domínio informado, respeitando a permissão de subdomínios do
    /// Diretório de Domínio. Não é comparação de texto: usa a regra de domínio.
    /// </summary>
    InDomain = 6,

    /// <summary>É verdadeiro (para campos booleanos como <see cref="RuleField.HasAttachment"/>).</summary>
    IsTrue = 7,

    /// <summary>É falso.</summary>
    IsFalse = 8,

    /// <summary>Maior que (números e datas).</summary>
    GreaterThan = 9,

    /// <summary>Menor que (números e datas).</summary>
    LessThan = 10,
}

/// <summary>Ação executada quando uma regra é satisfeita.</summary>
public enum RuleActionType
{
    /// <summary>Move a mensagem para a pasta indicada.</summary>
    MoveToFolder = 0,

    /// <summary>Copia a mensagem para a pasta indicada.</summary>
    CopyToFolder = 1,

    /// <summary>Aplica a categoria indicada.</summary>
    ApplyCategory = 2,

    /// <summary>Marca como lida.</summary>
    MarkAsRead = 3,

    /// <summary>Marca como importante.</summary>
    MarkAsImportant = 4,

    /// <summary>Aplica sinalizador.</summary>
    Flag = 5,

    /// <summary>Move para a lixeira.</summary>
    Delete = 6,

    /// <summary>Move para a pasta de pendências do Diretório de Domínio.</summary>
    MoveToPending = 7,

    /// <summary>Encaminha para o endereço configurado.</summary>
    Forward = 8,

    /// <summary>Interrompe o processamento das regras seguintes.</summary>
    StopProcessing = 9,
}
