using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Um endereço para quem o usuário já escreveu, com quantas vezes e quando pela última vez.
/// </summary>
/// <remarks>
/// <para>
/// É o equivalente do cache de autocompletar do Outlook: o que aparece ao digitar em Para,
/// CC e CCO. Distinto de <see cref="Contact"/>, que é o catálogo de endereços — este aqui
/// não é curado por ninguém, cresce sozinho com o uso e existe só para poupar digitação.
/// </para>
/// <para>
/// A entrada é criada <b>no envio</b>, não na entrega. O que registra a intenção do usuário
/// é ter escrito para aquele endereço; se a mensagem falhou no SMTP, ele provavelmente vai
/// tentar de novo e quer a sugestão disponível.
/// </para>
/// <para>
/// O histórico pertence a uma conta. Duas contas do mesmo computador não compartilham
/// sugestões — em um cliente organizado por Diretório de Domínio, ver os contatos de um
/// cliente ao escrever para outro é vazamento de contexto, não conveniência.
/// </para>
/// </remarks>
public sealed class RecipientHistory : Entity
{
    private RecipientHistory(
        Guid id, Guid accountId, EmailAddress address, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        Address = address;
        UseCount = 1;
        LastUsedAt = createdAt;
    }

    private RecipientHistory()
    {
    }

    /// <summary>Conta que escreveu para este endereço.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Endereço sugerido.</summary>
    public EmailAddress Address { get; private set; } = null!;

    /// <summary>
    /// Nome exibido mais recente associado ao endereço.
    /// </summary>
    /// <remarks>
    /// Guardado sempre o mais recente, e não o primeiro: pessoas mudam de sobrenome e
    /// empresas mudam de padrão de nome, e a sugestão útil é a que reflete como a pessoa
    /// se apresenta hoje.
    /// </remarks>
    public string? DisplayName { get; private set; }

    /// <summary>Quantas vezes o usuário escreveu para este endereço.</summary>
    public int UseCount { get; private set; }

    /// <summary>Quando escreveu pela última vez.</summary>
    public DateTimeOffset LastUsedAt { get; private set; }

    /// <summary>Registra o primeiro uso de um endereço.</summary>
    public static RecipientHistory Create(
        Guid accountId,
        EmailAddress address,
        DateTimeOffset usedAt,
        string? displayName = null,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new RecipientHistory(id ?? Guid.CreateVersion7(), accountId, address, usedAt)
        {
            DisplayName = Normalize(displayName),
        };
    }

    /// <summary>Registra mais um uso.</summary>
    public void RegisterUse(DateTimeOffset usedAt, string? displayName = null)
    {
        UseCount++;
        LastUsedAt = usedAt;

        // Nome vazio não apaga o que já se sabia: escrever para um endereço sem digitar o
        // nome é o caso comum, e perder o nome por isso pioraria a sugestão seguinte.
        if (Normalize(displayName) is { } name)
        {
            DisplayName = name;
        }

        Touch(usedAt);
    }

    /// <summary>Texto exibido na sugestão: nome e endereço, ou só o endereço.</summary>
    public string SuggestionText => DisplayName is null
        ? Address.Value
        : $"{DisplayName} <{Address.Value}>";

    private static string? Normalize(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}
