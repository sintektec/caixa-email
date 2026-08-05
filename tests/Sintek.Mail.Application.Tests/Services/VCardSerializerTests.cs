using AwesomeAssertions;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Tests.Services;

/// <summary>
/// Cobre a leitura e a escrita de vCard — o formato pelo qual o catálogo entra e sai. A
/// exigência que orienta os casos é não perder o arquivo inteiro por causa de um cartão
/// ruim: quem exporta do Outlook traz propriedades desconhecidas e endereços inválidos.
/// </summary>
public class VCardSerializerTests
{
    private const string CartaoSimples = """
        BEGIN:VCARD
        VERSION:3.0
        FN:Ana Souza
        N:Souza;Ana;;;
        ORG:Cliente Ltda
        TITLE:Diretora
        TEL;TYPE=VOICE:+55 81 99999-0000
        EMAIL;TYPE=INTERNET:ana@cliente.com.br
        UID:urn:uuid:8f0c-ana
        END:VCARD
        """;

    [Fact]
    public void Read_CartaoCompleto_ExtraiTodosOsCampos()
    {
        var resultado = VCardSerializer.Read(CartaoSimples);

        var contato = resultado.Contacts.Should().ContainSingle().Subject;
        contato.DisplayName.Should().Be("Ana Souza");
        contato.GivenName.Should().Be("Ana");
        contato.FamilyName.Should().Be("Souza");
        contato.Organization.Should().Be("Cliente Ltda");
        contato.JobTitle.Should().Be("Diretora");
        contato.PhoneNumber.Should().Be("+55 81 99999-0000");
        contato.Uid.Should().Be("urn:uuid:8f0c-ana");
        contato.Emails.Should().ContainSingle()
            .Which.Address.Should().Be(EmailAddress.Parse("ana@cliente.com.br"));
    }

