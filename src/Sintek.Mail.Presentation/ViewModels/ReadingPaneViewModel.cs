using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Um anexo exibido no painel de leitura.</summary>
public sealed record AttachmentViewModel
{
    public required Guid AttachmentId { get; init; }

    public required string FileName { get; init; }

    public required long Size { get; init; }

    /// <summary>
    /// Se a extensão do arquivo é executável ou interpretável no Windows.
    /// </summary>
    /// <remarks>
    /// A interface exibe um aviso claro antes de abrir. É alerta, não bloqueio: a decisão
    /// é do usuário, mas precisa ser informada.
    /// </remarks>
    public required bool IsSuspicious { get; init; }

    public bool IsDownloaded { get; init; }

    /// <summary>Tamanho legível, para exibição.</summary>
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024):0.#} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

/// <summary>ViewModel do painel de leitura.</summary>
public sealed partial class ReadingPaneViewModel : ScopedViewModel
{
    public ReadingPaneViewModel(IServiceScopeFactory scopes)
        : base(scopes)
    {
    }

    /// <summary>Conta e remetente da mensagem aberta, para o botão de adicionar aos contatos.</summary>
    /// <remarks>
    /// São dados, não entidade: <see cref="EmailAddress"/> é objeto de valor imutável, gravado
    /// por conversor. Guardá-lo entre escopos não prende nada ao <c>DbContext</c> que o leu.
    /// </remarks>
    private Guid? _accountId;
    private EmailAddress? _senderAddress;
    private string? _senderDisplayName;

    /// <summary>Resultado da última tentativa de adicionar o remetente aos contatos.</summary>
    [ObservableProperty]
    private string _contactMessage = string.Empty;

    /// <summary>Se há remetente a acrescentar ao catálogo.</summary>
    public bool CanAddSenderToContacts => _accountId is not null && _senderAddress is not null;

    /// <summary>
    /// Acrescenta o remetente da mensagem aberta ao catálogo de contatos.
    /// </summary>
    /// <remarks>
    /// Remetente que já está no catálogo não é duplicado nem sobrescrito: o contato pode ter
    /// sido editado à mão desde então, e trocar o nome curado pelo que veio no cabeçalho de
    /// uma mensagem seria perder a edição sem avisar.
    /// </remarks>
    [RelayCommand]
    public async Task AddSenderToContactsAsync(CancellationToken cancellationToken = default)
    {
        if (_accountId is not { } accountId || _senderAddress is not { } address)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(_senderDisplayName)
            ? address.Value
            : _senderDisplayName;

        // Busca e gravação no mesmo escopo. O contato encontrado é entidade rastreada e não
        // sai daqui: o que atravessa é a mensagem já formada.
        ContactMessage = await InScopeAsync(
            async sp =>
            {
                var contacts = sp.GetRequiredService<Application.UseCases.Contacts.ManageContactsHandler>();

                var existing = await contacts.FindByEmailAsync(accountId, address, cancellationToken)
                    .ConfigureAwait(true);

                if (existing is not null)
                {
                    return $"{existing.DisplayName} já está nos contatos.";
                }

                var result = await contacts.SaveAsync(
                    new Application.UseCases.Contacts.ContactCommand
                    {
                        AccountId = accountId,
                        DisplayName = displayName,
                        Emails =
                        [
                            new Application.UseCases.Contacts.ContactEmailInput(address, null, true),
                        ],
                    },
                    cancellationToken).ConfigureAwait(true);

                return result.Succeeded
                    ? "Remetente adicionado aos contatos."
                    : result.ErrorMessage ?? string.Empty;
            },
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Se o remetente pediu confirmação de leitura e o usuário ainda não decidiu.
    /// </summary>
    /// <remarks>
    /// A confirmação nunca sai sozinha: o cabeçalho é um pedido, e enviar sem perguntar
    /// entregaria ao remetente a informação de que a mensagem foi aberta — que é
    /// exatamente o que um remetente hostil quer confirmar.
    /// </remarks>
    [ObservableProperty]
    private bool _showReadReceiptPrompt;

    /// <summary>Envia a confirmação de leitura pedida pelo remetente.</summary>
    [RelayCommand]
    public async Task SendReadReceiptAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is not { } messageId)
        {
            return;
        }

        var result = await InScopeAsync(
            sp => sp.GetRequiredService<ReadReceiptHandler>().SendAsync(messageId, cancellationToken),
            cancellationToken).ConfigureAwait(true);

        ShowReadReceiptPrompt = false;
        DownloadError = result.Succeeded ? string.Empty : result.ErrorMessage ?? string.Empty;
    }

