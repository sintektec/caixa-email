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

    // ---- Precedência sem SEQUENCE (D-029) ---------------------------------------------

    /// <summary>
    /// Um <c>SEQUENCE</c> é contador de revisão; um instante de alteração é outra coisa. Um
    /// servidor que reescreve o objeto ao gravar move o segundo sem tocar no primeiro, e
    /// compará-los entre si produziria recusa arbitrária.
    /// </summary>
    [Fact]
    public void AllowsVersion_CriteriosDeTiposDiferentes_Aplica()
    {
        var local = RemoteVersion.FromSequence(5);
        var recebida = RemoteVersion.FromTimestamp(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(local, recebida).Should().BeTrue();
    }

    [Fact]
    public void AllowsVersion_InstanteMaisNovo_Aplica()
    {
        var local = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        var recebida = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 13, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(local, recebida).Should().BeTrue();
    }

    [Fact]
    public void AllowsVersion_InstanteMaisAntigo_ERecusada()
    {
        var local = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 13, 0, 0, TimeSpan.Zero));
        var recebida = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(local, recebida).Should().BeFalse();
    }

    /// <summary>
    /// A granularidade do <c>lastModifiedDateTime</c> faz duas alterações próximas caírem no
    /// mesmo instante; recusar a igual perderia a segunda.
    /// </summary>
    [Fact]
    public void AllowsVersion_MesmoInstante_Aplica()
    {
        var stamp = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(stamp, stamp).Should().BeTrue();
    }

    /// <summary>
    /// Chegar até aqui já significa que o ETag mudou. Recusar por falta de versão comparável
    /// deixaria a cópia local parada para sempre num servidor que não declara versão.
    /// </summary>
    [Fact]
    public void AllowsVersion_ServidorSemVersaoDeclarada_Aplica()
    {
        var local = RemoteVersion.FromTimestamp(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(local, RemoteVersion.Unknown).Should().BeTrue();
    }

    /// <summary>
    /// Compromisso que veio de iCalendar tem SEQUENCE e não tem instante; o do Graph tem o
    /// contrário. Quando os dois lados têm SEQUENCE, é ele quem decide — mesmo havendo
    /// instante junto.
    /// </summary>
    [Fact]
    public void AllowsVersion_ComOsDoisCriterios_OSequencePrevalece()
    {
        var local = new RemoteVersion(5, new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        var recebida = new RemoteVersion(3, new DateTimeOffset(2026, 8, 5, 23, 0, 0, TimeSpan.Zero));

        CalendarConflictEvaluator.AllowsVersion(local, recebida).Should().BeFalse();
    }
}
