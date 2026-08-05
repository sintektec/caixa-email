using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;

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
public sealed partial class ReadingPaneViewModel : ObservableObject
{
    private readonly IMessageRepository _messages;
    private readonly IHtmlSanitizer _sanitizer;
    private readonly DownloadMessageContentHandler _download;
    private readonly Application.UseCases.Organization.ManageSenderReputationHandler _reputation;
    private readonly ReadReceiptHandler _readReceipt;

    public ReadingPaneViewModel(
        IMessageRepository messages,
        IHtmlSanitizer sanitizer,
        DownloadMessageContentHandler download,
        Application.UseCases.Organization.ManageSenderReputationHandler reputation,
        ReadReceiptHandler readReceipt)
    {
        _messages = messages;
        _sanitizer = sanitizer;
        _download = download;
        _reputation = reputation;
        _readReceipt = readReceipt;
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

        var result = await _readReceipt.SendAsync(messageId, cancellationToken).ConfigureAwait(true);

        ShowReadReceiptPrompt = false;
        DownloadError = result.Succeeded ? string.Empty : result.ErrorMessage ?? string.Empty;
    }

    /// <summary>Recusa enviar a confirmação — decisão registrada, não adiada.</summary>
    [RelayCommand]
    public async Task DeclineReadReceiptAsync(CancellationToken cancellationToken = default)
    {
        if (MessageId is { } messageId)
        {
            await _readReceipt.DeclineAsync(messageId, cancellationToken).ConfigureAwait(true);
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
            // O download é idempotente: corpo já presente devolve sem tocar na rede, e é
            // isso que torna seguro chamá-lo em todo clique de mensagem.
            var download = await _download.DownloadBodyAsync(messageId, cancellationToken)
                .ConfigureAwait(true);

            if (!download.Succeeded)
            {
                DownloadError = download.ErrorMessage ?? string.Empty;
            }

            var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
                .ConfigureAwait(true);

            if (message is null)
            {
                Clear();
                return;
            }

            MessageId = message.Id;
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(sem assunto)" : message.Subject;
            From = FormatSender(message);
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
                RemoteContentAllowed = await _reputation
                    .IsTrustedAsync(message.FromAddress, message.AccountId, cancellationToken)
                    .ConfigureAwait(true);
            }

            ApplySanitizedBody(message.Body?.HtmlBody, message.Body?.TextBody);

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

            await EvaluateTrustAsync(message, cancellationToken).ConfigureAwait(true);
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

        var result = await _download.DownloadAttachmentAsync(messageId, attachmentId, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            DownloadError = result.ErrorMessage ?? string.Empty;
            return null;
        }

        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(true);

        return message?.Attachments.FirstOrDefault(a => a.Id == attachmentId)?.StoragePath;
    }

    /// <summary>
    /// Avalia a procedência da mensagem.
    /// </summary>
    /// <remarks>
    /// A regra vive em <see cref="SenderTrustEvaluator"/>, no domínio; aqui só se buscam os
    /// correspondentes conhecidos e se apresenta o veredito. A lista vem das mensagens que o
    /// usuário leu — é a leitura que indica que ele reconhece aquele remetente.
    /// </remarks>
    private async Task EvaluateTrustAsync(Message message, CancellationToken cancellationToken)
    {
        var correspondents = await _messages
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
        if (MessageId is null)
        {
            return;
        }

        var message = await _messages.GetWithParticipantsAsync(MessageId.Value, cancellationToken)
            .ConfigureAwait(true);

        if (message?.Body is null)
        {
            return;
        }

        RemoteContentAllowed = true;
        ApplySanitizedBody(message.Body.HtmlBody, message.Body.TextBody);
    }

    private void ApplySanitizedBody(string? htmlBody, string? textBody)
    {
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            var result = _sanitizer.Sanitize(htmlBody, RemoteContentAllowed);
            SanitizedHtml = result.SanitizedHtml;
            HasRemoteContent = result.HasRemoteContent;
        }
        else
        {
            SanitizedHtml = _sanitizer.PlainTextToHtml(textBody);
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
