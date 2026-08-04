using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Handlers;

namespace Sintek.Mail.App.ViewModels;

public partial class ComposeViewModel : ObservableObject
{
    private readonly SendMessageHandler _sendHandler;

    [ObservableProperty]
    private string _to = string.Empty;

    [ObservableProperty]
    private string _cc = string.Empty;

    [ObservableProperty]
    private string _bcc = string.Empty;

    [ObservableProperty]
    private string _subject = string.Empty;

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private bool _isHtml = true;

    public ComposeViewModel(SendMessageHandler sendHandler)
    {
        _sendHandler = sendHandler;
        SendCommand = new AsyncRelayCommand(SendAsync);
    }

    public IAsyncRelayCommand SendCommand { get; }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(To) || string.IsNullOrWhiteSpace(Subject))
            return;

        // TODO: Get account ID from context
        var toList = To.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ccList = string.IsNullOrWhiteSpace(Cc) ? null : Cc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var bccList = string.IsNullOrWhiteSpace(Bcc) ? null : Bcc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var command = new SendMessageCommand(
            AccountId: Guid.Empty,
            Subject: Subject,
            HtmlBody: IsHtml ? Body : null,
            TextBody: IsHtml ? null : Body,
            To: toList,
            Cc: ccList,
            Bcc: bccList
        );

        var messageId = await _sendHandler.HandleAsync(command);

        // Clear form
        To = Cc = Bcc = Subject = Body = string.Empty;
    }
}
