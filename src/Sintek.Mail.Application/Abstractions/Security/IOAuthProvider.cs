using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Abstractions.Security;

/// <summary>Token de acesso obtido de um provedor OAuth 2.0.</summary>
/// <param name="AccessToken">Token a ser apresentado via SASL XOAUTH2.</param>
/// <param name="ExpiresAt">Quando o token deixa de valer.</param>
public readonly record struct OAuthAccessToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Indica se o token ainda serve, com folga para o tempo da própria requisição.
    /// </summary>
    /// <remarks>
    /// A folga de dois minutos evita a corrida em que o token vence entre a verificação e
    /// o comando IMAP que o utiliza — falha que se manifestaria como desconexão aleatória.
    /// </remarks>
    public bool IsUsable(DateTimeOffset now) => ExpiresAt - now > TimeSpan.FromMinutes(2);
}

/// <summary>
/// Obtém e renova tokens OAuth 2.0 para autenticação IMAP e SMTP.
/// </summary>
/// <remarks>
/// Cada provedor concreto (Microsoft Entra ID, Google) implementa esta interface. O
/// registro é feito por <see cref="Provider"/>, de modo que acrescentar um provedor novo
/// não exige alterar nenhum consumidor.
/// </remarks>
public interface IOAuthProvider
{
    /// <summary>Provedor atendido por esta implementação.</summary>
    OAuthProviderKind Provider { get; }

    /// <summary>
    /// Indica se o provedor está configurado.
    /// </summary>
    /// <remarks>
    /// Falso quando não há Client ID registrado. A interface usa este sinal para explicar
    /// que a opção existe mas precisa ser configurada, em vez de falhar na autenticação.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>
    /// Executa o fluxo interativo de consentimento e guarda o token de atualização.
    /// </summary>
    Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devolve um token válido, renovando-o em silêncio quando possível.
    /// </summary>
    /// <exception cref="ReauthenticationRequiredException">
    /// O token de atualização não vale mais e o usuário precisa consentir de novo.
    /// </exception>
    Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>Revoga o consentimento e descarta os tokens guardados.</summary>
    Task SignOutAsync(string emailAddress, CancellationToken cancellationToken = default);
}

/// <summary>Resolve o provedor OAuth adequado a uma conta.</summary>
public interface IOAuthProviderRegistry
{
    /// <summary>
    /// Devolve o provedor correspondente, ou <see langword="null"/> quando não há
    /// implementação registrada para ele.
    /// </summary>
    IOAuthProvider? Resolve(OAuthProviderKind provider);

    /// <summary>Lista os provedores efetivamente configurados.</summary>
    IReadOnlyList<IOAuthProvider> ConfiguredProviders { get; }
}

/// <summary>
/// O consentimento do usuário precisa ser renovado: o token de atualização expirou ou
/// foi revogado.
/// </summary>
public sealed class ReauthenticationRequiredException : Exception
{
    public ReauthenticationRequiredException(string emailAddress)
        : base($"A conta '{emailAddress}' precisa ser autenticada novamente.")
        => EmailAddress = emailAddress;

    public ReauthenticationRequiredException(string emailAddress, Exception innerException)
        : base($"A conta '{emailAddress}' precisa ser autenticada novamente.", innerException)
        => EmailAddress = emailAddress;

    /// <summary>Conta que precisa de nova autenticação.</summary>
    public string EmailAddress { get; }
}
