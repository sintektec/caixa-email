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
        // Obrigatorio com Microsoft.Data.Sqlite.Core: sem provider registrado,
        // a primeira query lanca "You need to call SQLitePCL.raw.SetProvider()".
        // Registra o bundle_e_sqlcipher -- o unico bundle do projeto desde que
        // EntityFrameworkCore.Sqlite (que arrastava o e_sqlite3 sem
        // criptografia) foi trocado por .Sqlite.Core. Exigido por D-004.
        SQLitePCL.Batteries_V2.Init();

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
