using System.Net;
using System.Text;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>Uma resposta roteirizada de API REST.</summary>
/// <param name="Status">Código HTTP.</param>
/// <param name="Body">Corpo JSON.</param>
/// <param name="ETag">ETag do header, quando houver.</param>
internal sealed record RestReply(HttpStatusCode Status, string Body = "", string? ETag = null);

/// <summary>Uma requisição efetivamente emitida.</summary>
/// <param name="Method">Método HTTP.</param>
/// <param name="Uri">Endereço.</param>
/// <param name="Body">Corpo enviado.</param>
/// <param name="Headers">Headers da requisição, achatados.</param>
internal sealed record RestCapture(
    string Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>API REST roteirizada, com as respostas consumidas em fila.</summary>
internal sealed class ScriptedRestHandler : HttpMessageHandler
{
    private readonly Queue<RestReply> _replies = new();

    public List<RestCapture> Requests { get; } = [];

    public ScriptedRestHandler Reply(RestReply reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = request.Headers
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

        Requests.Add(new RestCapture(request.Method.Method, request.RequestUri!, body, headers));

        var reply = _replies.Count > 0 ? _replies.Dequeue() : new RestReply(HttpStatusCode.NotFound);

        var response = new HttpResponseMessage(reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
        };

        if (reply.ETag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", reply.ETag);
        }

        return response;
    }
}

/// <summary>Provedor de OAuth roteirizado.</summary>
/// <remarks>
/// Guarda os escopos pedidos: parte do que se verifica é que a agenda pede os dela, e não os
/// de IMAP — no Entra ID o token é emitido por recurso, e o do e-mail não abre o Graph.
/// </remarks>
internal sealed class ScriptedOAuthProvider : IOAuthProvider
{
    public required OAuthProviderKind Kind { get; init; }

    /// <summary>Escopos pedidos, na ordem.</summary>
    public List<IReadOnlyCollection<string>> RequestedScopes { get; } = [];

    /// <summary>Se o consentimento vale.</summary>
    public bool HasConsent { get; set; } = true;

    public OAuthProviderKind Provider => Kind;

    public bool IsConfigured => true;

    public Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default)
        => GetAccessTokenAsync(emailAddress, cancellationToken);

    public Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default)
        => GetAccessTokenAsync(emailAddress, [], cancellationToken);

    public Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        RequestedScopes.Add(scopes);

        if (!HasConsent)
        {
            throw new ReauthenticationRequiredException(emailAddress);
        }

        return Task.FromResult(new OAuthAccessToken(
            FakeSecret.For("token-de-acesso"), DateTimeOffset.MaxValue));
    }

    public Task SignOutAsync(string emailAddress, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Registro de provedores de OAuth alimentado por tabela.</summary>
internal sealed class ScriptedOAuthRegistry : IOAuthProviderRegistry
{
    private readonly Dictionary<OAuthProviderKind, IOAuthProvider> _providers;

    public ScriptedOAuthRegistry(params IOAuthProvider[] providers)
        => _providers = providers.ToDictionary(p => p.Provider);

    public IReadOnlyList<IOAuthProvider> ConfiguredProviders => [.. _providers.Values];

    public IOAuthProvider? Resolve(OAuthProviderKind provider)
        => _providers.GetValueOrDefault(provider);
}

/// <summary>Relógio fixo, para tornar os testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
