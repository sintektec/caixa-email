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

    /// <summary>
    /// Contêiner da aplicação. <b>Privado de propósito.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Era público, e por isso qualquer tela resolvia serviços direto dele. O provedor raiz
    /// <b>é um escopo</b> — o mais longo que existe —, então um repositório resolvido ali vive
    /// enquanto a aplicação viver, e é o mesmo para todo mundo. Junto dele vinha um
    /// <c>DbContext</c> único, compartilhado entre a interface e tudo o mais, com valores
    /// envelhecendo enquanto o laço de sincronização escrevia nas mesmas linhas.
    /// </para>
    /// <para>
    /// Fechar a propriedade é o que impede a reincidência: sem ela não há como resolver do
    /// raiz, e o <b>compilador</b> recusa — em vez de um analisador avisar, ou de ninguém
    /// perceber. Quem precisa de serviço abre um escopo por <see cref="CreateScope"/> e o
    /// descarta ao terminar.
    /// </para>
    /// </remarks>
    private static IServiceProvider _root = null!;

    /// <summary>Abre um escopo para uma operação, a ser descartado ao final dela.</summary>
    public static AsyncServiceScope CreateScope() => _root.CreateAsyncScope();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _root = await AppHost.BuildAsync().ConfigureAwait(true);

        // A janela é singleton e não recebe serviço scoped — ela distribui a fábrica de
        // escopos aos ViewModels residentes, que abrem o seu a cada operação.
        _window = _root.GetRequiredService<MainWindow>();
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
        var worker = _root.GetRequiredService<AccountSyncWorker>();

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
