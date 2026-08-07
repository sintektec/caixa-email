using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;

namespace Sintek.Mail.Infrastructure.Mail.Autodiscover;

/// <summary>Consulta registros SRV usando o resolvedor configurado no sistema.</summary>
/// <remarks>
/// O .NET não expõe consulta SRV na biblioteca padrão — <c>Dns.GetHostEntry</c> resolve
/// apenas A e AAAA. Daí a dependência do <c>DnsClient</c>, que fala o protocolo
/// diretamente.
/// </remarks>
public sealed class DnsClientResolver : IDnsResolver
{
    private readonly ILookupClient _lookup;
    private readonly ILogger<DnsClientResolver> _logger;

    public DnsClientResolver(ILogger<DnsClientResolver> logger)
        : this(
            new LookupClient(new LookupClientOptions
            {
                // Uma tentativa e poucos segundos: a descoberta tem outras estratégias
                // depois desta, e travar o assistente esperando um DNS que não responde é
                // pior do que seguir para a próxima.
                Timeout = TimeSpan.FromSeconds(4),
                Retries = 1,
                UseCache = true,
                ThrowDnsErrors = false,
            }),
            logger)
    {
    }

    internal DnsClientResolver(ILookupClient lookup, ILogger<DnsClientResolver> logger)
    {
        _lookup = lookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DnsServiceRecord>> ResolveServiceAsync(
        string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        try
        {
            var response = await _lookup
                .QueryAsync(serviceName, QueryType.SRV, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response.HasError)
            {
                return [];
            }

            return response.Answers
                .OfType<SrvRecord>()
                .Select(r => new DnsServiceRecord(r.Target.Value.TrimEnd('.'), r.Port, r.Priority, r.Weight))
                .ToList();
        }
        catch (DnsResponseException ex)
        {
            _logger.LogDebug(ex, "Consulta SRV a {ServiceName} falhou.", serviceName);
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Tempo esgotado do próprio resolvedor, não cancelamento do usuário.
            _logger.LogDebug("Consulta SRV a {ServiceName} esgotou o tempo.", serviceName);
            return [];
        }
    }
}
