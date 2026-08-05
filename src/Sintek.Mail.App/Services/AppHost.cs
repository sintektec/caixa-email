using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Presentation.ViewModels;
using Sintek.Mail.Application;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Infrastructure;
using Sintek.Mail.Infrastructure.Sync;
using Sintek.Mail.Infrastructure.Windows;
using Sintek.Mail.Persistence;
using Sintek.Mail.Presentation;

namespace Sintek.Mail.App.Services;

/// <summary>Monta o contêiner de serviços e prepara o banco local.</summary>
public static class AppHost
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sintek.Mail");

    /// <summary>
    /// Monta o contêiner, obtém a chave do banco e aplica as migrações pendentes.
    /// </summary>
    /// <remarks>
    /// A chave precisa ser lida do cofre do Windows <b>antes</b> de o contexto ser
    /// construído — daí a montagem em duas etapas: um contêiner mínimo só para o cofre,
    /// e depois o contêiner completo já com a chave em mãos.
    /// </remarks>
    public static async Task<IServiceProvider> BuildAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "Attachments"));

        var configuration = BuildConfiguration();
        var encryptionKey = await ResolveDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);
        var databasePath = Path.Combine(DataDirectory, "sintek-mail.db");

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddDebug();
        });

        services.AddSintekMailWindows();
        services.AddSintekMailApplication();
        services.AddSintekMailInfrastructure(configuration);
        services.AddSintekMailPersistence(_ => new DatabaseOptions(databasePath, encryptionKey));

        services.AddSintekMailPresentation();
        services.AddSingleton<MainWindow>();

        var provider = services.BuildServiceProvider();

        await MigrateDatabaseAsync(provider, cancellationToken).ConfigureAwait(false);

        return provider;
    }

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            // O arquivo Local fica fora do controle de versão e permite a cada instalação
            // informar seus próprios Client IDs sem alterar o arquivo versionado.
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("SINTEK_MAIL_")
            .Build();

    /// <summary>
    /// Obtém a chave do banco usando um contêiner mínimo, apenas com o cofre do Windows.
    /// </summary>
    private static async Task<string> ResolveDatabaseKeyAsync(CancellationToken cancellationToken)
    {
        var bootstrap = new ServiceCollection();
        bootstrap.AddLogging(builder => builder.AddDebug());
        bootstrap.AddSintekMailWindows();

        await using var provider = bootstrap.BuildServiceProvider();

        return await provider
            .GetRequiredService<IDatabaseKeyProvider>()
            .GetOrCreateKeyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task MigrateDatabaseAsync(
        IServiceProvider provider, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registra uma falha fatal em arquivo.
    /// </summary>
    /// <remarks>
    /// Grava só o tipo, a mensagem e a pilha da exceção. Conteúdo de mensagem e
    /// credenciais nunca entram em log, como exige a especificação — por isso o registro
    /// não inclui os dados que estavam sendo manipulados.
    /// </remarks>
    public static void LogFatal(Exception exception)
    {
        try
        {
            var path = Path.Combine(DataDirectory, "crash.log");
            var entry =
                $"[{DateTimeOffset.UtcNow:O}] {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}" +
                $"{exception.StackTrace}{Environment.NewLine}{Environment.NewLine}";

            File.AppendAllText(path, entry);
        }
        catch (IOException)
        {
            // Falhar ao registrar a falha não pode gerar uma segunda falha.
        }
    }
}
