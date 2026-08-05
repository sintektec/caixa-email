using AwesomeAssertions;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Entities;

/// <summary>
/// Cobre o catálogo de contatos e o histórico de destinatários — as duas fontes do
/// autocompletar de Para, CC e CCO.
/// </summary>
public class ContactAndRecipientHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Conta = Guid.CreateVersion7();

    private static EmailAddress Endereco(string value) => EmailAddress.Parse(value);

    [Fact]
    public void Create_PrimeiroUso_ComecaComContadorUm()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now, "Ana");

        entrada.UseCount.Should().Be(1);
        entrada.LastUsedAt.Should().Be(Now);
        entrada.DisplayName.Should().Be("Ana");
    }

    [Fact]
    public void RegisterUse_NovoEnvio_IncrementaContadorEAtualizaUltimoUso()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now);
        var depois = Now.AddDays(3);

        entrada.RegisterUse(depois);

        entrada.UseCount.Should().Be(2);
        entrada.LastUsedAt.Should().Be(depois);
    }

    [Fact]
    public void RegisterUse_SemNome_NaoApagaONomeJaConhecido()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now, "Ana Souza");

        entrada.RegisterUse(Now.AddDays(1));

        entrada.DisplayName.Should().Be("Ana Souza");
    }

    [Fact]
    public void RegisterUse_NomeNovo_SubstituiOAnterior()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now, "Ana Souza");

        entrada.RegisterUse(Now.AddDays(1), "Ana Souza Lima");

        entrada.DisplayName.Should().Be("Ana Souza Lima");
    }

    [Fact]
    public void SuggestionText_SemNome_MostraSoOEndereco()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now);

        entrada.SuggestionText.Should().Be("ana@cliente.com.br");
    }

    [Fact]
    public void SuggestionText_ComNome_MostraNomeEEndereco()
    {
        var entrada = RecipientHistory.Create(Conta, Endereco("ana@cliente.com.br"), Now, "Ana");

        entrada.SuggestionText.Should().Be("Ana <ana@cliente.com.br>");
    }

    [Fact]
    public void AddEmail_PrimeiroEndereco_ViraOPrincipalSemPedir()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);

        contato.AddEmail(Endereco("ana@cliente.com.br"), Now);

        contato.PrimaryEmail!.Address.Should().Be(Endereco("ana@cliente.com.br"));
        contato.PrimaryEmail.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void AddEmail_SegundoMarcadoComoPrincipal_RetiraAMarcaDoPrimeiro()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);
        contato.AddEmail(Endereco("ana@cliente.com.br"), Now);

        contato.AddEmail(Endereco("ana.souza@pessoal.com"), Now, isPrimary: true);

        contato.Emails.Should().ContainSingle(e => e.IsPrimary);
        contato.PrimaryEmail!.Address.Should().Be(Endereco("ana.souza@pessoal.com"));
    }

    [Fact]
    public void AddEmail_EnderecoRepetido_NaoDuplica()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);
        contato.AddEmail(Endereco("ana@cliente.com.br"), Now);

        contato.AddEmail(Endereco("ana@cliente.com.br"), Now);

        contato.Emails.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveEmail_EraOPrincipalEAindaHaOutros_PromoveUmDosRestantes()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);
        contato.AddEmail(Endereco("ana@cliente.com.br"), Now, isPrimary: true);
        contato.AddEmail(Endereco("ana.souza@pessoal.com"), Now);

        contato.RemoveEmail(Endereco("ana@cliente.com.br"), Now);

        contato.PrimaryEmail.Should().NotBeNull();
        contato.PrimaryEmail!.Address.Should().Be(Endereco("ana.souza@pessoal.com"));
    }

    [Fact]
    public void RemoveEmail_UltimoEndereco_DeixaOContatoSemPrincipal()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);
        contato.AddEmail(Endereco("ana@cliente.com.br"), Now);

        contato.RemoveEmail(Endereco("ana@cliente.com.br"), Now);

        contato.Emails.Should().BeEmpty();
        contato.PrimaryEmail.Should().BeNull();
    }

    [Fact]
    public void Create_NomeEmBranco_Recusa()
    {
        var criar = () => Contact.Create(Conta, "   ", Now);

        criar.Should().Throw<ArgumentException>();
    }
}
