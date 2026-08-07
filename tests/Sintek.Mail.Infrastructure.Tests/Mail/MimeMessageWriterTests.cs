using MimeKit;
using Sintek.Mail.Application.Abstractions.Mail;

// A prioridade existe nos dois mundos com o mesmo nome; o apelido diz de qual se fala.
using DomainImportance = Sintek.Mail.Domain.Enums.MessageImportance;
using Sintek.Mail.Infrastructure.Mail;

namespace Sintek.Mail.Infrastructure.Tests.Mail;

/// <summary>
/// Cobre a serialização MIME. O mesmo documento vai para o SMTP e para o <c>APPEND</c> em
/// Itens Enviados, então um defeito aqui aparece nos dois lugares — ou, pior, só em um.
/// </summary>
public class MimeMessageWriterTests
{
    private static OutgoingMessage Message() => new()
    {
        From = "contato@sintek.com.br",
        FromDisplayName = "Contato Sintek",
        To = ["cliente@externo.com"],
        Cc = ["gerente@sintek.com.br"],
        Subject = "Proposta comercial",
        TextBody = "Segue a proposta.",
        HtmlBody = "<p>Segue a proposta.</p>",
    };

    [Fact]
    public void Compor_MensagemBasica_PreencheRemetenteDestinatariosEAssunto()
    {
        var mime = MimeMessageWriter.Compose(Message());

        mime.From.Mailboxes.Single().Address.Should().Be("contato@sintek.com.br");
        mime.From.Mailboxes.Single().Name.Should().Be("Contato Sintek");
        mime.To.Mailboxes.Single().Address.Should().Be("cliente@externo.com");
        mime.Cc.Mailboxes.Single().Address.Should().Be("gerente@sintek.com.br");
        mime.Subject.Should().Be("Proposta comercial");
    }

    [Fact]
    public void Compor_ComCorpoNosDoisFormatos_GeraAlternativa()
    {
        // Cliente que não renderiza HTML precisa do texto puro; enviar só HTML deixaria a
        // mensagem ilegível para ele.
        var mime = MimeMessageWriter.Compose(Message());

        mime.HtmlBody.Should().Contain("Segue a proposta");
        mime.TextBody.Should().Contain("Segue a proposta");
    }

    [Fact]
    public void Compor_SempreAtribuiMessageId()
    {
        // Sem Message-ID a mensagem não entra em nenhuma conversa e vários servidores a
        // tratam como suspeita.
        MimeMessageWriter.Compose(Message()).MessageId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Compor_ComReferences_PreservaACadeiaDaConversa()
    {
        var message = Message() with
        {
            InReplyTo = "<anterior@externo.com>",
            References = ["<primeira@externo.com>", "<anterior@externo.com>"],
        };

        var mime = MimeMessageWriter.Compose(message);

        // O MimeKit guarda o identificador sem os sinais de menor e maior e os repõe ao
        // escrever o cabeçalho. A asserção é sobre o que vai para a rede, não sobre a
        // representação interna.
        mime.Headers[HeaderId.InReplyTo].Should().Be("<anterior@externo.com>");
        mime.References.Should().HaveCount(2);
        mime.Headers[HeaderId.References].Should().Contain("<primeira@externo.com>");
    }

    [Fact]
    public void Compor_PrioridadeAlta_DeclaraNosDoisCabecalhos()
    {
        // Importance é o do padrão; X-Priority é o que o Outlook lê. Só um deles faz a
        // prioridade sumir em metade dos clientes.
        var mime = MimeMessageWriter.Compose(Message() with { Importance = DomainImportance.High });

        mime.Importance.Should().Be(MessageImportance.High);
        mime.XPriority.Should().Be(XMessagePriority.High);
    }

    [Fact]
    public void Compor_PrioridadeNormal_NaoDeclaraNada()
    {
        var mime = MimeMessageWriter.Compose(Message());

        mime.Importance.Should().Be(MessageImportance.Normal);
    }

    [Fact]
    public void Compor_ComConfirmacaoDeLeitura_PedeAoClienteDoDestinatario()
    {
        var mime = MimeMessageWriter.Compose(Message() with { RequestReadReceipt = true });

        mime.Headers[HeaderId.DispositionNotificationTo].Should().Be("contato@sintek.com.br");
    }

    [Fact]
    public void Compor_EnderecoInvalidoNaLista_EIgnoradoSemDerrubarOEnvio()
    {
        // Um endereço malformado entre dez não pode impedir a mensagem de sair para os
        // outros nove.
        var mime = MimeMessageWriter.Compose(
            Message() with { To = ["cliente@externo.com", "isto não é endereço"] });

        mime.To.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("cliente@externo.com");
    }

    [Fact]
    public async Task Escrever_DevolveFluxoPosicionadoNoInicio()
    {
        // O APPEND lê o fluxo do começo; devolvê-lo no fim gravaria uma mensagem vazia.
        await using var stream = await new MimeMessageWriter().WriteAsync(Message());

        stream.Position.Should().Be(0);
        stream.Length.Should().BeGreaterThan(0);

        var reloaded = await MimeMessage.LoadAsync(stream);
        reloaded.Subject.Should().Be("Proposta comercial");
    }
}
