using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Sintek.Mail.App.ViewModels;
using Sintek.Mail.Application.Handlers;
using Sintek.Mail.Application.Ports;
using Sintek.Mail.Infrastructure;
using Sintek.Mail.Infrastructure.Windows;
using Sintek.Mail.Persistence;

namespace Sintek.Mail.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly IServiceProvider _services;

    public App()
    {
        this.InitializeComponent();
        _services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Persistence
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintek.Mail", "mail.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var encryptionKey = GetOrCreateEncryptionKey();
        services.AddPersistence(dbPath, encryptionKey);

        // Infrastructure
        services.AddInfrastructure();
        services.AddWindowsInfrastructure();

        // Handlers
        services.AddTransient<CreateDomainDirectoryHandler>();
        services.AddTransient<AddAccountHandler>();
        services.AddTransient<MoveMessageHandler>();
        services.AddTransient<ChangeDomainNameHandler>();
        services.AddTransient<SendMessageHandler>();
        services.AddTransient<SyncAccountHandler>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DomainListViewModel>();
        services.AddTransient<AccountListViewModel>();
        services.AddTransient<MessageListViewModel>();
        services.AddTransient<ComposeViewModel>();

        return services.BuildServiceProvider();
    }

    private static string GetOrCreateEncryptionKey()
    {
        // TODO: Store in Credential Manager or DPAPI
        // For now, generate a random key per install
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }

    public T GetService<T>() where T : notnull => _services.GetRequiredService<T>();
}
