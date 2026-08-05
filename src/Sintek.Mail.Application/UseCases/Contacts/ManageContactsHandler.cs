using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Services;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Contacts;

/// <summary>Um endereço informado ao gravar um contato.</summary>
/// <param name="Address">Endereço.</param>
/// <param name="Label">Rótulo livre.</param>
/// <param name="IsPrimary">Se é o principal.</param>
public readonly record struct ContactEmailInput(EmailAddress Address, string? Label, bool IsPrimary);

/// <summary>Dados de um contato a criar ou atualizar.</summary>
public sealed record ContactCommand
{
    /// <summary>Conta dona do contato.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Contato existente, quando for edição.</summary>
    public Guid? ContactId { get; init; }

    /// <summary>Nome exibido.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Primeiro nome.</summary>
    public string? GivenName { get; init; }

    /// <summary>Sobrenome.</summary>
    public string? FamilyName { get; init; }

    /// <summary>Empresa.</summary>
    public string? Organization { get; init; }

    /// <summary>Cargo.</summary>
    public string? JobTitle { get; init; }

    /// <summary>Telefone.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Anotações.</summary>
    public string? Notes { get; init; }

    /// <summary>Endereços do contato.</summary>
    public IReadOnlyList<ContactEmailInput> Emails { get; init; } = [];
}

/// <summary>Resultado de uma gravação de contato.</summary>
/// <param name="Succeeded">Se concluiu.</param>
/// <param name="ContactId">Identificador do contato.</param>
/// <param name="ErrorMessage">Motivo exibível da recusa.</param>
public readonly record struct ContactResult(bool Succeeded, Guid? ContactId, string? ErrorMessage);

/// <summary>Resultado de uma importação de vCard.</summary>
/// <param name="Imported">Contatos criados.</param>
/// <param name="Updated">Contatos que já existiam e foram atualizados.</param>
/// <param name="Skipped">Cartões que o arquivo trazia e não puderam ser entendidos.</param>
public readonly record struct ContactImportResult(int Imported, int Updated, int Skipped)
{
    /// <summary>Quantos contatos o arquivo produziu, entre novos e atualizados.</summary>
    public int Total => Imported + Updated;
}

/// <summary>
/// Cria, edita, remove, importa e exporta contatos.
/// </summary>
/// <remarks>
/// O catálogo é por conta. Em um cliente organizado por Diretório de Domínio, uma lista
/// única de contatos desfaria a separação que é a razão de ser do resto do produto: quem
/// atende três clientes não quer os contatos de um aparecendo ao escrever para outro.
/// </remarks>
public sealed class ManageContactsHandler
{
    private readonly IContactRepository _contacts;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManageContactsHandler> _logger;

    public ManageContactsHandler(
        IContactRepository contacts,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ManageContactsHandler> logger)
    {
        _contacts = contacts;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Lista os contatos da conta.</summary>
    public Task<IReadOnlyList<Contact>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => _contacts.ListAsync(accountId, cancellationToken);

    /// <summary>Carrega um contato.</summary>
    public Task<Contact?> GetAsync(Guid contactId, CancellationToken cancellationToken = default)
        => _contacts.GetByIdAsync(contactId, cancellationToken);

    /// <summary>Cria ou atualiza um contato.</summary>
    public async Task<ContactResult> SaveAsync(
        ContactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            return new ContactResult(false, null, "Informe o nome do contato.");
        }

        var account = await _accounts.GetByIdAsync(command.AccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new ContactResult(false, null, "A conta informada não existe.");
        }

        var now = _timeProvider.GetUtcNow();

        var contact = command.ContactId is { } id
            ? await _contacts.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

        if (command.ContactId is not null && contact is null)
        {
            return new ContactResult(false, null, "O contato não existe mais.");
        }

        if (contact is null)
        {
            contact = Contact.Create(command.AccountId, command.DisplayName, now);
            await _contacts.AddAsync(contact, cancellationToken).ConfigureAwait(false);
        }

        contact.Update(
            command.DisplayName,
            command.GivenName,
            command.FamilyName,
            command.Organization,
            command.JobTitle,
            command.PhoneNumber,
            command.Notes,
            now);

        SyncEmails(contact, command.Emails, now);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ContactResult(true, contact.Id, null);
    }

    /// <summary>Remove um contato.</summary>
    public async Task<bool> RemoveAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await _contacts.GetByIdAsync(contactId, cancellationToken).ConfigureAwait(false);

