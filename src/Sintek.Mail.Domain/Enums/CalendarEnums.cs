namespace Sintek.Mail.Domain.Enums;

/// <summary>Situação de um evento, conforme o <c>STATUS</c> da RFC 5545.</summary>
public enum CalendarEventStatus
{
    /// <summary>Confirmado.</summary>
    Confirmed = 0,

    /// <summary>Provisório — o organizador ainda pode mudar.</summary>
    Tentative = 1,

    /// <summary>Cancelado. O evento é preservado, não apagado.</summary>
    Cancelled = 2,
}

/// <summary>Papel de um participante, conforme o parâmetro <c>ROLE</c>.</summary>
public enum AttendeeRole
{
    /// <summary>Presença esperada (<c>REQ-PARTICIPANT</c>).</summary>
    Required = 0,

    /// <summary>Presença opcional (<c>OPT-PARTICIPANT</c>).</summary>
    Optional = 1,

    /// <summary>Apenas informado (<c>NON-PARTICIPANT</c>).</summary>
    Informational = 2,

    /// <summary>Preside a reunião (<c>CHAIR</c>).</summary>
    Chair = 3,
}

/// <summary>Resposta de um participante, conforme o parâmetro <c>PARTSTAT</c>.</summary>
public enum AttendeeResponse
{
    /// <summary>Ainda não respondeu (<c>NEEDS-ACTION</c>).</summary>
    NeedsAction = 0,

    /// <summary>Aceitou (<c>ACCEPTED</c>).</summary>
    Accepted = 1,

    /// <summary>Recusou (<c>DECLINED</c>).</summary>
    Declined = 2,

    /// <summary>Aceitou provisoriamente (<c>TENTATIVE</c>).</summary>
    Tentative = 3,

    /// <summary>Delegou a outra pessoa (<c>DELEGATED</c>).</summary>
    Delegated = 4,
}

/// <summary>
/// Intenção declarada no <c>METHOD</c> do iCalendar.
/// </summary>
/// <remarks>
/// É o que distingue um convite de um cancelamento e de uma resposta. Teams, Outlook e
/// Meet usam os mesmos valores — é por isso que uma implementação da norma cobre os três.
/// </remarks>
public enum CalendarMethod
{
    /// <summary>Divulgação sem pedido de resposta (<c>PUBLISH</c>).</summary>
    Publish = 0,

    /// <summary>Convite: cria ou atualiza o evento (<c>REQUEST</c>).</summary>
    Request = 1,

    /// <summary>Resposta de um participante (<c>REPLY</c>).</summary>
    Reply = 2,

    /// <summary>Cancelamento pelo organizador (<c>CANCEL</c>).</summary>
    Cancel = 3,

    /// <summary>Pedido de novo horário por um participante (<c>COUNTER</c>).</summary>
    Counter = 4,

    /// <summary>Método presente mas não tratado por este produto.</summary>
    Unsupported = 99,
}
