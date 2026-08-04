using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Ports;

namespace Sintek.Mail.Infrastructure.Windows;

public static class DependencyInjection
{
    public static IServiceCollection AddWindowsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialStore, CredentialManagerStore>();
        return services;
    }
}