        if (contact is null)
        {
            return false;
        }

        _contacts.Remove(contact);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Importa um arquivo vCard para a conta.
    /// </summary>
    /// <remarks>
    /// Contato cujo <c>UID</c> já existe na conta é atualizado, não duplicado — é o que
    /// permite reimportar a exportação do Outlook depois de uma alteração lá. Sem
    /// <c>UID</c>, a identidade cai para o endereço principal, que é o que outros clientes
    /// usam quando exportam sem identificador.
    /// </remarks>
    public async Task<ContactImportResult> ImportAsync(
        Guid accountId, string vCardContent, CancellationToken cancellationToken = default)
    {
        var parsed = VCardSerializer.Read(vCardContent);
        var imported = 0;
        var updated = 0;
        var now = _timeProvider.GetUtcNow();

        foreach (var card in parsed.Contacts)
        {
            var existing = await FindExistingAsync(accountId, card, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                existing = Contact.Create(accountId, card.DisplayName, now, card.Uid);
                await _contacts.AddAsync(existing, cancellationToken).ConfigureAwait(false);
                imported++;
            }
            else
            {
                updated++;
            }

            existing.Update(
                card.DisplayName,
                card.GivenName,
                card.FamilyName,
                card.Organization,
                card.JobTitle,
                card.PhoneNumber,
                card.Notes,
                now);

            // Os endereços do arquivo se somam aos que já existiam: a importação não é a
            // fonte da verdade, e apagar o que o usuário acrescentou à mão seria perda
            // silenciosa de dado que ele não pediu para trocar.
            for (var i = 0; i < card.Emails.Count; i++)
            {
                var email = card.Emails[i];
                existing.AddEmail(email.Address, now, email.IsPreferred || i == 0, email.Label);
            }
        }

        if (parsed.Contacts.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Importação de contatos da conta {AccountId}: {Imported} novos, {Updated} atualizados, {Skipped} ignorados.",
            accountId, imported, updated, parsed.SkippedCards);

        return new ContactImportResult(imported, updated, parsed.SkippedCards);
    }

    /// <summary>Exporta os contatos da conta como vCard 3.0.</summary>
    public async Task<string> ExportAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var contacts = await _contacts.ListAsync(accountId, cancellationToken).ConfigureAwait(false);

        return VCardSerializer.Write(contacts.Select(ToVCard).ToList());
    }

    private async Task<Contact?> FindExistingAsync(
        Guid accountId, VCardContact card, CancellationToken cancellationToken)
    {
        if (card.Uid is { } uid)
        {
            var byUid = await _contacts.GetByExternalIdAsync(accountId, uid, cancellationToken)
                .ConfigureAwait(false);

            if (byUid is not null)
            {
                return byUid;
            }
        }

        if (card.Emails.Count == 0)
        {
            return null;
        }

        return await _contacts
            .GetByEmailAsync(accountId, card.Emails[0].Address, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deixa os endereços do contato iguais aos informados.
    /// </summary>
    /// <remarks>
    /// A tela de edição mostra a lista completa, então o que sumiu de lá o usuário apagou
    /// de propósito. Distinto da importação, onde o arquivo é uma contribuição parcial.
    /// </remarks>
    private static void SyncEmails(
        Contact contact, IReadOnlyList<ContactEmailInput> emails, DateTimeOffset now)
    {
        var wanted = emails.Select(e => e.Address).ToList();

        foreach (var existing in contact.Emails.Select(e => e.Address).ToList())
        {
            if (!wanted.Contains(existing))
            {
                contact.RemoveEmail(existing, now);
            }
        }

        var hasExplicitPrimary = emails.Any(e => e.IsPrimary);

        for (var i = 0; i < emails.Count; i++)
        {
            var email = emails[i];

            // Sem escolha explícita, o primeiro da lista é o principal: um contato com
            // endereços e nenhum principal deixaria o campo Para sem saber qual usar.
            contact.AddEmail(
                email.Address, now, email.IsPrimary || (!hasExplicitPrimary && i == 0), email.Label);
        }
    }

    private static VCardContact ToVCard(Contact contact)
        => new(
            contact.ExternalId ?? contact.Id.ToString(),
            contact.DisplayName,
            contact.GivenName,
            contact.FamilyName,
            contact.Organization,
            contact.JobTitle,
            contact.PhoneNumber,
            contact.Notes,
            contact.Emails
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => new VCardEmail(e.Address, e.Label, e.IsPrimary))
                .ToList());
}
