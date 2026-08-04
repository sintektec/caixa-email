using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Infrastructure.OAuth;
using Sintek.Mail.Infrastructure.Services;
using Sintek.Mail.Infrastructure.Transport;

namespace Sintek.Mail.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? msalClientId = null, string? googleClientId = null, string? googleClientSecret = null)
    {
        services.AddSingleton<IMailTransport, MailKitTransport>();
        services.AddSingleton<IHtmlSanitizer, HtmlSanitizerService>();

        if (!string.IsNullOrEmpty(msalClientId))
        {
            services.AddSingleton<IOAuthProvider>(new MsalOAuthProvider(msalClientId));
        }

        if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
        {
            services.AddSingleton<IOAuthProvider>(new GoogleOAuthProvider(googleClientId, googleClientSecret));
        }

        return services;
    }
}
