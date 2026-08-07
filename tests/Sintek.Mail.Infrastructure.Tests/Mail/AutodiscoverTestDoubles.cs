using System.Net;
using Sintek.Mail.Application.Abstractions.Mail;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>
/// Responde requisições HTTP a partir de uma tabela, registrando o que foi pedido.
/// </summary>
/// <remarks>
/// O registro das URIs importa tanto quanto a resposta: parte do que se verifica aqui é
/// <b>o que não é consultado</b> — que um provedor conhecido não gera tráfego algum e que o
/// endereço completo do usuário não chega ao ISPDB.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _documentsByUrl;

    public StubHttpMessageHandler(Dictionary<string, string>? documentsByUrl = null)
        => _documentsByUrl = documentsByUrl ?? [];

    /// <summary>URIs efetivamente requisitadas, na ordem.</summary>
    public List<Uri> RequestedUris { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        RequestedUris.Add(uri);

        if (_documentsByUrl.TryGetValue(uri.AbsoluteUri, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Resolvedor de SRV alimentado por tabela, sem tocar na rede.</summary>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Dictionary<string, IReadOnlyList<DnsServiceRecord>> _recordsByService;

    public FakeDnsResolver(Dictionary<string, IReadOnlyList<DnsServiceRecord>>? recordsByService = null)
        => _recordsByService = recordsByService ?? [];

    /// <summary>Serviços consultados, na ordem.</summary>
    public List<string> QueriedServices { get; } = [];

    public Task<IReadOnlyList<DnsServiceRecord>> ResolveServiceAsync(
        string serviceName, CancellationToken cancellationToken = default)
    {
        QueriedServices.Add(serviceName);

        return Task.FromResult(
            _recordsByService.TryGetValue(serviceName, out var records) ? records : []);
    }
}

/// <summary>Documentos de autoconfiguração usados pelos testes.</summary>
internal static class ClientConfigSamples
{
    /// <summary>Configuração comum: IMAP com TLS, SMTP com STARTTLS, autenticação por senha.</summary>
    public static string PasswordConfig(string domain, string imapHost, string smtpHost) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <clientConfig version="1.1">
          <emailProvider id="{domain}">
            <domain>{domain}</domain>
            <displayName>{domain}</displayName>
            <incomingServer type="imap">
              <hostname>{imapHost}</hostname>
              <port>993</port>
              <socketType>SSL</socketType>
              <authentication>password-cleartext</authentication>
              <username>%EMAILADDRESS%</username>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>{smtpHost}</hostname>
              <port>587</port>
              <socketType>STARTTLS</socketType>
              <authentication>password-cleartext</authentication>
              <username>%EMAILADDRESS%</username>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;
}
