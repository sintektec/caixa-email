using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Application.UseCases.Accounts;
using Sintek.Mail.Application.UseCases.Domains;
using Sintek.Mail.Application.UseCases.Folders;
using Sintek.Mail.Application.UseCases.Messages;

namespace Sintek.Mail.Application;

/// <summary>Registro dos serviços da camada de Aplicação.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra os casos de uso e serviços de aplicação.
    /// </summary>
    /// <remarks>
    /// As portas (repositórios, transporte de e-mail, cofre de credenciais) NÃO são
    /// registradas aqui: cada camada de infraestrutura registra as suas implementações.
    /// É isso que permite ao aplicativo WinUI usar o Credential Manager enquanto os
    /// testes usam um cofre em memória, sem que a Aplicação saiba a diferença.
    /// </remarks>
    public static IServiceCollection AddSintekMailApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TimeProvider.System é o relógio real; testes substituem por um relógio falso.
        services.TryAddSingletonTimeProvider();

        services.AddScoped<OutboxEnqueuer>();

        services.AddScoped<MoveMessageHandler>();
        services.AddScoped<AddAccountHandler>();
        services.AddScoped<ChangeDomainNameHandler>();
        services.AddScoped<SetFolderRestrictionHandler>();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
