using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Um participante de uma mensagem: remetente, destinatário, cópia ou cópia oculta.
/// </summary>
/// <remarks>
/// Esta tabela é o que torna a regra de Diretório de Domínio viável em escala. O
/// domínio de cada participante é extraído e gravado já normalizado em
/// <see cref="Domain"/>, com índice próprio, de modo que perguntar "esta mensagem tem
/// algum participante em <c>sintek.com.br</c>?" vira uma busca indexada.
///
/// A alternativa — guardar os destinatários como uma string única e interpretá-la a cada
/// avaliação — obrigaria a ler e reprocessar a caixa inteira sempre que uma pasta
/// restrita fosse aberta ou uma regra fosse aplicada.
/// </remarks>
public sealed class MessageAddress : Entity
{
    private MessageAddress(
        Guid id,
        Guid messageId,
        AddressKind kind,
        EmailAddress address,
        string? displayName,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        MessageId = messageId;
        Kind = kind;
        Address = address;
        Domain = address.Domain;
        DisplayName = displayName;
    }

    private MessageAddress()
    {
    }

    /// <summary>Mensagem à qual o participante pertence.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Mensagem à qual o participante pertence.</summary>
    public Message? Message { get; private set; }

    /// <summary>Em que campo o participante aparece.</summary>
    public AddressKind Kind { get; private set; }

    /// <summary>Endereço do participante.</summary>
    public EmailAddress Address { get; private set; } = null!;

    /// <summary>
    /// Domínio do participante, desnormalizado e indexado. Sempre igual a
    /// <c>Address.Domain</c> — existe como coluna própria para poder ser indexado.
    /// </summary>
    public EmailDomain Domain { get; private set; } = null!;

    /// <summary>Nome exibido, quando o cabeçalho o traz.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Cria um participante de mensagem.</summary>
    public static MessageAddress Create(
        Guid messageId,
        AddressKind kind,
        EmailAddress address,
        DateTimeOffset createdAt,
        string? displayName = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new MessageAddress(
            id ?? Guid.CreateVersion7(),
            messageId,
            kind,
            address,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            createdAt);
    }
}
