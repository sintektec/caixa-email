using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Infrastructure.Mail;
using Sintek.Mail.Infrastructure.Mail.Autodiscover;
using Sintek.Mail.Infrastructure.Security;
using Sintek.Mail.Infrastructure.Sync;

namespace Sintek.Mail.Infrastructure;

/// <summary>Registro da infraestrutura multiplataforma.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra transporte de e-mail, provedores OAuth, sanitização e motor de
    /// sincronização.
    /// </summary>
    /// <remarks>
    /// O <see cref="ICredentialStore"/> NÃO é registrado aqui: a implementação real é
    /// específica do Windows e vem de <c>Sintek.Mail.Infrastructure.Windows</c>. Manter
    /// essa separação é o que permite testar esta camada em Linux.
    /// </remarks>
    public static IServiceCollection AddSintekMailInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OAuthOptions>(configuration.GetSection(OAuthOptions.SectionName));

        services.AddSingleton<IHtmlSanitizer, MessageHtmlSanitizer>();

        // A descoberta automática fala com hosts escolhidos pelo domínio que o usuário
        // digitou. O HttpClient dela é próprio e restrito: sem redirecionamento automático
        // — que poderia levar de um endereço HTTPS conferido para outro qualquer — e com
        // tempo curto, porque há outras estratégias esperando a vez.
        services.AddHttpClient<AutoconfigFetcher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sintek.Mail/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });

        services.AddSingleton<IDnsResolver, DnsClientResolver>();
        services.AddSingleton<DnsSrvLocator>();

        // Transitório, não singleton: o AutoconfigFetcher é um cliente tipado, e prendê-lo
        // dentro de um singleton anularia a rotação de handlers que a fábrica de HttpClient
        // existe para fazer. O serviço não guarda estado, então nada se perde.
        services.AddTransient<IAutodiscoverService, AutodiscoverService>();

        // Os dois provedores são sempre registrados, mesmo sem Client ID: o registro
        // expõe IsConfigured, que a interface usa para explicar que a opção existe e
        // precisa ser configurada — melhor do que sumir com ela do menu.
        services.AddSingleton<IOAuthProvider, MicrosoftOAuthProvider>();
        services.AddSingleton<IOAuthProvider, GoogleOAuthProvider>();
        services.AddSingleton<IOAuthProviderRegistry, OAuthProviderRegistry>();

        services.AddSingleton<MailKitAuthenticator>();
        services.AddScoped<IImapClient, MailKitImapClient>();
        services.AddScoped<ISmtpSender, MailKitSmtpSender>();

        services.AddScoped<OutboxProcessor>();

        return services;
    }
}
