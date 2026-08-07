using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre a tela da fila de sincronização. Ela existe porque, no modo offline-first, "já
/// enviei" e "ainda não saiu daqui" são estados diferentes que o usuário precisa distinguir.
/// </summary>
public class OutboxQueueViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private OutboxQueueViewModel CreateViewModel() => new(_outbox, _unitOfWork, _clock);

    private static OutboxOperation Operation(
        OutboxOperationType type = OutboxOperationType.MoveMessage, long sequence = 1)
        => OutboxOperation.Enqueue(AccountId, type, Guid.CreateVersion7(), "{}", sequence, Now);

    [Fact]
    public async Task Carregar_FilaVazia_InformaQueNadaEstaPendente()
    {
        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxOperation>());

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.IsEmpty.Should().BeTrue();
        viewModel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Carregar_ComOperacoes_ListaTodasComDescricaoEmPortugues()
    {
        // O nome do enum não serve à interface: "SetFolderSubscription" não diz nada a quem
        // só quer saber por que a mensagem ainda não saiu.
        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Operation(OutboxOperationType.SendMessage),
                Operation(OutboxOperationType.SetFolderSubscription, 2),
            });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.PendingCount.Should().Be(2);
        viewModel.Operations[0].Description.Should().Be("Enviar mensagem");
        viewModel.Operations[1].Description.Should().Be("Alterar assinatura da pasta");
    }

    [Fact]
    public async Task Carregar_OperacaoMorta_EContadaComoExigindoAtencao()
    {
        var dead = Operation();

        // Esgota as tentativas até o estado definitivo.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            dead.MarkFailed("falha", Now.AddMinutes(1), Now, isPermanent: attempt == 9);
        }

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { dead });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.NeedsAttentionCount.Should().Be(1);
        viewModel.Operations[0].NeedsAttention.Should().BeTrue();
        viewModel.Operations[0].StatusDescription.Should().Contain("definitivamente");
    }

    [Fact]
    public async Task Carregar_OperacaoComFalhaTemporaria_NaoPedeAtencao()
    {
        // Pedir atenção a cada falha de rede transformaria a fila em ruído.
        var failing = Operation();
        failing.MarkFailed("sem rede", Now.AddMinutes(1), Now);

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { failing });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.NeedsAttentionCount.Should().Be(0);
        viewModel.Operations[0].StatusDescription.Should().Contain("tentada de novo");
    }

    /// <summary>
    /// A falha traz o motivo, e não só a contagem de tentativas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OutboxOperation.LastError</c> já era gravado e não chegava à tela. O usuário via
    /// "Falhou 2 vez(es)" sem saber se era senha, rede, pasta que sumiu ou defeito do
    /// programa — nem, portanto, o que fazer.
    /// </para>
    /// <para>
    /// Aqui o motivo vale mais que o normal: a fila é sequencial por conta e para na primeira
    /// falha, então uma operação que falha sempre trava todas as seguintes. O motivo dela é o
    /// único dado que explica por que nada mais sai (D-046).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Carregar_OperacaoQueFalhou_MostraOMotivo()
    {
        var failing = Operation();
        failing.MarkFailed("O servidor recusou: pasta de destino inexistente.", Now.AddMinutes(1), Now);

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { failing });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.Operations[0].StatusDescription
            .Should().Contain("pasta de destino inexistente");
    }

    /// <summary>
    /// A operação que falhou é apontada como a que segura a fila.
    /// </summary>
    /// <remarks>
    /// Sem isso, dezoito linhas aparecem com o mesmo peso e a que importa é uma só — as
    /// demais estão apenas esperando atrás dela.
    /// </remarks>
    [Fact]
    public async Task Carregar_OperacaoQueFalhou_EMarcadaComoBloqueadoraDaFila()
    {
        var failing = Operation(sequence: 1);
        failing.MarkFailed("sem rede", Now.AddMinutes(1), Now);

        var waiting = Operation(sequence: 2);

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { failing, waiting });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        viewModel.Operations[0].IsBlockingQueue.Should().BeTrue();
        viewModel.Operations[1].IsBlockingQueue.Should().BeFalse(
            "quem só espera atrás não é o problema");
    }

    [Fact]
    public async Task Descartar_SemSelecao_NaoFazNada()
    {
        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Operation() });

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();

        await viewModel.CancelSelectedAsync();

        viewModel.PendingCount.Should().Be(1);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Descartar_OperacaoSelecionada_SaiDaFila()
    {
        var operation = Operation();

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { operation });
        _outbox.GetByIdAsync(operation.Id, Arg.Any<CancellationToken>()).Returns(operation);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();
        viewModel.SelectedOperation = viewModel.Operations[0];

        await viewModel.CancelSelectedAsync();

        operation.Status.Should().Be(OutboxOperationStatus.Cancelled);
        viewModel.IsEmpty.Should().BeTrue();
        viewModel.SelectedOperation.Should().BeNull();
    }

    [Fact]
    public async Task Descartar_OperacaoQueJaSaiuDaFila_ExplicaSemFalhar()
    {
        var operation = Operation();

        _outbox.ListPendingAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new[] { operation });
        _outbox.GetByIdAsync(operation.Id, Arg.Any<CancellationToken>()).Returns((OutboxOperation?)null);

        var viewModel = CreateViewModel();
        await viewModel.LoadAsync();
        viewModel.SelectedOperation = viewModel.Operations[0];

        await viewModel.CancelSelectedAsync();

        viewModel.StatusMessage.Should().Contain("já não está mais na fila");
    }
}
