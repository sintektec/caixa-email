using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Domain.Tests.Entities;

public class MessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static Message NewMessage()
        => Message.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "<id@sintek.com.br>", Now, Now, Now);

    [Theory]
    [InlineData("Re: Proposta comercial", "proposta comercial")]
    [InlineData("RES: Proposta comercial", "proposta comercial")]
    [InlineData("ENC: Proposta comercial", "proposta comercial")]
    [InlineData("Fwd: Proposta comercial", "proposta comercial")]
    [InlineData("Re: Enc: Re: Proposta comercial", "proposta comercial")]
    [InlineData("Re[2]: Proposta comercial", "proposta comercial")]
    [InlineData("Proposta comercial", "proposta comercial")]
    [InlineData("", "")]
    public void NormalizeSubject_RemovePrefixosDeRespostaEEncaminhamento(string subject, string expected)
    {
        // Agrupar conversas só por References falha sempre que alguém responde de um
        // cliente que os descarta; o assunto normalizado é a rede de segurança.
        Message.NormalizeSubject(subject).Should().Be(expected);
    }

    [Fact]
    public void NormalizeSubject_NaoRemovePalavraQueApenasComecaComPrefixo()
    {
        // "Reunião" começa com "Re" mas não é um prefixo de resposta — sem a exigência
        // dos dois-pontos, o assunto seria mutilado.
        Message.NormalizeSubject("Reunião de diretoria").Should().Be("reunião de diretoria");
    }

    [Fact]
    public void SetRead_GravaLocalmenteEMarcaPendenteDeSincronizacao()
    {
        var message = NewMessage();

        message.SetRead(true, Now);

        message.IsRead.Should().BeTrue();
        message.SyncState.Should().Be(MessageSyncState.PendingUpdate);
    }

    [Fact]
    public void SetRead_NaoMarcaPendente_QuandoOValorNaoMuda()
    {
        var message = NewMessage();
        message.SetRead(true, Now);
        message.MarkSynced(Now);

        message.SetRead(true, Now);

        message.SyncState.Should().Be(MessageSyncState.Synced);
    }

    [Fact]
    public void MarkDeleted_NaoEhRebaixadoPorAlteracaoDeMarcador()
    {
        // Se a alteração de marcador rebaixasse o estado, a fila propagaria o marcador e
        // esqueceria a exclusão — a mensagem reapareceria na próxima sincronização.
        var message = NewMessage();
        message.MarkDeleted(Now);

        message.SetRead(true, Now);

        message.SyncState.Should().Be(MessageSyncState.PendingDelete);
    }

    [Fact]
    public void MoveTo_NaoEhRebaixadoPorAlteracaoDeMarcador()
    {
        var message = NewMessage();
        message.MoveTo(Guid.CreateVersion7(), Now);

        message.SetFlagged(true, Now);

        message.SyncState.Should().Be(MessageSyncState.PendingMove);
    }

    [Fact]
    public void RascunhoLocal_NaoEhRebaixadoPorAlteracaoDeMarcador()
    {
        var message = NewMessage();
        message.MarkAsDraft(Now);

        message.SetRead(true, Now);

        message.SyncState.Should().Be(MessageSyncState.LocalOnly);
    }

    [Fact]
    public void Conflito_NuncaEhRebaixado()
    {
        var message = NewMessage();
        message.MarkConflicted(Now);

        message.SetRead(true, Now);
        message.MoveTo(Guid.CreateVersion7(), Now);

        message.SyncState.Should().Be(MessageSyncState.Conflict);
    }

    [Fact]
    public void Restore_TiraDaLixeiraEAgendaAMovimentacao()
    {
        var message = NewMessage();
        message.MarkDeleted(Now);
        var inbox = Guid.CreateVersion7();

        message.Restore(inbox, Now);

        message.IsDeleted.Should().BeFalse();
        message.FolderId.Should().Be(inbox);
        message.SyncState.Should().Be(MessageSyncState.PendingMove);
    }

    [Fact]
    public void ScheduleSend_Recusa_InstanteNoPassado()
    {
        var message = NewMessage();

        var act = () => message.ScheduleSend(Now.AddMinutes(-1), Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddAttachment_MarcaHasAttachments_ApenasParaAnexosNaoEmbutidos()
    {
        var message = NewMessage();

        message.AddAttachment(Attachment.Create(
            message.Id, "logo.png", "image/png", 100, "2", Now, contentId: "logo", isInline: true));
        message.HasAttachments.Should().BeFalse();

        message.AddAttachment(Attachment.Create(
            message.Id, "contrato.pdf", "application/pdf", 5000, "3", Now));
        message.HasAttachments.Should().BeTrue();
    }
}
