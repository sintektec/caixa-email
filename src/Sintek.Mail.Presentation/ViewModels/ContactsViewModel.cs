using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.UseCases.Contacts;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Um contato na lista.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="DisplayName">Nome exibido.</param>
/// <param name="PrimaryAddress">Endereço principal, vazio se o contato não tiver nenhum.</param>
/// <param name="Organization">Empresa, vazia se não informada.</param>
public sealed record ContactListItem(
    Guid Id, string DisplayName, string PrimaryAddress, string Organization);

/// <summary>Uma entrada do histórico de destinatários.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="DisplayText">Nome e endereço.</param>
/// <param name="UseCount">Quantas vezes foi usada.</param>
/// <param name="LastUsedAt">Último uso.</param>
public sealed record RecipientHistoryItem(
    Guid Id, string DisplayText, int UseCount, DateTimeOffset LastUsedAt)
{
    /// <summary>
    /// Resumo exibido embaixo do endereço.
    /// </summary>
    /// <remarks>
    /// Data em formato explícito e <see cref="CultureInfo.InvariantCulture"/>: com
    /// <c>InvariantGlobalization</c> ligado, pedir a cultura pt-BR lança em tempo de
    /// execução.
    /// </remarks>
    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "{0} envio(s) — último em {1:dd/MM/yyyy}",
        UseCount,
        LastUsedAt.ToLocalTime());
}

