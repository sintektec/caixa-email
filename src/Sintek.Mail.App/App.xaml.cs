using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Sintek.Mail.App.Services;
using Sintek.Mail.Infrastructure.Sync;

namespace Sintek.Mail.App;

/// <summary>Ponto de entrada da aplicação.</summary>
/// <remarks>
/// A classe base é qualificada por inteiro de propósito. Dentro do namespace
/// <c>Sintek.Mail.App</c>, o nome curto <c>Application</c> resolve para o nosso próprio
/// namespace <c>Sintek.Mail.Application</c> — a camada de casos de uso — e não para o
/// tipo do WinUI.
/// </remarks>
public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly CancellationTokenSource _syncCancellation = new();

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

        // O laço de sincronização começa depois de a janela aparecer. Iniciá-lo antes
        // atrasaria a primeira tela pelo tempo de uma conexão IMAP — e é justamente na
        // abertura que o usuário mais percebe demora.
        StartSyncLoop();
    }

    /// <summary>
    /// Põe o laço de sincronização em segundo plano.
    /// </summary>
    /// <remarks>
    /// A tarefa não é aguardada de propósito: ela roda enquanto o aplicativo estiver aberto.
    /// A exceção é capturada aqui porque uma falha em tarefa não observada derruba o
    /// processo em silêncio, sem nada em log e sem nada na tela.
    /// </remarks>
    private void StartSyncLoop()
    {
        var worker = Services.GetRequiredService<AccountSyncWorker>();

        _ = Task.Run(async () =>
        {
            try
            {
                await worker.RunAsync(_syncCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do aplicativo.
            }
            catch (Exception ex)
            {
                AppHost.LogFatal(ex);
            }
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppHost.LogFatal(e.Exception);

        // Não marcamos como tratada: engolir a falha deixaria a aplicação em estado
        // inconsistente, com a interface respondendo sobre dados que já não são válidos.
    }
}
