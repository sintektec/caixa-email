using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre a montagem de respostas e encaminhamentos — as regras chatas que separam um cliente
/// de e-mail utilizável de um que constrange quem o usa.
/// </summary>
public class DraftComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);
    private static readonly EmailAddress Conta = EmailAddress.Parse("contato@sintek.com.br");

    private static Message Original(string subject = "Proposta comercial")
    {
        var message = Message.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "<original@externo.com>", Now, Now, Now);

        message.SetHeaders(
            subject, EmailAddress.Parse("cliente@externo.com"), "Cliente Externo", null, null, Now);

        message.AddAddress(MessageAddress.Create(message.Id, AddressKind.To, Conta, Now));

        return message;
    }

    private static void AddRecipient(Message message, AddressKind kind, string address, string? name = null)
        => message.AddAddress(MessageAddress.Create(
            message.Id, kind, EmailAddress.Parse(address), Now, name));

    // ----- Responder ------------------------------------------------------------------

    [Fact]
    public void Responder_MensagemSimples_EnderecaAoRemetente()
    {
        var draft = DraftComposer.Compose(DraftKind.Reply, Original(), null, Conta);

        draft.Recipients.Should().ContainSingle();
        draft.Recipients[0].Address.Value.Should().Be("cliente@externo.com");
        draft.Recipients[0].Kind.Should().Be(AddressKind.To);
    }

    [Fact]
    public void Responder_ComReplyTo_PrefereOCabecalhoDeResposta()
    {
        // É para isso que o Reply-To existe, e listas de discussão dependem dele.
        var original = Original();
        AddRecipient(original, AddressKind.ReplyTo, "lista@discussao.org");

        var draft = DraftComposer.Compose(DraftKind.Reply, original, null, Conta);

        draft.Recipients.Should().ContainSingle();
        draft.Recipients[0].Address.Value.Should().Be("lista@discussao.org");
    }

    [Fact]
    public void Responder_AssuntoSemPrefixo_RecebeRe()
    {
        var draft = DraftComposer.Compose(DraftKind.Reply, Original(), null, Conta);

        draft.Subject.Should().Be("Re: Proposta comercial");
    }

    [Fact]
    public void Responder_AssuntoJaPrefixado_NaoEmpilhaOPrefixo()
    {
        // "Re: Re: Re: Proposta" é o sintoma clássico de cliente que não verifica antes.
        var draft = DraftComposer.Compose(DraftKind.Reply, Original("Re: Proposta comercial"), null, Conta);

        draft.Subject.Should().Be("Re: Proposta comercial");
    }

    [Fact]
    public void Responder_MantemACadeiaDaConversa()
    {
        var draft = DraftComposer.Compose(DraftKind.Reply, Original(), null, Conta);

        draft.InReplyTo.Should().Be("<original@externo.com>");
        draft.References.Should().Contain("<original@externo.com>");
    }

    [Fact]
    public void Responder_ComReferencesAnteriores_AcrescentaAoFinal()
    {
        var original = Original();
        original.SetHeaders(
            "Proposta comercial",
            EmailAddress.Parse("cliente@externo.com"),
            "Cliente Externo",
            "<anterior@externo.com>",
            "<primeira@externo.com> <anterior@externo.com>",
            Now);

        var draft = DraftComposer.Compose(DraftKind.Reply, original, null, Conta);

        draft.References.Should().ContainInOrder(
            "<primeira@externo.com>", "<anterior@externo.com>", "<original@externo.com>");
    }

    // ----- Responder a todos ----------------------------------------------------------

    [Fact]
    public void ResponderATodos_IncluiDestinatariosECopias()
    {
        var original = Original();
        AddRecipient(original, AddressKind.To, "gerente@externo.com");
        AddRecipient(original, AddressKind.Cc, "auditoria@externo.com");

        var draft = DraftComposer.Compose(DraftKind.ReplyAll, original, null, Conta);

        draft.Recipients.Select(r => r.Address.Value).Should().BeEquivalentTo([
            "cliente@externo.com", "gerente@externo.com", "auditoria@externo.com",
        ]);
    }

    [Fact]
    public void ResponderATodos_NaoIncluiAPropriaConta()
    {
        // Responder a todos e receber a própria resposta é ruído que ninguém quer.
        var original = Original();

        var draft = DraftComposer.Compose(DraftKind.ReplyAll, original, null, Conta);

        draft.Recipients.Select(r => r.Address.Value)
            .Should().NotContain("contato@sintek.com.br");
    }

    [Fact]
    public void ResponderATodos_NuncaRevelaQuemEstavaEmCopiaOculta()
    {
        // Quem estava em CCO estava escondido dos demais. Revelá-lo numa resposta é
        // vazamento de informação, não conveniência.
        var original = Original();
        AddRecipient(original, AddressKind.Bcc, "diretoria@sintek.com.br");

        var draft = DraftComposer.Compose(DraftKind.ReplyAll, original, null, Conta);

        draft.Recipients.Select(r => r.Address.Value)
            .Should().NotContain("diretoria@sintek.com.br");
    }

    [Fact]
    public void ResponderATodos_EnderecoRepetidoEmParaECopia_EntraUmaVezSo()
    {
        var original = Original();
        AddRecipient(original, AddressKind.Cc, "cliente@externo.com");

        var draft = DraftComposer.Compose(DraftKind.ReplyAll, original, null, Conta);

        draft.Recipients.Count(r => r.Address.Value == "cliente@externo.com").Should().Be(1);
    }

    // ----- Encaminhar -----------------------------------------------------------------

    [Fact]
    public void Encaminhar_NaoPreencheDestinatarios()
    {
        var draft = DraftComposer.Compose(DraftKind.Forward, Original(), null, Conta);

        draft.Recipients.Should().BeEmpty();
    }

    [Fact]
    public void Encaminhar_UsaOPrefixoDoOutlookEmPortugues()
    {
        var draft = DraftComposer.Compose(DraftKind.Forward, Original(), null, Conta);

        draft.Subject.Should().Be("Enc: Proposta comercial");
    }

    [Fact]
    public void Encaminhar_NaoEncadeiaNaConversaOriginal()
    {
        // Encaminhamento inicia outra conversa: quem recebe não participou da anterior.
        var draft = DraftComposer.Compose(DraftKind.Forward, Original(), null, Conta);

        draft.InReplyTo.Should().BeNull();
        draft.References.Should().BeEmpty();
        draft.ThreadId.Should().BeNull();
    }

    [Fact]
    public void Encaminhar_IncluiOCabecalhoDaMensagemOriginal()
    {
        var draft = DraftComposer.Compose(DraftKind.Forward, Original(), null, Conta);

        draft.TextBody.Should().Contain("Mensagem encaminhada");
        draft.TextBody.Should().Contain("Cliente Externo");
        draft.TextBody.Should().Contain("05/08/2026");
    }

    // ----- Corpo citado ---------------------------------------------------------------

    [Fact]
    public void Citar_UsaOHtmlHigienizadoNuncaOOriginal()
    {
        // O conteúdo que o usuário vai reenviar não pode carregar script que o painel de
        // leitura já tinha removido — seria o produto propagando o que existe para conter.
        var original = Original();
        var body = MessageBody.Create(original.Id, Now);
        body.SetContent(
            htmlBody: "<p>Olá</p><script>roubar()</script>",
            textBody: "Olá",
            sanitizedHtml: "<p>Olá</p>",
            hasRemoteContent: false,
            Now);

        var draft = DraftComposer.Compose(DraftKind.Reply, original, body, Conta);

        draft.HtmlBody.Should().Contain("<p>Olá</p>");
        draft.HtmlBody.Should().NotContain("script");
    }

    [Fact]
    public void Citar_TextoPuro_RecebeOMarcadorDeCitacao()
    {
        var original = Original();
        var body = MessageBody.Create(original.Id, Now);
        body.SetContent(null, "Primeira linha\nSegunda linha", null, hasRemoteContent: false, Now);

        var draft = DraftComposer.Compose(DraftKind.Reply, original, body, Conta);

        draft.TextBody.Should().Contain("> Primeira linha");
        draft.TextBody.Should().Contain("> Segunda linha");
    }

    [Fact]
    public void Compor_MensagemNova_TrazApenasAAssinatura()
    {
        var draft = DraftComposer.Compose(DraftKind.New, null, null, Conta, "Contato — Sintek");

        draft.Subject.Should().BeEmpty();
        draft.Recipients.Should().BeEmpty();
        draft.TextBody.Should().Be("Contato — Sintek");
        draft.HtmlBody.Should().Contain("Contato");
    }

    [Fact]
    public void Compor_ComAssinatura_EscapaOHtmlDoTextoDoUsuario()
    {
        // Assinatura é texto digitado pelo usuário; injetá-la crua no HTML transformaria o
        // campo de assinatura em vetor de injeção contra o próprio compositor.
        var draft = DraftComposer.Compose(DraftKind.New, null, null, Conta, "<b>Contato</b>");

        draft.HtmlBody.Should().Contain("&lt;b&gt;");
        draft.HtmlBody.Should().NotContain("<b>Contato</b>");
    }
}