/// <summary>
/// Catálogo de contatos e histórico de destinatários de uma conta.
/// </summary>
/// <remarks>
/// <para>
/// As duas listas convivem na mesma tela de propósito: o usuário que quer "apagar aquele
/// endereço errado que sempre aparece" não sabe se ele veio do catálogo ou do histórico —
/// sabe apenas que ele aparece ao digitar. Separá-las em telas distintas obrigaria a
/// procurar nas duas.
/// </para>
/// <para>
/// Importar e exportar recebem e devolvem <b>texto</b>, não caminho de arquivo: a escolha
/// do arquivo é do WinUI, e mantê-la fora daqui é o que permite testar a importação no job
/// Linux.
/// </para>
/// </remarks>
public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly ManageContactsHandler _contacts;
    private readonly RecipientHistoryHandler _history;

    public ContactsViewModel(ManageContactsHandler contacts, RecipientHistoryHandler history)
    {
        _contacts = contacts;
        _history = history;
    }

    /// <summary>Conta cujo catálogo está aberto.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Contato selecionado na lista.</summary>
    [ObservableProperty]
    private ContactListItem? _selectedContact;

    /// <summary>Contato em edição, quando já existe.</summary>
    [ObservableProperty]
    private Guid? _editingContactId;

    /// <summary>Nome exibido em edição.</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Primeiro nome em edição.</summary>
    [ObservableProperty]
    private string _givenName = string.Empty;

    /// <summary>Sobrenome em edição.</summary>
    [ObservableProperty]
    private string _familyName = string.Empty;

    /// <summary>Empresa em edição.</summary>
    [ObservableProperty]
    private string _organization = string.Empty;

    /// <summary>Cargo em edição.</summary>
    [ObservableProperty]
    private string _jobTitle = string.Empty;

    /// <summary>Telefone em edição.</summary>
    [ObservableProperty]
    private string _phoneNumber = string.Empty;

    /// <summary>Anotações em edição.</summary>
    [ObservableProperty]
    private string _notes = string.Empty;

    /// <summary>Endereços em edição, separados por ponto e vírgula. O primeiro é o principal.</summary>
    [ObservableProperty]
    private string _emails = string.Empty;

    /// <summary>Mensagem de erro ou confirmação.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Contatos da conta.</summary>
    public ObservableCollection<ContactListItem> Contacts { get; } = [];

    /// <summary>Histórico de destinatários da conta.</summary>
    public ObservableCollection<RecipientHistoryItem> History { get; } = [];

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>Carrega catálogo e histórico da conta.</summary>
    public async Task LoadAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        AccountId = accountId;

        await RefreshContactsAsync(cancellationToken).ConfigureAwait(true);
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Limpa o formulário para um contato novo.</summary>
    [RelayCommand]
    public void StartNewContact()
    {
        EditingContactId = null;
        SelectedContact = null;
        DisplayName = string.Empty;
        GivenName = string.Empty;
        FamilyName = string.Empty;
        Organization = string.Empty;
        JobTitle = string.Empty;
        PhoneNumber = string.Empty;
        Notes = string.Empty;
        Emails = string.Empty;
        StatusMessage = string.Empty;
    }

    /// <summary>Carrega o contato selecionado no formulário.</summary>
    public async Task EditSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedContact is not { } selected)
        {
            return;
        }

        var contact = await _contacts.GetAsync(selected.Id, cancellationToken).ConfigureAwait(true);

        if (contact is null)
        {
            StatusMessage = "O contato não existe mais.";
            await RefreshContactsAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        EditingContactId = contact.Id;
        DisplayName = contact.DisplayName;
        GivenName = contact.GivenName ?? string.Empty;
        FamilyName = contact.FamilyName ?? string.Empty;
        Organization = contact.Organization ?? string.Empty;
        JobTitle = contact.JobTitle ?? string.Empty;
        PhoneNumber = contact.PhoneNumber ?? string.Empty;
        Notes = contact.Notes ?? string.Empty;

        // O principal em primeiro lugar: é essa ordem que a gravação relê para decidir
        // qual endereço continua sendo o principal.
        Emails = string.Join("; ", contact.Emails
            .OrderByDescending(e => e.IsPrimary)
            .Select(e => e.Address.Value));

        StatusMessage = string.Empty;
    }

    /// <summary>Grava o contato do formulário.</summary>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId || IsBusy)
        {
            return;
        }

        if (DisplayName.Trim().Length == 0)
        {
            StatusMessage = "Informe o nome do contato.";
            return;
        }

        var parsed = new List<ContactEmailInput>();

        foreach (var token in SplitEmails(Emails))
        {
            if (!EmailAddress.TryParse(token, out var address))
            {
                StatusMessage = $"O endereço '{token}' não é válido.";
                return;
            }

            // O primeiro da lista é o principal — a mesma ordem que a edição apresenta.
            parsed.Add(new ContactEmailInput(address, null, parsed.Count == 0));
        }

        IsBusy = true;

        try
        {
            var result = await _contacts.SaveAsync(
                new ContactCommand
                {
                    AccountId = accountId,
                    ContactId = EditingContactId,
                    DisplayName = DisplayName,
                    GivenName = NullIfBlank(GivenName),
                    FamilyName = NullIfBlank(FamilyName),
                    Organization = NullIfBlank(Organization),
                    JobTitle = NullIfBlank(JobTitle),
                    PhoneNumber = NullIfBlank(PhoneNumber),
                    Notes = NullIfBlank(Notes),
                    Emails = parsed,
                },
                cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage ?? string.Empty;
                return;
            }

            EditingContactId = result.ContactId;
            StatusMessage = "Contato gravado.";

            await RefreshContactsAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Remove o contato selecionado.</summary>
    [RelayCommand]
    public async Task RemoveSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedContact is not { } selected || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (!await _contacts.RemoveAsync(selected.Id, cancellationToken).ConfigureAwait(true))
            {
                StatusMessage = "O contato não existe mais.";
            }

            if (EditingContactId == selected.Id)
            {
                StartNewContact();
            }

            await RefreshContactsAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Importa um arquivo vCard já lido.</summary>
    public async Task ImportAsync(string vCardContent, CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _contacts.ImportAsync(accountId, vCardContent, cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = result.Total == 0 && result.Skipped == 0
                ? "O arquivo não trouxe nenhum contato."
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} contato(s) novo(s), {1} atualizado(s), {2} ignorado(s).",
                    result.Imported, result.Updated, result.Skipped);

            await RefreshContactsAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Devolve o catálogo da conta como texto vCard, para a janela gravar em arquivo.</summary>
    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
        => AccountId is { } accountId
            ? await _contacts.ExportAsync(accountId, cancellationToken).ConfigureAwait(true)
            : string.Empty;

    /// <summary>Apaga uma entrada do histórico.</summary>
    public async Task RemoveHistoryEntryAsync(
        Guid entryId, CancellationToken cancellationToken = default)
    {
        if (await _history.RemoveAsync(entryId, cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = "Entrada removida do histórico.";
        }

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Apaga todo o histórico da conta.</summary>
    [RelayCommand]
    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId)
        {
            return;
        }

        var removed = await _history.ClearAsync(accountId, cancellationToken).ConfigureAwait(true);

        StatusMessage = string.Format(
            CultureInfo.InvariantCulture, "{0} entrada(s) apagada(s) do histórico.", removed);

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshContactsAsync(CancellationToken cancellationToken)
    {
        Contacts.Clear();

        if (AccountId is not { } accountId)
        {
            return;
        }

        foreach (var contact in await _contacts.ListAsync(accountId, cancellationToken).ConfigureAwait(true))
        {
            Contacts.Add(new ContactListItem(
                contact.Id,
                contact.DisplayName,
                contact.PrimaryEmail?.Address.Value ?? string.Empty,
                contact.Organization ?? string.Empty));
        }
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        History.Clear();

        if (AccountId is not { } accountId)
        {
            return;
        }

        foreach (var entry in await _history.ListAsync(accountId, cancellationToken).ConfigureAwait(true))
        {
            History.Add(new RecipientHistoryItem(
                entry.Id, entry.SuggestionText, entry.UseCount, entry.LastUsedAt));
        }
    }

    private static IEnumerable<string> SplitEmails(string raw)
        => raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
}
