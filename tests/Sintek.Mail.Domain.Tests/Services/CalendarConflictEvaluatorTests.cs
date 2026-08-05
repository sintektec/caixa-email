using AwesomeAssertions;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre a decisão que define se alguém perde trabalho quando local e servidor divergem.
/// Alterar um destes casos precisa ser intencional.
/// </summary>
public class CalendarConflictEvaluatorTests
{
    private static CalendarSyncFacts Facts(
        CalendarSyncState local,
        string? knownETag,
        string? remoteETag,
        RemoteChangeKind change = RemoteChangeKind.Upserted,
        bool existsLocally = true)
        => new(local, knownETag, remoteETag, change, existsLocally);

    [Fact]
    public void Evaluate_RecursoNovoNoServidor_TrazParaCa()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, null, "\"1\"", existsLocally: false));

        decisao.Should().Be(CalendarSyncDecision.ApplyRemote);
    }

    [Fact]
    public void Evaluate_RemocaoDeRecursoQueNaoExisteAqui_NaoFazNada()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, null, null, RemoteChangeKind.Removed, existsLocally: false));

        decisao.Should().Be(CalendarSyncDecision.NoChange);
    }

    [Fact]
    public void Evaluate_EtagIgualESemAlteracaoLocal_NaoFazNada()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, "\"2134-314\"", "\"2134-314\""));

        decisao.Should().Be(CalendarSyncDecision.NoChange);
    }

    [Fact]
    public void Evaluate_EtagDiferenteESemAlteracaoLocal_AplicaOServidor()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, "\"2134-314\"", "\"2134-315\""));

        decisao.Should().Be(CalendarSyncDecision.ApplyRemote);
    }

    /// <summary>
    /// O ETag é comparado com as aspas. Normalizar aqui esconderia a diferença que o
    /// <c>If-Match</c> vai enxergar depois.
    /// </summary>
    [Fact]
    public void Evaluate_EtagSemAspasContraComAspas_ContaComoAlteracao()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, "2134-314", "\"2134-314\""));

        decisao.Should().Be(CalendarSyncDecision.ApplyRemote);
    }

    [Fact]
    public void Evaluate_AlteracaoLocalEServidorParado_EnviaOLocal()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.PendingUpdate, "\"2134-314\"", "\"2134-314\""));

        decisao.Should().Be(CalendarSyncDecision.PushLocal);
    }

    [Fact]
    public void Evaluate_ExclusaoLocalEServidorParado_EnviaAExclusao()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.PendingDelete, "\"2134-314\"", "\"2134-314\""));

        decisao.Should().Be(CalendarSyncDecision.PushDelete);
    }

    [Fact]
    public void Evaluate_OsDoisLadosMudaram_MarcaConflito()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.PendingUpdate, "\"2134-314\"", "\"2134-315\""));

        decisao.Should().Be(CalendarSyncDecision.Conflict);
    }

    [Fact]
    public void Evaluate_ExcluidoNosDoisLados_ApagaAquiSemConflito()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.PendingDelete, "\"2134-314\"", null, RemoteChangeKind.Removed));

        decisao.Should().Be(CalendarSyncDecision.DeleteLocal);
    }

    [Fact]
    public void Evaluate_ExcluidoNoServidorEAlteradoAqui_MarcaConflito()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.PendingUpdate, "\"2134-314\"", null, RemoteChangeKind.Removed));

        decisao.Should().Be(CalendarSyncDecision.Conflict);
    }

    [Fact]
    public void Evaluate_ExcluidoNoServidorESemAlteracaoLocal_ApagaAqui()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Synced, "\"2134-314\"", null, RemoteChangeKind.Removed));

        decisao.Should().Be(CalendarSyncDecision.DeleteLocal);
    }

    /// <summary>
    /// Conflito já declarado continua esperando decisão. Reavaliar a cada passada faria a
    /// marca sumir sozinha assim que o servidor mudasse de novo.
    /// </summary>
    [Fact]
    public void Evaluate_ConflitoJaDeclarado_ContinuaEmConflito()
    {
        var decisao = CalendarConflictEvaluator.Evaluate(
            Facts(CalendarSyncState.Conflict, "\"2134-314\"", "\"2134-314\""));

        decisao.Should().Be(CalendarSyncDecision.Conflict);
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    [InlineData(3, 2, false)]
    public void AllowsSequence_VersaoMenorNuncaSobrescreveMaior(int local, int remoto, bool esperado)
        => CalendarConflictEvaluator.AllowsSequence(local, remoto).Should().Be(esperado);
}
