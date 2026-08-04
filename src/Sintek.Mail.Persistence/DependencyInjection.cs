using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Persistence.Interceptors;
using Sintek.Mail.Persistence.Repositories;

namespace Sintek.Mail.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, string databasePath, string encryptionKey)
    {
        services.AddDbContext<MailDbContext>(options =>
        {
            options.UseSqlite($"Data Source={databasePath}");
            options.AddInterceptors(new SqlCipherInterceptor(encryptionKey));
        });

        services.AddScoped<IMailRepository, MailRepository>();
        services.AddScoped<ISyncQueue, SyncQueue>();

        return services;
    }
}
