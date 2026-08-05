using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Um contato do catálogo de endereços.
/// </summary>
/// <remarks>
/// <para>
/// Distinto de <see cref="RecipientHistory"/>: o contato é criado e mantido pelo usuário,
/// tem os campos que ele preencheu e sobrevive a limpezas do histórico. O histórico é
/// automático, descartável e existe só para poupar digitação.
/// </para>
/// <para>
/// O contato pertence a uma conta e, por consequência, a um Diretório de Domínio. Em um
/// produto organizado por cliente, o catálogo segmentado por conta é o comportamento
/// esperado — misturar tudo numa lista só desfaria a separação que é a razão de ser do
/// resto.
/// </para>
/// </remarks>
public sealed class Contact : Entity
{
    private readonly List<ContactEmail> _emails = [];

    private Contact(Guid id, Guid accountId, string displayName, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        DisplayName = displayName;
    }

    private Contact()
    {
    }

    /// <summary>Conta dona do contato.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Nome exibido — o único campo obrigatório.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Primeiro nome.</summary>
    public string? GivenName { get; private set; }

    /// <summary>Sobrenome.</summary>
    public string? FamilyName { get; private set; }

    /// <summary>Empresa.</summary>
    public string? Organization { get; private set; }

    /// <summary>Cargo.</summary>
    public string? JobTitle { get; private set; }

    /// <summary>Telefone principal.</summary>
    public string? PhoneNumber { get; private set; }

    /// <summary>Anotações livres.</summary>
    public string? Notes { get; private set; }

    /// <summary>
    /// Identificador do contato na origem, quando veio de uma importação.
    /// </summary>
    /// <remarks>
    /// É o <c>UID</c> do vCard. Guardá-lo é o que permite reimportar o mesmo arquivo sem
    /// duplicar tudo — o caso normal de quem exporta do Outlook mais de uma vez.
    /// </remarks>
    public string? ExternalId { get; private set; }

    /// <summary>Endereços do contato.</summary>
    public IReadOnlyCollection<ContactEmail> Emails => _emails;

    /// <summary>Endereço principal, usado ao inserir o contato em um campo de destinatário.</summary>
    public ContactEmail? PrimaryEmail
        => _emails.FirstOrDefault(e => e.IsPrimary) ?? _emails.FirstOrDefault();

    /// <summary>Cria um contato.</summary>
    public static Contact Create(
        Guid accountId,
        string displayName,
        DateTimeOffset createdAt,
        string? externalId = null,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new Contact(id ?? Guid.CreateVersion7(), accountId, displayName.Trim(), createdAt)
        {
            ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim(),
        };
    }

    /// <summary>Atualiza os dados pessoais.</summary>
    public void Update(
        string displayName,
        string? givenName,
        string? familyName,
        string? organization,
        string? jobTitle,
        string? phoneNumber,
        string? notes,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        DisplayName = displayName.Trim();
        GivenName = Normalize(givenName);
        FamilyName = Normalize(familyName);
        Organization = Normalize(organization);
        JobTitle = Normalize(jobTitle);
        PhoneNumber = Normalize(phoneNumber);
        Notes = Normalize(notes);
        Touch(now);
    }

    /// <summary>
    /// Acrescenta um endereço ao contato.
    /// </summary>
    /// <remarks>
    /// Endereço repetido não é acrescentado de novo, e marcar um como principal remove a
    /// marca dos demais: dois principais tornariam indefinido qual entra no campo Para.
    /// </remarks>
    public ContactEmail AddEmail(EmailAddress address, DateTimeOffset now, bool isPrimary = false, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        var existing = _emails.FirstOrDefault(e => e.Address == address);

        if (existing is not null)
        {
            if (isPrimary)
            {
                SetPrimaryEmail(existing, now);
            }

            return existing;
        }

        var email = ContactEmail.Create(Id, address, label, now);
        _emails.Add(email);

        if (isPrimary || _emails.Count == 1)
        {
            SetPrimaryEmail(email, now);
        }

        Touch(now);
        return email;
    }

    /// <summary>Remove um endereço.</summary>
    public bool RemoveEmail(EmailAddress address, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(address);

        var email = _emails.FirstOrDefault(e => e.Address == address);

        if (email is null)
        {
            return false;
        }

        _emails.Remove(email);

        // O contato não pode ficar sem principal enquanto tiver endereços: o campo Para
        // precisa saber qual usar.
        if (email.IsPrimary && _emails.Count > 0)
        {
            SetPrimaryEmail(_emails[0], now);
        }

        Touch(now);
        return true;
    }

    /// <summary>Define qual endereço é o principal.</summary>
    public void SetPrimaryEmail(ContactEmail email, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);

        foreach (var candidate in _emails)
        {
            candidate.SetPrimary(candidate.Id == email.Id);
        }

        Touch(now);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Um endereço de e-mail de um contato.</summary>
public sealed class ContactEmail : Entity
{
    private ContactEmail(Guid id, Guid contactId, EmailAddress address, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ContactId = contactId;
        Address = address;
    }

    private ContactEmail()
    {
    }

    /// <summary>Contato dono do endereço.</summary>
    public Guid ContactId { get; private set; }

    /// <summary>Contato dono do endereço.</summary>
    public Contact? Contact { get; private set; }

    /// <summary>Endereço.</summary>
    public EmailAddress Address { get; private set; } = null!;

    /// <summary>Rótulo livre — "trabalho", "pessoal".</summary>
    public string? Label { get; private set; }

    /// <summary>Se é o endereço principal do contato.</summary>
    public bool IsPrimary { get; private set; }

    internal static ContactEmail Create(
        Guid contactId, EmailAddress address, string? label, DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), contactId, address, createdAt)
        {
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
        };

    internal void SetPrimary(bool isPrimary) => IsPrimary = isPrimary;
}
