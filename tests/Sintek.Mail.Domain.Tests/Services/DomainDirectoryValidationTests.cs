using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre literalmente as tabelas de validação da seção 5.2 da especificação.
/// </summary>
public class DomainDirectoryValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static DomainDirectory Directory(string domain, bool allowSubdomains = false)
        => DomainDirectory.Create(
            EmailDomain.Parse(domain),
            Now,
            allowSubdomains: allowSubdomains);

    // Tabela 1 da especificação (5.2): validação de contas contra o Diretório de Domínio.
    [Theory]
    [InlineData("sintek.com.br", "contato@sintek.com.br", true)]
    [InlineData("sintek.com.br", "financeiro@sintek.com.br", true)]
    [InlineData("sintek.com.br", "admin@gmail.com", false)]
    [InlineData("sintek.com.br", "suporte@cliente.com.br", false)]
    [InlineData("cliente.com.br", "suporte@cliente.com.br", true)]
    public void Accepts_ReproduzTabelaDaEspecificacao(string directoryDomain, string account, bool expected)
    {
        Directory(directoryDomain)
            .Accepts(EmailAddress.Parse(account))
            .Should().Be(expected);
    }

    // Tabela 2 da especificação (5.2): subdomínios são bloqueados por padrão.
    [Theory]
    [InlineData("empresa.com", "usuario@empresa.com", true)]
    [InlineData("empresa.com", "usuario@vendas.empresa.com", false)]
    [InlineData("empresa.com", "usuario@gmail.com", false)]
    public void Accepts_BloqueiaSubdominiosPorPadrao(string directoryDomain, string account, bool expected)
    {
        Directory(directoryDomain)
            .Accepts(EmailAddress.Parse(account))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("empresa.com", "usuario@vendas.empresa.com", true)]
    [InlineData("empresa.com", "usuario@a.b.empresa.com", true)]
    [InlineData("empresa.com", "usuario@gmail.com", false)]
    public void Accepts_AceitaSubdominios_QuandoConfiguracaoExplicitaPermite(
        string directoryDomain, string account, bool expected)
    {
        Directory(directoryDomain, allowSubdomains: true)
            .Accepts(EmailAddress.Parse(account))
            .Should().Be(expected);
    }

    [Fact]
    public void ValidateAccount_Lanca_QuandoDominioDiverge()
    {
        var directory = Directory("sintek.com.br");
        var account = EmailAddress.Parse("admin@gmail.com");

        var act = () => directory.ValidateAccount(account);

        act.Should().Throw<DomainMismatchException>()
            .Which.ActualDomain.Value.Should().Be("gmail.com");
    }

    [Fact]
    public void ValidateAccount_Orienta_QuandoARecusaEhPorSubdominio()
    {
        // A recusa de um subdomínio costuma surpreender o usuário: a mensagem precisa
        // apontar a configuração que a resolveria, não apenas negar.
        var directory = Directory("empresa.com");

        var act = () => directory.ValidateAccount(EmailAddress.Parse("usuario@vendas.empresa.com"));

        act.Should().Throw<DomainMismatchException>()
            .Which.Message.Should().Contain("Permitir subdomínios");
    }

    [Fact]
    public void ValidateAccount_NaoLanca_QuandoDominioConfere()
    {
        var directory = Directory("sintek.com.br");

        var act = () => directory.ValidateAccount(EmailAddress.Parse("contato@sintek.com.br"));

        act.Should().NotThrow();
    }

    [Fact]
    public void ComparacaoDeDominio_IgnoraCaixaEEspacos()
    {
        var directory = Directory("sintek.com.br");

        directory.Accepts(EmailAddress.Parse("  Contato@SINTEK.COM.BR  ")).Should().BeTrue();
    }

    [Fact]
    public void AddAlias_PermiteDominioAdicional()
    {
        // A especificação prevê que uma mensagem pertença ao domínio quando "o domínio
        // está registrado como domínio adicional permitido".
        var directory = Directory("sintek.com.br");
        directory.AddAlias(EmailDomain.Parse("sintek.tec.br"), Now);

        directory.Accepts(EmailAddress.Parse("contato@sintek.tec.br")).Should().BeTrue();
        directory.Accepts(EmailAddress.Parse("contato@outro.com.br")).Should().BeFalse();
    }

    [Fact]
    public void AddAlias_EhIdempotente()
    {
        var directory = Directory("sintek.com.br");

        directory.AddAlias(EmailDomain.Parse("sintek.tec.br"), Now);
        directory.AddAlias(EmailDomain.Parse("SINTEK.TEC.BR"), Now);

        directory.Aliases.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveAlias_RevogaODominioAdicional()
    {
        var directory = Directory("sintek.com.br");
        directory.AddAlias(EmailDomain.Parse("sintek.tec.br"), Now);

        directory.RemoveAlias(EmailDomain.Parse("sintek.tec.br"), Now).Should().BeTrue();
        directory.Accepts(EmailAddress.Parse("contato@sintek.tec.br")).Should().BeFalse();
    }

    [Fact]
    public void AttachAccount_Recusa_ContaDeOutroDominio()
    {
        var directory = Directory("sintek.com.br");
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("admin@gmail.com"), "Admin", Now);

        var act = () => directory.AttachAccount(account, Now);

        act.Should().Throw<DomainMismatchException>();
        directory.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void AttachAccount_VinculaMultiplasContasDoMesmoDominio()
    {
        // A especificação exige permitir múltiplas contas no mesmo Diretório de Domínio.
        var directory = Directory("sintek.com.br");

        directory.AttachAccount(
            Account.Create(directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now), Now);
        directory.AttachAccount(
            Account.Create(directory.Id, EmailAddress.Parse("financeiro@sintek.com.br"), "Financeiro", Now), Now);

        directory.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public void ChangeDomainName_AlteraODominioDoDiretorio()
    {
        var directory = Directory("antigo.com.br");

        directory.ChangeDomainName(EmailDomain.Parse("novo.com.br"), Now);

        directory.DomainName.Value.Should().Be("novo.com.br");
        directory.Accepts(EmailAddress.Parse("contato@novo.com.br")).Should().BeTrue();
        directory.Accepts(EmailAddress.Parse("contato@antigo.com.br")).Should().BeFalse();
    }
}
