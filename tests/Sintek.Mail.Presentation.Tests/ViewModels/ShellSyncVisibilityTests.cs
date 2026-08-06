using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre a visibilidade de falha de sincronização na barra lateral e na barra de status.
/// </summary>
/// <remarks>
/// <para>
/// A falha já era gravada em <c>Account.SyncStatus</c> e <c>Account.LastSyncError</c> desde a
/// fase 3, e <b>ninguém a lia</b>. Uma conta parada por senha expirada ficava idêntica a uma
/// conta sem mensagem nova; o motivo existia apenas no log de depuração, que o usuário não
/// tem como abrir. Ele descobria dias depois, procurando um e-mail que nunca chegou.
/// </para>
/// <para>
/// O laço de sincronização também não falava com a interface: gravava e seguia, para não
/// morrer. Por isso a leitura acontece na montagem da árvore, e o laço passou a avisar quando
/// termina uma volta.
/// </para>
/// </remarks>
public class ShellSyncVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 21, 0, 0, TimeSpan.Zero);

    private readonly IDomainDirectoryRepository _directories = Substitute.For<IDomainDirectoryRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();
    private readonly ISavedSearchRepository _savedSearches = Substitute.For<ISavedSearchRepository>();
    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();

    private readonly DomainDirectory _directory =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now, "Sintek");

    private ShellViewModel CreateShell() => new(
        new TestScopes()
            .With(_directories)
            .With(_accounts)
            .With(_folders)
            .With(_savedSearches)
            .With(_outbox)
            .Build(),
        new FixedClock(Now));

    /// <summary>Deixa uma conta no diretório, com o estado de sincronização pedido.</summary>
    private Account ArrangeAccount(
        string address, string displayName, Action<Account>? configure = null)
    {
        var account = Account.Create(
            _directory.Id, EmailAddress.Parse(address), displayName, Now);

        configure?.Invoke(account);

        _directories.ListAsync(Arg.Any<CancellationToken>()).Returns([_directory]);
        _accounts.ListByDomainAsync(_directory.Id, Arg.Any<CancellationToken>()).Returns([account]);
        _folders.ListByAccountAsync(account.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Folder>());
        _savedSearches.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SavedSearch>());

        return account;
    }

    private static NavigationNode AccountNodeOf(ShellViewModel shell)
        => shell.NavigationRoots
            .SelectMany(r => r.Children)
            .SelectMany(d => d.Children)
            .Single(n => n.Kind == NavigationNodeKind.Account);

    [Fact]
    public async Task CarregarNavegacao_ContaComFalhaDeAutenticacao_MarcaONoDaConta()
    {
        ArrangeAccount(
            "contato@sintek.com.br",
            "Contato",
            a => a.MarkSyncFailed("A senha foi recusada pelo servidor.", true, Now));

        var shell = CreateShell();
        await shell.LoadNavigationAsync();

        var node = AccountNodeOf(shell);

        node.HasSyncProblem.Should().BeTrue();
        node.SyncError.Should().Be("A senha foi recusada pelo servidor.");
        node.SyncStatusIcon.Should().NotBeEmpty("o alerta precisa de um glifo para aparecer");
    }

    /// <summary>
    /// Com várias contas, o aviso precisa dizer <b>qual</b> delas parou.
    /// </summary>
    /// <remarks>
    /// "Falha de sincronização" sem nome não diz onde mexer, e o usuário teria de abrir conta
    /// por conta para descobrir.
    /// </remarks>
    [Fact]
    public async Task CarregarNavegacao_ContaComFalha_NomeiaAContaNaBarraDeStatus()
    {
        ArrangeAccount(
            "contato@sintek.com.br",
            "Contato Comercial",
            a => a.MarkSyncFailed("A senha foi recusada pelo servidor.", true, Now));

        var shell = CreateShell();
        await shell.LoadNavigationAsync();

        shell.StatusMessage.Should().Contain("Contato Comercial");
        shell.StatusMessage.Should().Contain("A senha foi recusada pelo servidor.");
        shell.Connectivity.Should().Be(ConnectivityState.Error);
    }

    /// <summary>
    /// Conta offline não acende alerta.
    /// </summary>
    /// <remarks>
    /// É o modo offline funcionando como projetado, e não defeito: os dados locais seguem
    /// utilizáveis e a fila espera a rede voltar. Alerta que aparece a cada oscilação deixa de
    /// ser lido, e aí o alerta que importa passa junto com ele.
    /// </remarks>
    [Fact]
    public async Task CarregarNavegacao_ContaApenasOffline_NaoAcendeAlerta()
    {
        ArrangeAccount(
            "contato@sintek.com.br",
            "Contato",
            a => a.SetSyncStatus(AccountSyncStatus.Offline, Now));

        var shell = CreateShell();
        await shell.LoadNavigationAsync();

        AccountNodeOf(shell).HasSyncProblem.Should().BeFalse();
        shell.StatusMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CarregarNavegacao_TodasAsContasEmDia_NaoDeixaAvisoNaBarra()
    {
        ArrangeAccount("contato@sintek.com.br", "Contato", a => a.MarkSynced(Now));

        var shell = CreateShell();
        await shell.LoadNavigationAsync();

        AccountNodeOf(shell).HasSyncProblem.Should().BeFalse();
        shell.StatusMessage.Should().BeNullOrEmpty();
        shell.Connectivity.Should().Be(ConnectivityState.Online);
    }

    /// <summary>
    /// O aviso de sincronização não apaga uma mensagem já posta na barra.
    /// </summary>
    /// <remarks>
    /// Uma recusa da regra de domínio acabou de ser explicada ao usuário; trocá-la por um
    /// aviso de sincronização apagaria a resposta à ação que ele acabou de fazer.
    /// </remarks>
    [Fact]
    public async Task CarregarNavegacao_ComMensagemJaPosta_NaoASobrescreve()
    {
        ArrangeAccount(
            "contato@sintek.com.br",
            "Contato",
            a => a.MarkSyncFailed("A senha foi recusada pelo servidor.", true, Now));

        var shell = CreateShell();
        shell.StatusMessage = "A mensagem não pertence ao domínio da pasta.";

        await shell.LoadNavigationAsync();

        shell.StatusMessage.Should().Be("A mensagem não pertence ao domínio da pasta.");
    }
    /// <summary>
    /// Recarga da árvore não pode impedir o clique em sincronizar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O comando desistia com <c>IsBusy</c> ligado, e <c>LoadNavigationAsync</c> liga o mesmo
    /// sinalizador. Depois que o laço passou a mandar a árvore recarregar a cada volta
    /// (D-038), essas recargas ficaram frequentes — e clicar em sincronizar durante uma delas
    /// não fazia nada e não avisava nada. O usuário concluía, com razão, que o botão estava
    /// quebrado (D-043).
    /// </para>
    /// <para>
    /// São operações diferentes: ler o banco não impede conversar com o servidor.
    /// </para>
    /// </remarks>
    [Fact]
    public void SincronizarAgora_ComRecargaDaArvoreEmCurso_ContinuaHabilitado()
    {
        var shell = CreateShell();

        shell.IsBusy = true;

        shell.SyncNowCommand.CanExecute(null).Should().BeTrue(
            "recarregar a árvore não é motivo para recusar sincronizar");
    }

    /// <summary>Sincronização em curso desabilita o botão, em vez de o deixar inerte.</summary>
    /// <remarks>
    /// Botão desabilitado comunica "já estou fazendo"; botão habilitado que ignora o clique
    /// comunica "este programa está quebrado".
    /// </remarks>
    [Fact]
    public void SincronizarAgora_JaSincronizando_DesabilitaOBotao()
    {
        var shell = CreateShell();

        shell.IsSyncing = true;

        shell.SyncNowCommand.CanExecute(null).Should().BeFalse();
    }

    // ----- Texto da última sincronização ----------------------------------------------

    /// <summary>
    /// O instante da última sincronização vira texto que se lê de relance.
    /// </summary>
    /// <remarks>
    /// "há 3 minutos" responde a pergunta que a pessoa tem — "isto está funcionando?" —,
    /// enquanto um carimbo de hora a obriga a olhar o relógio e subtrair.
    /// </remarks>
    [Theory]
    [InlineData(0, "agora há pouco")]
    [InlineData(10, "10 minuto")]
    [InlineData(180, "3 hora")]
    [InlineData(3000, "mais de um dia")]
    public void DescreverUltimaSincronizacao_PorDistancia_EscolheAUnidadeUtil(
        int minutosAtras, string esperado)
    {
        var texto = ShellViewModel.DescribeLastSync(Now.AddMinutes(-minutosAtras), Now);

        texto.Should().Contain(esperado);
    }

    /// <summary>
    /// Relógio para trás não vira "há -4 minutos".
    /// </summary>
    /// <remarks>
    /// Fuso, NTP e hibernação movem o relógio do sistema para trás de verdade, e a subtração
    /// fica negativa. É o mesmo cuidado que <c>SyncSchedule</c> já toma.
    /// </remarks>
    [Fact]
    public void DescreverUltimaSincronizacao_RelogioParaTras_NaoProduzTempoNegativo()
    {
        var texto = ShellViewModel.DescribeLastSync(Now.AddMinutes(30), Now);

        texto.Should().NotContain("-");
        texto.Should().Contain("agora há pouco");
    }

    [Fact]
    public void DescreverUltimaSincronizacao_NenhumaContaSincronizou_DizIsso()
        => ShellViewModel.DescribeLastSync(null, Now).Should().Contain("Nenhuma conta");

    /// <summary>O ícone do botão muda com o estado, e não só a dica.</summary>
    /// <remarks>
    /// Estado que só existe na dica é estado que ninguém lê: exige parar o mouse em cima, e
    /// uma sincronização de dois segundos ninguém pega.
    /// </remarks>
    [Fact]
    public void IconeDeConectividade_MudaComOEstado()
    {
        var shell = CreateShell();
        var vistos = new List<string>();

        foreach (var estado in Enum.GetValues<ConnectivityState>())
        {
            shell.Connectivity = estado;
            vistos.Add(shell.ConnectivityIcon);
        }

        vistos.Should().OnlyContain(g => !string.IsNullOrEmpty(g), "todo estado precisa de glifo");
        vistos.Distinct().Should().HaveCountGreaterThan(1, "senão o ícone não comunica nada");
    }
}

/// <summary>Relógio fixo, para o texto de "sincronizado há X" ser verificável.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
