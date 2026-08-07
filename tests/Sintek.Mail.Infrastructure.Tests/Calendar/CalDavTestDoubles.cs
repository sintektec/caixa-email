using System.Net;
using System.Text;
using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Infrastructure.Tests.Calendar;

/// <summary>Uma resposta roteirizada do servidor.</summary>
/// <param name="Status">Código HTTP.</param>
/// <param name="Body">Corpo, como texto.</param>
/// <param name="ETag">ETag do header, verbatim — inclusive fora da norma, sem aspas.</param>
/// <param name="Location">Destino do redirecionamento.</param>
/// <param name="ContentType">Tipo declarado.</param>
internal sealed record CalDavReply(
    HttpStatusCode Status,
    string Body = "",
    string? ETag = null,
    string? Location = null,
    string ContentType = "application/xml");

/// <summary>Uma requisição efetivamente emitida.</summary>
/// <param name="Method">Método HTTP.</param>
/// <param name="Uri">Endereço.</param>
/// <param name="Body">Corpo enviado.</param>
/// <param name="Headers">Headers da requisição, achatados.</param>
internal sealed record CalDavCapture(
    string Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Servidor CalDAV roteirizado.
/// </summary>
/// <remarks>
/// As respostas são consumidas em fila. O que se verifica aqui é tanto a leitura quanto o
/// que foi <b>emitido</b>: método, header <c>Depth</c>, pré-condição e — no caso do
/// redirecionamento — se o <c>Authorization</c> sobreviveu ao salto de host.
/// </remarks>
internal sealed class ScriptedCalDavHandler : HttpMessageHandler
{
    private readonly Queue<CalDavReply> _replies = new();

    public List<CalDavCapture> Requests { get; } = [];

    public ScriptedCalDavHandler Reply(CalDavReply reply)
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

        Requests.Add(new CalDavCapture(request.Method.Method, request.RequestUri!, body, headers));

        var reply = _replies.Count > 0
            ? _replies.Dequeue()
            : new CalDavReply(HttpStatusCode.NotFound);

        var response = new HttpResponseMessage(reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, reply.ContentType),
        };

        if (reply.ETag is not null)
        {
            // Sem validação: é justamente o ETag fora da norma que precisa chegar ao
            // cliente sem derrubá-lo.
            response.Headers.TryAddWithoutValidation("ETag", reply.ETag);
        }

        if (reply.Location is not null)
        {
            response.Headers.TryAddWithoutValidation("Location", reply.Location);
        }

        return response;
    }
}

/// <summary>Cofre de credenciais em memória.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.TryGetValue(key, out var value) ? value : null);

    public Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.Remove(key));

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.ContainsKey(key));
}
