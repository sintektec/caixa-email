using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Infrastructure.Mail.Autodiscover;

/// <summary>
/// Descobre servidores pelos registros SRV do DNS, conforme a RFC 6186.
/// </summary>
/// <remarks>
/// <para>
/// São quatro serviços: <c>_imaps</c> e <c>_imap</c> para leitura, <c>_submissions</c> e
/// <c>_submission</c> para envio. As variantes com "s" usam TLS desde a conexão; as outras,
/// STARTTLS.
/// </para>
/// <para>
/// A variante cifrada é consultada primeiro e vence quando existe. A RFC permite anunciar
/// as duas, e o servidor que anuncia ambas normalmente aceita as duas — escolher a cifrada
/// é a decisão certa mesmo quando o registro em claro tem prioridade menor.
/// </para>
/// </remarks>
public sealed class DnsSrvLocator
{
    private readonly IDnsResolver _resolver;
    private readonly ILogger<DnsSrvLocator> _logger;

    public DnsSrvLocator(IDnsResolver resolver, ILogger<DnsSrvLocator> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Devolve a configuração anunciada pelo domínio, ou <see langword="null"/> quando não
    /// há registros suficientes para montar um par IMAP/SMTP.
    /// </summary>
    public async Task<DiscoveredServerSettings?> LocateAsync(
        EmailDomain domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var imap = await SelectAsync($"_imaps._tcp.{domain.Value}", cancellationToken).ConfigureAwait(false);
        var imapSecurity = SecureSocketMode.SslOnConnect;

        if (imap is null)
        {
            imap = await SelectAsync($"_imap._tcp.{domain.Value}", cancellationToken).ConfigureAwait(false);
            imapSecurity = SecureSocketMode.StartTls;
        }

        var smtp = await SelectAsync($"_submissions._tcp.{domain.Value}", cancellationToken).ConfigureAwait(false);
        var smtpSecurity = SecureSocketMode.SslOnConnect;

        if (smtp is null)
        {
            smtp = await SelectAsync($"_submission._tcp.{domain.Value}", cancellationToken).ConfigureAwait(false);
            smtpSecurity = SecureSocketMode.StartTls;
        }

        if (imap is null || smtp is null)
        {
            return null;
        }

        _logger.LogInformation(
            "Registros SRV encontrados para {Domain}: IMAP {ImapHost}:{ImapPort}, SMTP {SmtpHost}:{SmtpPort}.",
            domain.Value, imap.Value.Target, imap.Value.Port, smtp.Value.Target, smtp.Value.Port);

        // O SRV diz onde conectar, nunca como autenticar — não há campo para isso na RFC.
        // Senha é o padrão seguro: um domínio que exigisse OAuth estaria na tabela de
        // provedores conhecidos ou publicaria autoconfig, ambos consultados antes daqui.
        return new DiscoveredServerSettings(
            imap.Value.Target,
            imap.Value.Port,
            imapSecurity,
            smtp.Value.Target,
            smtp.Value.Port,
            smtpSecurity,
            AuthenticationType.Password,
            OAuthProviderKind.None,
            DiscoverySource.DnsSrv,
            RequiresUserConfirmation:
                !ClientConfigParser.HostBelongsToDomain(imap.Value.Target, domain)
                || !ClientConfigParser.HostBelongsToDomain(smtp.Value.Target, domain));
    }

    /// <summary>
    /// Escolhe o melhor registro: menor prioridade e, em caso de empate, maior peso.
    /// </summary>
    /// <remarks>
    /// A RFC 2782 manda sortear entre pesos iguais para distribuir carga. Aqui a escolha é
    /// determinística de propósito: o usuário configura a conta uma vez, e um sorteio faria
    /// duas execuções do assistente proporem servidores diferentes para o mesmo endereço —
    /// comportamento que ninguém consegue diagnosticar.
    /// </remarks>
    private async Task<DnsServiceRecord?> SelectAsync(string serviceName, CancellationToken cancellationToken)
    {
        var records = await _resolver.ResolveServiceAsync(serviceName, cancellationToken).ConfigureAwait(false);

        return records
            .Where(r => !r.IsServiceUnavailable && r.Port is > 0 and <= 65535)
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.Weight)
            .ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .Cast<DnsServiceRecord?>()
            .FirstOrDefault();
    }
}