    /// <summary>Recusa enviar a confirmação — decisão registrada, não adiada.</summary>
    [RelayCommand]
    public async Task DeclineReadReceiptAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is { } messageId)
        {
            await InScopeAsync(
                sp => sp.GetRequiredService<ReadReceiptHandler>().DeclineAsync(messageId, cancellationToken),
                cancellationToken).ConfigureAwait(true);
        }

        ShowReadReceiptPrompt = false;
    }

    [ObservableProperty]
    private Guid? _messageId;

    [ObservableProperty]
    private string _subject = string.Empty;

    [ObservableProperty]
    private string _from = string.Empty;

    [ObservableProperty]
    private string _to = string.Empty;

    [ObservableProperty]
    private string _cc = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _sentAt;

    /// <summary>
    /// HTML já higienizado — o <b>único</b> conteúdo que pode ser entregue ao WebView2.
    /// </summary>
    [ObservableProperty]
    private string _sanitizedHtml = string.Empty;

    /// <summary>Se a mensagem referencia imagens ou recursos externos.</summary>
    [ObservableProperty]
    private bool _hasRemoteContent;

    /// <summary>Se o usuário já autorizou o carregamento do conteúdo remoto.</summary>
    [ObservableProperty]
    private bool _remoteContentAllowed;

    /// <summary>Se a barra "Exibir imagens" deve aparecer.</summary>
    public bool ShowRemoteContentBar => HasRemoteContent && !RemoteContentAllowed;

    /// <summary>Veredito de procedência da mensagem.</summary>
    [ObservableProperty]
    private SenderTrustLevel _trustLevel = SenderTrustLevel.Neutral;

    /// <summary>Explicação do veredito, para a faixa e para leitores de tela.</summary>
    [ObservableProperty]
    private string _trustMessage = string.Empty;

    /// <summary>Se a faixa de alerta de procedência deve aparecer.</summary>
    /// <remarks>
    /// Só os vereditos negativos ganham faixa. O selo de "verificada" fica discreto no
    /// cabeçalho: alerta que aparece em toda mensagem legítima deixa de ser lido, e o espaço
    /// de atenção do usuário é o recurso mais escasso da tela.
    /// </remarks>
    public bool ShowTrustWarning => TrustLevel is SenderTrustLevel.DisplayNameSpoofing
        or SenderTrustLevel.AuthenticationFailed
        or SenderTrustLevel.FlaggedAsSpam;

    /// <summary>Se o selo de origem verificada deve aparecer.</summary>
    public bool ShowAuthenticatedBadge => TrustLevel == SenderTrustLevel.Authenticated;

    /// <summary>Mensagem exibida quando o corpo não pôde ser baixado.</summary>
    [ObservableProperty]
    private string _downloadError = string.Empty;

    /// <summary>Se há erro de download a exibir.</summary>
    public bool HasDownloadError => DownloadError.Length > 0;

    /// <summary>Anexos.</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    /// <summary>Se algum anexo tem extensão perigosa.</summary>
    public bool HasSuspiciousAttachment => Attachments.Any(a => a.IsSuspicious);

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Carrega uma mensagem no painel.</summary>
    [RelayCommand]
    public async Task LoadMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        DownloadError = string.Empty;

        try
        {
            // Um escopo para a operação inteira. O download grava o corpo na instância que o
            // DbContext do escopo rastreia, e é essa mesma instância que a releitura devolve;
            // em escopos separados a leitura viria de outro contexto e o corpo chegaria nulo.
            await InScopeAsync(
                async sp =>
                {
                    var messages = sp.GetRequiredService<IMessageRepository>();
                    var sanitizer = sp.GetRequiredService<IHtmlSanitizer>();

                    // O download é idempotente: corpo já presente devolve sem tocar na rede, e é
                    // isso que torna seguro chamá-lo em todo clique de mensagem.
                    var download = await sp.GetRequiredService<DownloadMessageContentHandler>()
                        .DownloadBodyAsync(messageId, cancellationToken)
                        .ConfigureAwait(true);

                    if (!download.Succeeded)
                    {
                        DownloadError = download.ErrorMessage ?? string.Empty;
                    }

                    var message = await messages.GetWithParticipantsAsync(messageId, cancellationToken)
                        .ConfigureAwait(true);

                    if (message is null)
                    {
                        Clear();
                        return;
                    }

                    MessageId = message.Id;
                    Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(sem assunto)" : message.Subject;
                    From = FormatSender(message);

                    _accountId = message.AccountId;
                    _senderAddress = message.FromAddress;
                    _senderDisplayName = message.FromDisplayName;
                    ContactMessage = string.Empty;
                    OnPropertyChanged(nameof(CanAddSenderToContacts));
                    To = FormatAddresses(message, AddressKind.To);
                    Cc = FormatAddresses(message, AddressKind.Cc);
                    SentAt = message.SentAt;

                    // A pergunta só aparece uma vez por mensagem: repeti-la depois de um "não"
                    // trataria a recusa como se não tivesse valido.
                    ShowReadReceiptPrompt = message.ReadReceiptRequested && !message.ReadReceiptHandled;

                    // Sempre parte do original: reaproveitar o HTML já higienizado impediria que
                    // uma atualização das regras de sanitização valesse para mensagens antigas.
                    RemoteContentAllowed = message.Body?.RemoteContentAllowed ?? false;

                    // Remetente confiável libera o conteúdo remoto sem perguntar — é para isso que
                    // a lista existe. A autorização vale só para a exibição; nada é gravado.
                    if (!RemoteContentAllowed && message.FromAddress is not null)
                    {
                        RemoteContentAllowed = await sp
                            .GetRequiredService<Application.UseCases.Organization.ManageSenderReputationHandler>()
                            .IsTrustedAsync(message.FromAddress, message.AccountId, cancellationToken)
                            .ConfigureAwait(true);
                    }

                    ApplySanitizedBody(sanitizer, message.Body?.HtmlBody, message.Body?.TextBody);

                    Attachments.Clear();
                    foreach (var attachment in message.Attachments.Where(a => !a.IsInline))
                    {
                        Attachments.Add(new AttachmentViewModel
                        {
                            AttachmentId = attachment.Id,
                            FileName = attachment.FileName,
                            Size = attachment.Size,
                            IsSuspicious = attachment.IsSuspicious,
                            IsDownloaded = attachment.IsDownloaded,
                        });
                    }

                    OnPropertyChanged(nameof(HasSuspiciousAttachment));

                    await EvaluateTrustAsync(messages, message, cancellationToken).ConfigureAwait(true);
                },
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Baixa o conteúdo de um anexo e devolve o caminho no disco.</summary>
    public async Task<string?> DownloadAttachmentAsync(
        Guid attachmentId, CancellationToken cancellationToken = default)
    {
        if (MessageId is not { } messageId)
        {
            return null;
        }

        // Mesmo motivo do carregamento: quem grava o anexo e quem relê o caminho precisam ver o
        // mesmo DbContext. O que sai daqui é o caminho em disco, nunca a entidade.
        return await InScopeAsync<string?>(
            async sp =>
            {
                var result = await sp.GetRequiredService<DownloadMessageContentHandler>()
                    .DownloadAttachmentAsync(messageId, attachmentId, cancellationToken)
                    .ConfigureAwait(true);

                if (!result.Succeeded)
                {
                    DownloadError = result.ErrorMessage ?? string.Empty;
                    return null;
                }

                var message = await sp.GetRequiredService<IMessageRepository>()
                    .GetWithParticipantsAsync(messageId, cancellationToken)
                    .ConfigureAwait(true);

                return message?.Attachments.FirstOrDefault(a => a.Id == attachmentId)?.StoragePath;
            },
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Avalia a procedência da mensagem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A regra vive em <see cref="SenderTrustEvaluator"/>, no domínio; aqui só se buscam os
    /// correspondentes conhecidos e se apresenta o veredito. A lista vem das mensagens que o
    /// usuário leu — é a leitura que indica que ele reconhece aquele remetente.
    /// </para>
    /// <para>
    /// O repositório vem por parâmetro porque <paramref name="message"/> é entidade rastreada:
    /// quem a leu é o escopo de quem chama, e ela não pode sair dele.
    /// </para>
    /// </remarks>
    private async Task EvaluateTrustAsync(
        IMessageRepository messages, Message message, CancellationToken cancellationToken)
    {
        var correspondents = await messages
            .ListKnownCorrespondentsAsync(message.AccountId, cancellationToken)
            .ConfigureAwait(true);

        var verdict = SenderTrustEvaluator.Evaluate(message, correspondents);

        TrustLevel = verdict.Level;
        TrustMessage = verdict.Level == SenderTrustLevel.Authenticated
            ? "Origem verificada pelo servidor."
            : verdict.Reason;
    }

    /// <summary>Autoriza o carregamento do conteúdo remoto desta mensagem.</summary>
    [RelayCommand]
    public async Task AllowRemoteContentAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is not { } messageId)
        {
            return;
        }

        await InScopeAsync(
            async sp =>
            {
                var message = await sp.GetRequiredService<IMessageRepository>()
                    .GetWithParticipantsAsync(messageId, cancellationToken)
                    .ConfigureAwait(true);

                if (message?.Body is null)
                {
                    return;
                }

                RemoteContentAllowed = true;
                ApplySanitizedBody(
                    sp.GetRequiredService<IHtmlSanitizer>(),
                    message.Body.HtmlBody,
                    message.Body.TextBody);
            },
            cancellationToken).ConfigureAwait(true);
    }

    private void ApplySanitizedBody(IHtmlSanitizer sanitizer, string? htmlBody, string? textBody)
    {
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            var result = sanitizer.Sanitize(htmlBody, RemoteContentAllowed);
            SanitizedHtml = result.SanitizedHtml;
            HasRemoteContent = result.HasRemoteContent;
        }
        else
        {
            SanitizedHtml = sanitizer.PlainTextToHtml(textBody);
            HasRemoteContent = false;
        }

        OnPropertyChanged(nameof(ShowRemoteContentBar));
    }

    private void Clear()
    {
        TrustLevel = SenderTrustLevel.Neutral;
        TrustMessage = string.Empty;
        DownloadError = string.Empty;
        ShowReadReceiptPrompt = false;
        MessageId = null;
        Subject = string.Empty;
        From = string.Empty;
        _accountId = null;
        _senderAddress = null;
        _senderDisplayName = null;
        ContactMessage = string.Empty;
        OnPropertyChanged(nameof(CanAddSenderToContacts));
        To = string.Empty;
        Cc = string.Empty;
        SentAt = null;
        SanitizedHtml = string.Empty;
        HasRemoteContent = false;
        Attachments.Clear();
    }

    private static string FormatSender(Message message)
        => string.IsNullOrWhiteSpace(message.FromDisplayName)
            ? message.FromAddress?.Value ?? "(sem remetente)"
            : $"{message.FromDisplayName} <{message.FromAddress?.Value}>";

    private static string FormatAddresses(Message message, AddressKind kind)
        => string.Join("; ", message.Addresses
            .Where(a => a.Kind == kind)
            .Select(a => string.IsNullOrWhiteSpace(a.DisplayName)
                ? a.Address.Value
                : $"{a.DisplayName} <{a.Address.Value}>"));

    partial void OnHasRemoteContentChanged(bool value) => OnPropertyChanged(nameof(ShowRemoteContentBar));

    partial void OnRemoteContentAllowedChanged(bool value) => OnPropertyChanged(nameof(ShowRemoteContentBar));

    partial void OnTrustLevelChanged(SenderTrustLevel value)
    {
        OnPropertyChanged(nameof(ShowTrustWarning));
        OnPropertyChanged(nameof(ShowAuthenticatedBadge));
    }

    partial void OnDownloadErrorChanged(string value) => OnPropertyChanged(nameof(HasDownloadError));
}
