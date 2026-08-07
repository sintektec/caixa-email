using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Persistence.Repositories;
using Sintek.Mail.Persistence.Search;

namespace Sintek.Mail.Persistence;

/// <summary>Registro da camada de persistência.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra o <see cref="MailDbContext"/> criptografado e os repositórios.
    /// </summary>
    /// <param name="services">Coleção de serviços.</param>
    /// <param name="optionsFactory">
    /// Resolve o caminho do banco e a chave de criptografia. É uma função, e não um valor,
    /// porque a chave vem do Windows Credential Manager e só pode ser obtida depois que o
    /// contêiner já está montado.
    /// </param>
    public static IServiceCollection AddSintekMailPersistence(
        this IServiceCollection services,
        Func<IServiceProvider, DatabaseOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        SqlCipherConnectionFactory.EnsureProviderInitialized();

        services.AddDbContext<MailDbContext>((provider, builder) =>
        {
            var options = optionsFactory(provider);
            builder.UseSqlite(
                SqlCipherConnectionFactory.BuildConnectionString(options),
                sqlite => sqlite.MigrationsAssembly(typeof(MailDbContext).Assembly.FullName));
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDomainDirectoryRepository, DomainDirectoryRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<ISavedSearchRepository, SavedSearchRepository>();
        services.AddScoped<ISearchService, Fts5SearchService>();
        services.AddScoped<IRuleRepository, RuleRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IMessageTemplateRepository, MessageTemplateRepository>();
        services.AddScoped<ISenderReputationRepository, SenderReputationRepository>();
        services.AddScoped<IRecipientHistoryRepository, RecipientHistoryRepository>();
        services.AddScoped<IContactRepository, ContactRepository>();
        services.AddScoped<ICalendarRepository, CalendarRepository>();
        services.AddScoped<IRemoteCalendarRepository, RemoteCalendarRepository>();

        return services;
    }
}
