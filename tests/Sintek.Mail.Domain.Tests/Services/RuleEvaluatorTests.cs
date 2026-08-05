using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre o motor de regras: os campos, os operadores e a combinação E/OU — as tabelas da
/// seção 6.5 da especificação.
/// </summary>
public class RuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    private static RuleMessageFacts Facts(
        string subject = "Proposta comercial",
        string body = "",
        string? from = "cliente@externo.com",
        IReadOnlyList<RuleParticipant>? participants = null,
        IReadOnlyList<string>? attachmentNames = null,
        bool hasAttachments = false,
        long size = 1024,
        MessageImportance importance = MessageImportance.Normal) => new()
    {
        AccountId = AccountId,
        Subject = subject,
        BodyText = body,
        FromAddress = from,
        FromDomain = from is null ? null : EmailAddress.Parse(from).Domain,
        Participants = participants ?? [],
        AttachmentNames = attachmentNames ?? [],
        HasAttachments = hasAttachments,
        Size = size,
        ReceivedAt = Now,
        Importance = importance,
    };

    private static Rule RuleWith(
        params (RuleField Field, RuleOperator Operator, string? Value)[] conditions)
        => RuleWith(RuleMatchType.All, conditions);

    private static Rule RuleWith(
        RuleMatchType matchType,
        params (RuleField Field, RuleOperator Operator, string? Value)[] conditions)
    {
        var rule = Rule.Create("Teste", Now, matchType: matchType);

        foreach (var (field, @operator, value) in conditions)
        {
            rule.AddCondition(field, @operator, value, Now);
        }

        return rule;
    }

    private static RuleParticipant Participant(AddressKind kind, string address)
        => new(kind, address, EmailAddress.Parse(address).Domain);

    // ----- Campos e operadores ---------------------------------------------------------

    [Fact]
    public void Avaliar_AssuntoContem_IgnoraMaiusculas()
    {
        var rule = RuleWith((RuleField.Subject, RuleOperator.Contains, "PROPOSTA"));

        RuleEvaluator.Matches(rule, Facts(subject: "Proposta comercial")).Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(subject: "Fatura")).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_RemetenteNoDominio_CobreSubdominio()
    {
        // InDomain usa a regra de domínio, não comparação de texto: "sintek.com.br"
        // alcança "vendas.sintek.com.br" — e não alcança "narcosintek.com.br".
        var rule = RuleWith((RuleField.Sender, RuleOperator.InDomain, "sintek.com.br"));

        RuleEvaluator.Matches(rule, Facts(from: "ana@vendas.sintek.com.br")).Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(from: "ana@narcosintek.com.br")).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_DestinatarioECopia_NaoSeConfundem()
    {
        var facts = Facts(participants:
        [
            Participant(AddressKind.To, "direto@cliente.com"),
            Participant(AddressKind.Cc, "copiado@cliente.com"),
        ]);

        var paraRule = RuleWith((RuleField.Recipient, RuleOperator.Contains, "copiado"));
        var ccRule = RuleWith((RuleField.Cc, RuleOperator.Contains, "copiado"));

        RuleEvaluator.Matches(paraRule, facts).Should().BeFalse();
        RuleEvaluator.Matches(ccRule, facts).Should().BeTrue();
    }

    [Fact]
    public void Avaliar_NaoContem_ExigeQueNenhumValorContenha()
    {
        // "CC não contém fulano" com dois endereços só é verdade se nenhum for o fulano.
        var rule = RuleWith((RuleField.Cc, RuleOperator.NotContains, "chefe"));

        var comChefe = Facts(participants:
        [
            Participant(AddressKind.Cc, "colega@empresa.com"),
            Participant(AddressKind.Cc, "chefe@empresa.com"),
        ]);
        var semChefe = Facts(participants: [Participant(AddressKind.Cc, "colega@empresa.com")]);

        RuleEvaluator.Matches(rule, comChefe).Should().BeFalse();
        RuleEvaluator.Matches(rule, semChefe).Should().BeTrue();
    }

    [Fact]
    public void Avaliar_PresencaDeAnexo_UsaOperadorBooleano()
    {
        var comAnexo = RuleWith((RuleField.HasAttachment, RuleOperator.IsTrue, null));
        var semAnexo = RuleWith((RuleField.HasAttachment, RuleOperator.IsFalse, null));

        RuleEvaluator.Matches(comAnexo, Facts(hasAttachments: true)).Should().BeTrue();
        RuleEvaluator.Matches(comAnexo, Facts(hasAttachments: false)).Should().BeFalse();
        RuleEvaluator.Matches(semAnexo, Facts(hasAttachments: false)).Should().BeTrue();
    }

    [Fact]
    public void Avaliar_NomeDeAnexo_EncontraEmQualquerAnexo()
    {
        var rule = RuleWith((RuleField.AttachmentName, RuleOperator.EndsWith, ".pdf"));

        RuleEvaluator.Matches(rule, Facts(attachmentNames: ["foto.png", "contrato.pdf"]))
            .Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(attachmentNames: ["foto.png"])).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_TamanhoMaiorQue_CompararNumero_NaoTexto()
    {
        var rule = RuleWith((RuleField.Size, RuleOperator.GreaterThan, "1000000"));

        // "999999" > "1000000" na ordem alfabética — o teste garante comparação numérica.
        RuleEvaluator.Matches(rule, Facts(size: 999_999)).Should().BeFalse();
        RuleEvaluator.Matches(rule, Facts(size: 2_000_000)).Should().BeTrue();
    }

    [Fact]
    public void Avaliar_Importancia_PorNomeDoEnum()
    {
        var rule = RuleWith((RuleField.Importance, RuleOperator.Equals, "High"));

        RuleEvaluator.Matches(rule, Facts(importance: MessageImportance.High)).Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(importance: MessageImportance.Normal)).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_DominioDeParticipante_OlhaTodosOsCampos()
    {
        var rule = RuleWith((RuleField.ParticipantDomain, RuleOperator.InDomain, "cliente.com"));

        var facts = Facts(
            from: "alguem@outro.com",
            participants: [Participant(AddressKind.Bcc, "oculto@cliente.com")]);

        RuleEvaluator.Matches(rule, facts).Should().BeTrue();
    }

    // ----- Combinação e estado ---------------------------------------------------------

    [Fact]
    public void Avaliar_ModoTodas_ExigeCadaCondicao()
    {
        var rule = RuleWith(
            RuleMatchType.All,
            (RuleField.Subject, RuleOperator.Contains, "proposta"),
            (RuleField.HasAttachment, RuleOperator.IsTrue, null));

        RuleEvaluator.Matches(rule, Facts(subject: "Proposta", hasAttachments: true)).Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(subject: "Proposta", hasAttachments: false)).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_ModoQualquer_BastaUmaCondicao()
    {
        var rule = RuleWith(
            RuleMatchType.Any,
            (RuleField.Subject, RuleOperator.Contains, "urgente"),
            (RuleField.Importance, RuleOperator.Equals, "High"));

        RuleEvaluator.Matches(rule, Facts(subject: "Nada de mais", importance: MessageImportance.High))
            .Should().BeTrue();
        RuleEvaluator.Matches(rule, Facts(subject: "Nada de mais")).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_RegraDesativada_NuncaCasa()
    {
        var rule = RuleWith((RuleField.Subject, RuleOperator.Contains, "proposta"));
        rule.SetEnabled(false, Now);

        RuleEvaluator.Matches(rule, Facts(subject: "Proposta")).Should().BeFalse();
    }

    [Fact]
    public void Avaliar_RegraSemCondicoes_CasaComTodaMensagem()
    {
        // O "aplicar a todas" do Outlook: útil para categorização geral de uma conta.
        RuleEvaluator.Matches(Rule.Create("Tudo", Now), Facts()).Should().BeTrue();
    }

    [Fact]
    public void Avaliar_ValorNumericoInvalido_NaoCasaNemLanca()
    {
        var rule = RuleWith((RuleField.Size, RuleOperator.GreaterThan, "abc"));

        RuleEvaluator.Matches(rule, Facts(size: 5000)).Should().BeFalse();
    }
}