    [Fact]
    public void Read_VariosCartoes_DevolveTodos()
    {
        var conteudo = CartaoSimples + "\r\n" + """
            BEGIN:VCARD
            VERSION:3.0
            FN:Bruno Lima
            EMAIL:bruno@cliente.com.br
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts.Should().HaveCount(2);
    }

    [Fact]
    public void Read_LinhaDobrada_RemontaOValor()
    {
        // O vCard quebra aos 75 octetos e marca a continuação com espaço inicial. Sem
        // remontar, o endereço chegaria partido e seria descartado como inválido.
        var conteudo = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ana\r\nEMAIL:ana.souza.diretoria\r\n @cliente.com.br\r\nEND:VCARD\r\n";

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts.Should().ContainSingle()
            .Which.Emails[0].Address.Should().Be(
                EmailAddress.Parse("ana.souza.diretoria@cliente.com.br"));
    }

    [Fact]
    public void Read_PrefDoVCard3_MarcaOEnderecoComoPreferencial()
    {
        var conteudo = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana
            EMAIL;TYPE=INTERNET:secundario@cliente.com.br
            EMAIL;TYPE=INTERNET,PREF:principal@cliente.com.br
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].Emails[0].Address.Should().Be(
            EmailAddress.Parse("principal@cliente.com.br"));
    }

    [Fact]
    public void Read_PrefDoVCard4_MarcaOEnderecoComoPreferencial()
    {
        // No 4.0 a marca virou PREF=1. Aceitar as duas formas é o que preserva qual
        // endereço é o principal, seja qual for o cliente de origem.
        var conteudo = """
            BEGIN:VCARD
            VERSION:4.0
            FN:Ana
            EMAIL:secundario@cliente.com.br
            EMAIL;PREF=1:principal@cliente.com.br
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].Emails[0].Address.Should().Be(
            EmailAddress.Parse("principal@cliente.com.br"));
    }

    [Fact]
    public void Read_CartaoSemNomeESemEndereco_EIgnoradoSemDerrubarOsOutros()
    {
        var conteudo = """
            BEGIN:VCARD
            VERSION:3.0
            NOTE:cartao vazio
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Bruno Lima
            EMAIL:bruno@cliente.com.br
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts.Should().ContainSingle();
        resultado.SkippedCards.Should().Be(1);
    }

    [Fact]
    public void Read_EnderecoInvalido_EDescartadoSemDerrubarOContato()
    {
        var conteudo = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Ana Souza
            EMAIL:isto nao e um endereco
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        var contato = resultado.Contacts.Should().ContainSingle().Subject;
        contato.DisplayName.Should().Be("Ana Souza");
        contato.Emails.Should().BeEmpty();
    }

    [Fact]
    public void Read_SemFn_MontaONomeAPartirDeN()
    {
        var conteudo = """
            BEGIN:VCARD
            VERSION:3.0
            N:Souza;Ana;;;
            EMAIL:ana@cliente.com.br
            END:VCARD
            """;

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].DisplayName.Should().Be("Ana Souza");
    }

    [Fact]
    public void Read_SemNomeAlgum_CaiParaOEndereco()
    {
        var conteudo = "BEGIN:VCARD\r\nVERSION:3.0\r\nEMAIL:ana@cliente.com.br\r\nEND:VCARD\r\n";

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].DisplayName.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public void Read_ValorComPontoEVirgulaEscapado_NaoQuebraOCampo()
    {
        var conteudo = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Souza\\; Ana\r\nEMAIL:ana@cliente.com.br\r\nEND:VCARD\r\n";

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].DisplayName.Should().Be("Souza; Ana");
    }

    [Fact]
    public void Read_PropriedadeComGrupo_EReconhecidaIgual()
    {
        // O formato do iOS prefixa a propriedade com um grupo ("item1.EMAIL").
        var conteudo = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ana\r\nitem1.EMAIL:ana@cliente.com.br\r\nEND:VCARD\r\n";

        var resultado = VCardSerializer.Read(conteudo);

        resultado.Contacts[0].Emails.Should().ContainSingle();
    }

    [Fact]
    public void Read_ConteudoVazio_DevolveListaVazia()
    {
        var resultado = VCardSerializer.Read(string.Empty);

        resultado.Contacts.Should().BeEmpty();
        resultado.SkippedCards.Should().Be(0);
    }

    [Fact]
    public void Write_DepoisDeLer_PreservaOsCampos()
    {
        var original = VCardSerializer.Read(CartaoSimples).Contacts[0];

        var relido = VCardSerializer.Read(VCardSerializer.Write([original])).Contacts[0];

        relido.DisplayName.Should().Be(original.DisplayName);
        relido.GivenName.Should().Be(original.GivenName);
        relido.FamilyName.Should().Be(original.FamilyName);
        relido.Organization.Should().Be(original.Organization);
        relido.JobTitle.Should().Be(original.JobTitle);
        relido.PhoneNumber.Should().Be(original.PhoneNumber);
        relido.Uid.Should().Be(original.Uid);
        relido.Emails[0].Address.Should().Be(original.Emails[0].Address);
    }

    [Fact]
    public void Write_NomeComVirgula_SobreviveAoIdaEVolta()
    {
        var contato = new VCardContact(
            null, "Souza, Ana", "Ana", "Souza", null, null, null, null,
            [new VCardEmail(EmailAddress.Parse("ana@cliente.com.br"), null, true)]);

        var relido = VCardSerializer.Read(VCardSerializer.Write([contato])).Contacts[0];

        relido.DisplayName.Should().Be("Souza, Ana");
    }

    [Fact]
    public void Write_ContatoComPreferencial_MarcaOPrefNaSaida()
    {
        var contato = new VCardContact(
            null, "Ana", null, null, null, null, null, null,
            [
                new VCardEmail(EmailAddress.Parse("principal@cliente.com.br"), null, true),
                new VCardEmail(EmailAddress.Parse("secundario@cliente.com.br"), null, false),
            ]);

        var texto = VCardSerializer.Write([contato]);

        texto.Should().Contain("EMAIL;TYPE=INTERNET,PREF:principal@cliente.com.br");
    }
}
