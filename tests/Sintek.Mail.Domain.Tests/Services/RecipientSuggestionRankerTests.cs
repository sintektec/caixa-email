using AwesomeAssertions;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre a ordenação das sugestões de destinatário — a única parte do autocompletar que o
/// usuário percebe. A regra do produto está aqui: endereço fora do Diretório de Domínio da
/// conta é <b>marcado</b>, nunca escondido.
/// </summary>
public class RecipientSuggestionRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Conta = Guid.CreateVersion7();

    private static readonly DomainDirectory Diretorio =
        DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now);

    private static EmailAddress Endereco(string value) => EmailAddress.Parse(value);

    private static RecipientHistory Historico(
        string address, int usos, DateTimeOffset ultimoUso, string? nome = null)
    {
        var entrada = RecipientHistory.Create(Conta, Endereco(address), ultimoUso, nome);

        for (var i = 1; i < usos; i++)
        {
            entrada.RegisterUse(ultimoUso, nome);
        }

        return entrada;
    }

    private static Contact Contato(string nome, string address)
    {
        var contato = Contact.Create(Conta, nome, Now);
        contato.AddEmail(Endereco(address), Now, isPrimary: true);

        return contato;
    }

    [Fact]
    public void Rank_TermoVazio_DevolveOsMaisUsados()
    {
        var historico = new[]
        {
            Historico("raro@cliente.com.br", 1, Now),
            Historico("frequente@cliente.com.br", 20, Now),
        };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes.Should().HaveCount(2);
        sugestoes[0].Address.Value.Should().Be("frequente@cliente.com.br");
    }

    [Fact]
    public void Rank_TermoCasaComONome_EncontraMesmoSemCasarComOEndereco()
    {
        var historico = new[] { Historico("js@cliente.com.br", 3, Now, "João Silva") };

        var sugestoes = RecipientSuggestionRanker.Rank("silva", historico, [], Diretorio, Now);

        sugestoes.Should().ContainSingle()
            .Which.Address.Value.Should().Be("js@cliente.com.br");
    }

    [Fact]
    public void Rank_TermoEmCaixaDiferente_ContinuaCasando()
    {
        var historico = new[] { Historico("Ana@cliente.com.br", 3, Now, "Ana") };

        var sugestoes = RecipientSuggestionRanker.Rank("ANA", historico, [], Diretorio, Now);

        sugestoes.Should().HaveCount(1);
    }

    [Fact]
    public void Rank_UsoAntigoContraUsoRecente_ORecenteVemPrimeiro()
    {
        // Dez usos há um ano contra três usos hoje: só frequência daria a vitória ao
        // primeiro, e é justamente esse o comportamento que o decaimento corrige.
        var historico = new[]
        {
            Historico("antigo@cliente.com.br", 10, Now.AddDays(-365)),
            Historico("recente@cliente.com.br", 3, Now),
        };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes[0].Address.Value.Should().Be("recente@cliente.com.br");
    }

    [Fact]
    public void Rank_ContatoEHistoricoEmpatados_OContatoVemPrimeiro()
    {
        var historico = new[] { Historico("outro@cliente.com.br", 50, Now) };
        var contatos = new[] { Contato("Ana Souza", "ana@cliente.com.br") };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, contatos, Diretorio, Now);

        sugestoes[0].Source.Should().Be(RecipientSuggestionSource.Contact);
    }

    [Fact]
    public void Rank_MesmoEnderecoNoCatalogoENoHistorico_ApareceUmaVezSo()
    {
        var historico = new[] { Historico("ana@cliente.com.br", 5, Now) };
        var contatos = new[] { Contato("Ana Souza", "ana@cliente.com.br") };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, contatos, Diretorio, Now);

        sugestoes.Should().ContainSingle();
        sugestoes[0].Source.Should().Be(RecipientSuggestionSource.Contact);
        sugestoes[0].DisplayName.Should().Be("Ana Souza");
    }

    [Fact]
    public void Rank_EnderecoForaDoDominioDaConta_ApareceMarcado()
    {
        var historico = new[] { Historico("externo@outraempresa.com", 5, Now) };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes.Should().ContainSingle()
            .Which.BelongsToAccountDomain.Should().BeFalse();
    }

    [Fact]
    public void Rank_EnderecoDoDominioDaConta_NaoEMarcado()
    {
        var historico = new[] { Historico("colega@sintek.com.br", 5, Now) };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes[0].BelongsToAccountDomain.Should().BeTrue();
    }

    [Fact]
    public void Rank_MuitosCandidatos_RespeitaOTeto()
    {
        var historico = Enumerable.Range(0, 30)
            .Select(i => Historico($"contato{i}@cliente.com.br", i + 1, Now))
            .ToList();

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes.Should().HaveCount(RecipientSuggestionRanker.DefaultLimit);
    }

    [Fact]
    public void Rank_SemDiretorio_NaoMarcaNada()
    {
        var historico = new[] { Historico("externo@outraempresa.com", 5, Now) };

        var sugestoes = RecipientSuggestionRanker.Rank(
            string.Empty, historico, [], accountDirectory: null, Now);

        sugestoes[0].BelongsToAccountDomain.Should().BeTrue();
    }

    [Fact]
    public void DisplayText_ComNome_MostraNomeEEndereco()
    {
        var historico = new[] { Historico("ana@cliente.com.br", 1, Now, "Ana") };

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, historico, [], Diretorio, Now);

        sugestoes[0].DisplayText.Should().Be("Ana <ana@cliente.com.br>");
    }

    [Fact]
    public void Rank_ContatoComEnderecoSecundario_OPrincipalVemPrimeiro()
    {
        var contato = Contact.Create(Conta, "Ana Souza", Now);
        contato.AddEmail(Endereco("secundario@cliente.com.br"), Now);
        contato.AddEmail(Endereco("principal@cliente.com.br"), Now, isPrimary: true);

        var sugestoes = RecipientSuggestionRanker.Rank(string.Empty, [], [contato], Diretorio, Now);

        sugestoes[0].Address.Value.Should().Be("principal@cliente.com.br");
    }
}
