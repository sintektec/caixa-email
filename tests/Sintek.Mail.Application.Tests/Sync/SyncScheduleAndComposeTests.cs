using Sintek.Mail.Application.Sync;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.Sync;

/// <summary>
/// Cobre a política de agendamento e a montagem da mensagem de saída — as duas peças puras
/// da sincronização, verificáveis sem relógio real nem servidor.
/// </summary>
public class SyncScheduleAndComposeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private static Account NewAccount()
        => Account.Create(Guid.CreateVersion7(), EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

    // ----- Agendamento ---------------------------------------------------------------

    [Fact]
    public void Agendar_ContaNuncaSincronizada_SincronizaAgora()
    {
        var decision = SyncSchedule.Decide(NewAccount(), Now);

        decision.Action.Should().Be(SyncAction.SyncNow);
    }

    [Fact]
    public void Agendar_ContaDesativada_NaoSincroniza()
    {
        var account = NewAccount();
        account.SetActive(false, Now);

        SyncSchedule.Decide(account, Now).Action.Should().Be(SyncAction.Skip);
    }

    [Fact]
    public void Agendar_CredencialRecusada_SaiDoCicloAteReautenticar()
    {
        // Insistir com senha recusada é a forma mais rápida de ganhar bloqueio no provedor.
        var account = NewAccount();
        account.MarkSyncFailed("Senha recusada.", isAuthenticationFailure: true, Now);

        var decision = SyncSchedule.Decide(account, Now.AddHours(1));

        decision.Action.Should().Be(SyncAction.Skip);
        decision.Reason.Should().Contain("autenticação");
    }

    /// <summary>
    /// A volta ao ciclo precisa existir de verdade, não só na justificativa da saída.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O agendador pula a conta com credencial recusada <b>indefinidamente</b>, e o motivo é
    /// bom: insistir a cada minuto rende bloqueio temporário no provedor. A saída prevista
    /// era "a conta volta ao ciclo quando o usuário reautenticar" — e nada executava essa
    /// volta. Corrigir a senha deixava a conta exatamente tão parada quanto antes, agora com
    /// a credencial certa, e sem nada na tela dizendo por quê.
    /// </para>
    /// <para>
    /// Este teste e <c>Agendar_CredencialRecusada_SaiDoCicloAteReautenticar</c> são as duas
    /// metades da mesma regra. Só a primeira deixava o defeito passar por comportamento
    /// correto (D-040).
    /// </para>
    /// </remarks>
    [Fact]
    public void Agendar_DepoisDeReconfigurada_VoltaAoCiclo()
    {
        var account = NewAccount();
        account.MarkSyncFailed("Senha recusada.", isAuthenticationFailure: true, Now);

        account.ResumeSync(Now.AddMinutes(5));

        SyncSchedule.Decide(account, Now.AddMinutes(5)).Action.Should().Be(SyncAction.SyncNow);
        account.LastSyncError.Should().BeNull("o motivo antigo não vale para a configuração nova");
    }

    /// <summary>
    /// Voltar como <c>NeverSynced</c>, e não como <c>Online</c>.
    /// </summary>
    /// <remarks>
    /// Quem reconfigurou não provou que o servidor aceita — apenas pediu nova tentativa.
    /// Declarar a conta em dia mentiria na barra de status até a primeira sincronização, e é
    /// o resultado dela que define o estado.
    /// </remarks>
    [Fact]
    public void Retomar_ContaComFalha_NaoADeclaraEmDia()
    {
        var account = NewAccount();
        account.MarkSyncFailed("Senha recusada.", isAuthenticationFailure: true, Now);

        account.ResumeSync(Now);

        account.SyncStatus.Should().Be(AccountSyncStatus.NeverSynced);
        account.SyncStatus.Should().NotBe(AccountSyncStatus.Online);
    }

    [Fact]
    public void Agendar_IntervaloAindaNaoVenceu_Espera()
    {
        var account = NewAccount();
        account.ConfigureSync(syncIntervalMinutes: 10, BodyDownloadPolicy.RecentOnly, Now);
        account.MarkSynced(Now);

        var decision = SyncSchedule.Decide(account, Now.AddMinutes(4));

        decision.Action.Should().Be(SyncAction.Wait);
        decision.Delay.Should().Be(TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void Agendar_IntervaloVencido_SincronizaAgora()
    {
        var account = NewAccount();
        account.ConfigureSync(syncIntervalMinutes: 5, BodyDownloadPolicy.RecentOnly, Now);
        account.MarkSynced(Now);

        SyncSchedule.Decide(account, Now.AddMinutes(5)).Action.Should().Be(SyncAction.SyncNow);
    }

    [Fact]
    public void Agendar_ContaOffline_TentaDeNovoEmUmMinuto()
    {
        // Sem conexão, o intervalo configurado não interessa: o que importa é perceber a
        // rede voltando.
        var account = NewAccount();
        account.ConfigureSync(syncIntervalMinutes: 60, BodyDownloadPolicy.RecentOnly, Now);
        account.MarkSynced(Now);
        account.SetSyncStatus(AccountSyncStatus.Offline, Now);

        var decision = SyncSchedule.Decide(account, Now.AddSeconds(30));

        decision.Action.Should().Be(SyncAction.Wait);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Agendar_RelogioAndouParaTras_TrataComoVencido()
    {
        // Fuso, NTP ou hibernação podem recuar o relógio; esperar o tempo "andar de novo"
        // deixaria a conta parada indefinidamente.
        var account = NewAccount();
        account.MarkSynced(Now);

        SyncSchedule.Decide(account, Now.AddMinutes(-30)).Action.Should().Be(SyncAction.SyncNow);
    }

    [Fact]
    public void Agendar_AposErro_EsperaMaisQueOIntervaloConfigurado()
    {
        var account = NewAccount();
        account.ConfigureSync(syncIntervalMinutes: 5, BodyDownloadPolicy.RecentOnly, Now);
        account.MarkSynced(Now);
        account.MarkSyncFailed("falha", isAuthenticationFailure: false, Now);

        var decision = SyncSchedule.Decide(account, Now.AddMinutes(6));

        decision.Action.Should().Be(SyncAction.Wait);
        decision.Delay.Should().Be(TimeSpan.FromMinutes(4));
    }

    [Theory]
    [InlineData(AccountSyncStatus.Online, true, true)]
    [InlineData(AccountSyncStatus.Online, false, false)]
    [InlineData(AccountSyncStatus.Error, true, false)]
    [InlineData(AccountSyncStatus.Offline, true, false)]
    public void EsperaPassiva_SoValeParaContaEmDiaComServidorQueSuporta(
        AccountSyncStatus status, bool serverSupports, bool expected)
    {
        var account = NewAccount();
        account.SetSyncStatus(status, Now);

        SyncSchedule.ShouldIdle(account, serverSupports).Should().Be(expected);
    }

    // ----- Montagem da mensagem de saída ---------------------------------------------

    private static Message Draft(Guid folderId)
    {
        var message = Message.Create(AccountId, folderId, "<rascunho@local>", Now, Now, Now);
        message.SetHeaders("Proposta", EmailAddress.Parse("contato@sintek.com.br"), "Contato", null, null, Now);
        return message;
    }

    [Fact]
    public void Montar_SemDestinatario_Recusa()
    {
        var message = Draft(Guid.CreateVersion7());

        OutgoingMessageBuilder.Build(message, null).Should().BeNull();
    }

    [Fact]
    public void Montar_SemRemetente_Recusa()
    {
        var message = Message.Create(AccountId, Guid.CreateVersion7(), "<rascunho@local>", Now, Now, Now);
        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("cliente@externo.com"), Now));

        OutgoingMessageBuilder.Build(message, null).Should().BeNull();
    }

    [Fact]
    public void Montar_ComDestinatarios_SeparaParaCopiaECopiaOculta()
    {
        var message = Draft(Guid.CreateVersion7());

        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("cliente@externo.com"), Now));
        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.Cc, EmailAddress.Parse("gerente@sintek.com.br"), Now));
        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.Bcc, EmailAddress.Parse("arquivo@sintek.com.br"), Now));

        var outgoing = OutgoingMessageBuilder.Build(message, null);

        outgoing.Should().NotBeNull();
        outgoing!.To.Should().BeEquivalentTo(["cliente@externo.com"]);
        outgoing.Cc.Should().BeEquivalentTo(["gerente@sintek.com.br"]);
        outgoing.Bcc.Should().BeEquivalentTo(["arquivo@sintek.com.br"]);
    }

    [Fact]
    public void Montar_AnexoAindaNaoBaixado_Recusa()
    {
        // Enviar sem o anexo é pior do que não enviar: a mensagem chega aparentemente
        // completa e ninguém percebe a falta.
        var message = Draft(Guid.CreateVersion7());
        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("cliente@externo.com"), Now));

        message.AddAttachment(Attachment.Create(
            message.Id, "contrato.pdf", "application/pdf", 1024, "2", Now));

        OutgoingMessageBuilder.Build(message, null).Should().BeNull();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("<a@x>", 1)]
    [InlineData("<a@x> <b@x>", 2)]
    [InlineData("<a@x>\r\n <b@x>\t<c@x>", 3)]
    public void QuebrarReferences_FormatosUsadosNaPratica_SaoTodosAceitos(string? raw, int expected)
        => OutgoingMessageBuilder.SplitReferences(raw).Should().HaveCount(expected);
}
