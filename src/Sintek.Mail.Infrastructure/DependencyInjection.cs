using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Sync;
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
        services.Configure<Assistant.AssistantOptions>(
            configuration.GetSection(Assistant.AssistantOptions.SectionName));

        services.AddSingleton<IHtmlSanitizer, MessageHtmlSanitizer>();

        // Leitura e escrita de iCalendar. Singleton porque não guarda estado: cada chamada
        // trabalha só sobre o documento recebido.
        services.AddSingleton<
            Application.Abstractions.Calendar.ICalendarSerializer,
            Calendar.IcalNetCalendarSerializer>();

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

        // O cliente CalDAV fala com um host escolhido pelo endereço que o usuário digitou.
        // O HttpClient dele é próprio e restrito, pelo mesmo motivo do da autoconfiguração —
        // e com uma exigência a mais: sem redirecionamento automático, porque o HttpClient
        // transforma PROPFIND em GET ao seguir 301 e descarta o Authorization quando o
        // destino é outro host, que é justamente o caso do iCloud.
        services.AddHttpClient<Calendar.CalDav.CalDavTransport>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sintek.Mail/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            PreAuthenticate = false,
        });

        services.AddTransient<
            Application.Abstractions.Calendar.ICalendarSyncProvider,
            Calendar.CalDav.CalDavCalendarSyncProvider>();

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
        services.AddSingleton<IMimeMessageWriter, MimeMessageWriter>();

        services.AddScoped<OutboxProcessor>();
        services.AddScoped<IOutboxDrainer>(sp => sp.GetRequiredService<OutboxProcessor>());

        // Singleton: é um laço de vida longa que cria o próprio escopo a cada ciclo.
        services.AddSingleton<AccountSyncWorker>();

        // Os dois provedores de IA são sempre registrados, como os de OAuth: quem decide
        // se algum pode rodar é o AssistantGateway, consultando disponibilidade e o
        // consentimento do Diretório de Domínio. O local vem primeiro na coleção, o que
        // reforça na ordem o que a política já garante.
        services.AddHttpClient();
        services.AddSingleton<Application.Abstractions.Assistant.IAssistantProvider,
            Assistant.LocalAssistantProvider>();
        services.AddSingleton<Application.Abstractions.Assistant.IAssistantProvider,
            Assistant.CloudAssistantProvider>();

        return services;
    }
}
