using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Tests.Services;

/// <summary>
/// Cobre a seção 5.3 da especificação: os modos configuráveis de validação que decidem
/// se uma mensagem pertence a um Diretório de Domínio.
/// </summary>
public class DomainMembershipEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static DomainDirectory Directory(
        DomainValidationMode mode,
        bool allowSubdomains = false,
        string domain = "sintek.com.br")
        => DomainDirectory.Create(
            EmailDomain.Parse(domain),
            Now,
            validationMode: mode,
            allowSubdomains: allowSubdomains);

    private static MessageParticipant Participant(AddressKind kind, string address)
        => new(kind, EmailAddress.Parse(address).Domain);

    [Theory]
    // SenderOnly: só o remetente conta.
    [InlineData(DomainValidationMode.SenderOnly, "contato@sintek.com.br", "cliente@outro.com", true)]
    [InlineData(DomainValidationMode.SenderOnly, "externo@outro.com", "contato@sintek.com.br", false)]
    // RecipientOnly: só o destinatário conta.
    [InlineData(DomainValidationMode.RecipientOnly, "contato@sintek.com.br", "cliente@outro.com", false)]
    [InlineData(DomainValidationMode.RecipientOnly, "externo@outro.com", "contato@sintek.com.br", true)]
    // SenderOrRecipient: basta um dos dois.
    [InlineData(DomainValidationMode.SenderOrRecipient, "contato@sintek.com.br", "cliente@outro.com", true)]
    [InlineData(DomainValidationMode.SenderOrRecipient, "externo@outro.com", "contato@sintek.com.br", true)]
    [InlineData(DomainValidationMode.SenderOrRecipient, "externo@outro.com", "cliente@terceiro.com", false)]
    // SenderAndRecipient: exige os dois.
    [InlineData(DomainValidationMode.SenderAndRecipient, "contato@sintek.com.br", "financeiro@sintek.com.br", true)]
    [InlineData(DomainValidationMode.SenderAndRecipient, "contato@sintek.com.br", "cliente@outro.com", false)]
    [InlineData(DomainValidationMode.SenderAndRecipient, "externo@outro.com", "contato@sintek.com.br", false)]
    // AnyParticipant: qualquer um serve.
    [InlineData(DomainValidationMode.AnyParticipant, "contato@sintek.com.br", "cliente@outro.com", true)]
    [InlineData(DomainValidationMode.AnyParticipant, "externo@outro.com", "contato@sintek.com.br", true)]
    [InlineData(DomainValidationMode.AnyParticipant, "externo@outro.com", "cliente@terceiro.com", false)]
    public void Evaluate_RespeitaCadaModoDeValidacao(
        DomainValidationMode mode, string from, string to, bool expected)
    {
        var participants = new[]
        {
            Participant(AddressKind.From, from),
            Participant(AddressKind.To, to),
        };

        DomainMembershipEvaluator.Evaluate(Directory(mode), participants)
            .IsMember.Should().Be(expected);
    }

    [Fact]
    public void AnyParticipant_AceitaCorrespondenciaApenasEmCopia()
    {
        // A especificação lista explicitamente "um destinatário em cópia possui o
        // domínio" como critério suficiente.
        var participants = new[]
        {
            Participant(AddressKind.From, "externo@outro.com"),
            Participant(AddressKind.To, "cliente@terceiro.com"),
            Participant(AddressKind.Cc, "contato@sintek.com.br"),
        };

        DomainMembershipEvaluator.Evaluate(Directory(DomainValidationMode.AnyParticipant), participants)
            .IsMember.Should().BeTrue();
    }

    [Fact]
    public void RecipientOnly_NaoEhSatisfeitoPorCopia()
    {
        // Cópia não é destinatário direto: se fosse, RecipientOnly e AnyParticipant
        // seriam o mesmo modo e a configuração perderia sentido.
        var participants = new[]
        {
            Participant(AddressKind.From, "externo@outro.com"),
            Participant(AddressKind.To, "cliente@terceiro.com"),
            Participant(AddressKind.Cc, "contato@sintek.com.br"),
        };

        DomainMembershipEvaluator.Evaluate(Directory(DomainValidationMode.RecipientOnly), participants)
            .IsMember.Should().BeFalse();
    }

    [Fact]
    public void SenderAndRecipient_InformaQualLadoFaltou()
    {
        var directory = Directory(DomainValidationMode.SenderAndRecipient);

        var semDestinatario = DomainMembershipEvaluator.Evaluate(directory, new[]
        {
            Participant(AddressKind.From, "contato@sintek.com.br"),
            Participant(AddressKind.To, "cliente@outro.com"),
        });

        var semRemetente = DomainMembershipEvaluator.Evaluate(directory, new[]
        {
            Participant(AddressKind.From, "externo@outro.com"),
            Participant(AddressKind.To, "contato@sintek.com.br"),
        });

        semDestinatario.Reason.Should().Be(DomainMembershipReason.RecipientMissing);
        semRemetente.Reason.Should().Be(DomainMembershipReason.SenderMissing);
    }

    [Fact]
    public void Evaluate_AceitaQuandoUmaRegraExplicitaJaDecidiu()
    {
        // "A mensagem atende a uma regra explícita criada pelo usuário" é critério
        // suficiente por si só, independentemente dos participantes.
        var participants = new[] { Participant(AddressKind.From, "externo@outro.com") };

        var result = DomainMembershipEvaluator.Evaluate(
            Directory(DomainValidationMode.SenderOnly), participants, matchedExplicitRule: true);

        result.IsMember.Should().BeTrue();
        result.Reason.Should().Be(DomainMembershipReason.ExplicitRuleMatched);
    }

    [Fact]
    public void Evaluate_ConsideraDominiosAdicionaisPermitidos()
    {
        var directory = Directory(DomainValidationMode.AnyParticipant);
        directory.AddAlias(EmailDomain.Parse("sintek.tec.br"), Now);

        var participants = new[] { Participant(AddressKind.From, "contato@sintek.tec.br") };

        DomainMembershipEvaluator.Evaluate(directory, participants).IsMember.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RespeitaPermissaoDeSubdominios()
    {
        var participants = new[] { Participant(AddressKind.From, "contato@vendas.sintek.com.br") };

        DomainMembershipEvaluator
            .Evaluate(Directory(DomainValidationMode.SenderOnly), participants)
            .IsMember.Should().BeFalse();

        DomainMembershipEvaluator
            .Evaluate(Directory(DomainValidationMode.SenderOnly, allowSubdomains: true), participants)
            .IsMember.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_SemParticipantes_NaoPertence()
    {
        DomainMembershipEvaluator
            .Evaluate(Directory(DomainValidationMode.AnyParticipant), Array.Empty<MessageParticipant>())
            .IsMember.Should().BeFalse();
    }

    [Fact]
    public void GetUserMessage_UsaOTextoExigidoPelaEspecificacao()
    {
        var result = DomainMembershipEvaluator.Evaluate(
            Directory(DomainValidationMode.SenderOnly),
            new[] { Participant(AddressKind.From, "externo@outro.com") });

        result.GetUserMessage().Should().Be(
            "Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local.");
    }

    [Fact]
    public void Evaluate_ComMensagem_ExigeParticipantesCarregados()
    {
        // Avaliar uma mensagem cujos participantes não foram carregados produziria uma
        // recusa falsa — silenciar a regra é pior do que falhar alto.
        var message = Message.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "<a@b>", Now, Now, Now);

        var act = () => DomainMembershipEvaluator.Evaluate(
            Directory(DomainValidationMode.AnyParticipant), message);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*participantes*");
    }

    [Fact]
    public void Evaluate_ComMensagem_UsaOsParticipantesCarregados()
    {
        var message = Message.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "<a@b>", Now, Now, Now);
        message.AddAddress(MessageAddress.Create(
            message.Id, AddressKind.From, EmailAddress.Parse("contato@sintek.com.br"), Now));

        DomainMembershipEvaluator
            .Evaluate(Directory(DomainValidationMode.SenderOnly), message)
            .IsMember.Should().BeTrue();
    }
}
