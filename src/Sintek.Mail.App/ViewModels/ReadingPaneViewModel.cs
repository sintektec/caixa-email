using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.App.ViewModels;

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

    public ReadingPaneViewModel(IMessageRepository messages, IHtmlSanitizer sanitizer)
    {
        _messages = messages;
        _sanitizer = sanitizer;
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

        try
        {
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

            // Sempre parte do original: reaproveitar o HTML já higienizado impediria que
            // uma atualização das regras de sanitização valesse para mensagens antigas.
            RemoteContentAllowed = message.Body?.RemoteContentAllowed ?? false;
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
        }
        finally
        {
            IsLoading = false;
        }
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
}
