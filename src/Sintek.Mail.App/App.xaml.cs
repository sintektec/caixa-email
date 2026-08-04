using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Sintek.Mail.App.Services;

namespace Sintek.Mail.App;

/// <summary>Ponto de entrada da aplicação.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Uma exceção não tratada em um manipulador de interface derruba o processo em
        // silêncio no WinUI. Capturá-la aqui é o que permite registrar a causa em log e
        // exibir algo compreensível ao usuário em vez de a janela simplesmente sumir.
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Contêiner de serviços da aplicação.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = await AppHost.BuildAsync().ConfigureAwait(true);

        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppHost.LogFatal(e.Exception);

        // Não marcamos como tratada: engolir a falha deixaria a aplicação em estado
        // inconsistente, com a interface respondendo sobre dados que já não são válidos.
    }
}
