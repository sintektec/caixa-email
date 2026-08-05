namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Resultado de uma verificação de autenticação do remetente.
/// </summary>
/// <remarks>
/// Os valores seguem o vocabulário do cabeçalho <c>Authentication-Results</c> (RFC 8601).
/// <see cref="None"/> e <see cref="Unknown"/> são coisas diferentes e a distinção importa:
/// "o domínio não publica a política" não é o mesmo que "não sabemos, o servidor não disse".
/// </remarks>
public enum AuthenticationResult
{
    /// <summary>O servidor não informou resultado para esta verificação.</summary>
    Unknown = 0,

    /// <summary>O domínio não publica política para esta verificação.</summary>
    None = 1,

    /// <summary>A verificação passou.</summary>
    Pass = 2,

    /// <summary>A verificação falhou.</summary>
    Fail = 3,

    /// <summary>Falha branda: o domínio pede para não rejeitar, apenas marcar.</summary>
    SoftFail = 4,

    /// <summary>O domínio declara neutralidade.</summary>
    Neutral = 5,

    /// <summary>Erro temporário na verificação. Nada se conclui.</summary>
    TemporaryError = 6,

    /// <summary>Erro permanente de configuração no domínio do remetente.</summary>
    PermanentError = 7,
}

/// <summary>
/// Grau de confiança atribuído a uma mensagem recebida.
/// </summary>
/// <remarks>
/// Serve à faixa exibida no painel de leitura. O produto <b>não</b> classifica spam por conta
/// própria — isso é trabalho do servidor, que tem dados que nenhum cliente desktop tem. O que
/// esta escala expressa é o que o servidor disse somado ao que se pode verificar localmente
/// sobre disfarce de identidade.
/// </remarks>
public enum SenderTrustLevel
{
    /// <summary>Nada a destacar: autenticação em ordem ou ausente sem sinais contrários.</summary>
    Neutral = 0,

    /// <summary>Autenticação completa: SPF e DKIM passaram e o DMARC está alinhado.</summary>
    Authenticated = 1,

    /// <summary>Autenticação falhou. A mensagem pode não ser de quem diz ser.</summary>
    AuthenticationFailed = 2,

    /// <summary>
    /// O nome exibido imita um contato conhecido, mas o domínio é outro. É o vetor de
    /// phishing que mais funciona na prática.
    /// </summary>
    DisplayNameSpoofing = 3,

    /// <summary>O servidor classificou a mensagem como lixo eletrônico.</summary>
    FlaggedAsSpam = 4,
}