/// <summary>Cobre o aviso de anexo esquecido.</summary>
public class ForgottenAttachmentDetectorTests
{
    [Theory]
    [InlineData("Segue em anexo a proposta.")]
    [InlineData("Prezado, segue anexo o contrato assinado.")]
    [InlineData("Estou encaminhando o documento em anexo.")]
    [InlineData("O relatório anexado traz os números.")]
    [InlineData("Please find attached the invoice.")]
    [InlineData("Attached is the report you asked for.")]
    [InlineData("I have attached the signed copy.")]
    public void Avisar_TextoPrometeAnexoESemAnexo_Avisa(string body)
        => ForgottenAttachmentDetector.ShouldWarn(body, attachmentCount: 0).Should().BeTrue();

    [Fact]
    public void Avisar_ComAnexoPresente_NaoAvisa()
        => ForgottenAttachmentDetector.ShouldWarn("Segue em anexo a proposta.", 1).Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Bom dia, tudo bem? Conforme conversamos, o prazo é sexta-feira.")]
    [InlineData("Precisamos revisar o anexo do contrato antes da assinatura.")]
    [InlineData("O contrato tem três anexos previstos na cláusula quinta.")]
    public void Avisar_SemPromessaDeAnexo_NaoAvisa(string? body)
        => ForgottenAttachmentDetector.ShouldWarn(body, attachmentCount: 0).Should().BeFalse();

    [Fact]
    public void Avisar_PromessaApenasNoTextoCitado_NaoAvisa()
    {
        // A mensagem original quase sempre menciona o anexo dela, e não é isso que está
        // sendo redigido agora.
        var body = """
            Recebido, obrigado.

            Em 05/08/2026 14:30, Cliente Externo escreveu:
            > Segue em anexo a proposta.
            """;

        ForgottenAttachmentDetector.ShouldWarn(body, attachmentCount: 0).Should().BeFalse();
    }

    [Fact]
    public void Avisar_PromessaAcimaDaCitacao_Avisa()
    {
        var body = """
            Segue em anexo a versão corrigida.

            Em 05/08/2026 14:30, Cliente Externo escreveu:
            > Pode reenviar?
            """;

        ForgottenAttachmentDetector.ShouldWarn(body, attachmentCount: 0).Should().BeTrue();
    }
}