/// <summary>
/// Cobre as listas de remetentes: alcance por endereço, por domínio e por conta.
/// </summary>
public class SenderReputationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();

    [Fact]
    public void PorEndereco_AlcancaSomenteOEnderecoExato()
    {
        var entry = SenderReputation.ForAddress(
            SenderReputationKind.Blocked, EmailAddress.Parse("spam@promo.com"), Now);

        entry.AppliesTo(EmailAddress.Parse("SPAM@promo.com"), AccountId).Should().BeTrue();
        entry.AppliesTo(EmailAddress.Parse("outro@promo.com"), AccountId).Should().BeFalse();
    }

    [Fact]
    public void PorDominio_AlcancaOsSubdominios()
    {
        // Remetentes de massa se espalham por subdomínios; bloquear "promo.com" precisa
        // derrubar "mail.promo.com" junto.
        var entry = SenderReputation.ForDomain(
            SenderReputationKind.Blocked, EmailDomain.Parse("promo.com"), Now);

        entry.AppliesTo(EmailAddress.Parse("news@mail.promo.com"), AccountId).Should().BeTrue();
        entry.AppliesTo(EmailAddress.Parse("news@outra.com"), AccountId).Should().BeFalse();
    }

    [Fact]
    public void EscopoDeConta_NaoVazaParaOutraConta()
    {
        var entry = SenderReputation.ForDomain(
            SenderReputationKind.Trusted, EmailDomain.Parse("parceiro.com"), Now, accountId: AccountId);

        entry.AppliesTo(EmailAddress.Parse("a@parceiro.com"), AccountId).Should().BeTrue();
        entry.AppliesTo(EmailAddress.Parse("a@parceiro.com"), Guid.CreateVersion7()).Should().BeFalse();
    }
}
