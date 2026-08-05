using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Um anexo listado no compositor.</summary>
/// <param name="FileName">Nome exibido.</param>
/// <param name="FilePath">Caminho no disco.</param>
/// <param name="ContentType">Tipo MIME.</param>
/// <param name="Size">Tamanho em bytes.</param>
public sealed record ComposerAttachmentItem(
    string FileName, string FilePath, string ContentType, long Size);

/// <summary>
/// Compositor de mensagens: nova, resposta, resposta a todos e encaminhamento.
/// </summary>
/// <remarks>
/// <para>
/// Enviar aqui é entregar à fila: a mensagem vai para a Caixa de Saída local e o SMTP
/// acontece quando a fila drenar. O botão funciona num avião, e a fila visível mostra o que
/// ainda não saiu.
/// </para>
/// <para>
/// O rascunho automático grava a cada intervalo de digitação parada — perder texto por
/// queda de energia é o tipo de acidente que um cliente de e-mail não tem o direito de
/// deixar acontecer.
/// </para>
/// </remarks>
public sealed partial class ComposerViewModel : ObservableObject
{
    /// <summary>
    /// Quanto tempo de digitação parada dispara a gravação automática do rascunho.
    /// </summary>
    /// <remarks>
    /// A janela chama <see cref="AutoSaveTickAsync"/> em intervalos curtos; é este período
    /// de silêncio que decide gravar. Gravar a cada tecla transformaria a fila de
    /// rascunhos em ruído; esperar demais é perder texto na queda de energia.
    /// </remarks>
    public static readonly TimeSpan AutoSaveQuietPeriod = TimeSpan.FromSeconds(5);

    private readonly IMessageRepository _messages;
    private readonly IAccountRepository _accounts;
    private readonly ComposeMessageHandler _compose;
    private readonly Application.UseCases.Organization.ManageTemplatesHandler _templates;
    private readonly TimeProvider _timeProvider;

    private DateTimeOffset? _lastEditAt;
    private bool _hasUnsavedChanges;
    private bool _isInitializing;

    public ComposerViewModel(
        IMessageRepository messages,
        IAccountRepository accounts,
        ComposeMessageHandler compose,
        Application.UseCases.Organization.ManageTemplatesHandler templates,
        TimeProvider timeProvider)
    {
        _messages = messages;
        _accounts = accounts;
        _compose = compose;
        _templates = templates;
        _timeProvider = timeProvider;
    }

    /// <summary>Conta que escreve.</summary>
    [ObservableProperty]
    private Guid? _accountId;

    /// <summary>Rascunho em edição, quando já gravado.</summary>
    [ObservableProperty]
    private Guid? _draftId;

    /// <summary>Destinatários diretos, separados por ponto e vírgula.</summary>
    [ObservableProperty]
    private string _to = string.Empty;

    /// <summary>Cópia.</summary>
    [ObservableProperty]
    private string _cc = string.Empty;

    /// <summary>Cópia oculta.</summary>
    [ObservableProperty]
    private string _bcc = string.Empty;

    /// <summary>Se os campos CC e CCO estão visíveis.</summary>
    [ObservableProperty]
    private bool _showCcBcc;

    /// <summary>Assunto.</summary>
    [ObservableProperty]
    private string _subject = string.Empty;

    /// <summary>Corpo em edição, como texto.</summary>
    [ObservableProperty]
    private string _bodyText = string.Empty;

    /// <summary>Corpo em HTML, quando o editor rico estiver em uso.</summary>
    [ObservableProperty]
    private string _bodyHtml = string.Empty;

    /// <summary>Prioridade declarada.</summary>
    [ObservableProperty]
    private MessageImportance _importance = MessageImportance.Normal;

    /// <summary>Se pede confirmação de leitura.</summary>
    [ObservableProperty]
    private bool _requestReadReceipt;

    /// <summary>Mensagem de erro ou aviso.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Se há operação em andamento.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Se a mensagem foi entregue à fila e o compositor pode fechar.</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>
    /// Se o aviso de anexo esquecido está aguardando decisão.
    /// </summary>
    /// <remarks>
    /// O aviso segura o envio uma única vez: confirmado, <see cref="SendCommand"/> segue
    /// mesmo sem anexo. Bloquear de vez transformaria um alerta útil em obstáculo.
    /// </remarks>
    [ObservableProperty]
    private bool _showForgottenAttachmentWarning;

    /// <summary>Metadados de conversa herdados da mensagem de origem.</summary>
    private string? _inReplyTo;
    private IReadOnlyList<string> _references = [];
    private Guid? _threadId;

    /// <summary>Anexos escolhidos.</summary>
    public ObservableCollection<ComposerAttachmentItem> Attachments { get; } = [];

    /// <summary>Modelos de mensagem disponíveis.</summary>
    public ObservableCollection<ScopeFilterOption> TemplateOptions { get; } = [];

    /// <summary>Se há mensagem a exibir na faixa de aviso.</summary>
    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>
    /// Prepara o compositor a partir de uma mensagem existente, ou vazio para mensagem nova.
    /// </summary>
    public async Task InitializeAsync(
        Guid accountId,
        DraftKind kind = DraftKind.New,
        Guid? sourceMessageId = null,
        CancellationToken cancellationToken = default)
    {
        AccountId = accountId;

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(true);

        if (account is null)
        {
            StatusMessage = "A conta informada não existe.";
            return;
        }

        var source = sourceMessageId is { } id
            ? await _messages.GetWithParticipantsAsync(id, cancellationToken).ConfigureAwait(true)
            : null;

        var draft = DraftComposer.Compose(kind, source, source?.Body, account.EmailAddress, account.Signature);

        // O preenchimento inicial não é digitação: sem esta guarda, abrir uma resposta e
        // fechá-la sem tocar em nada deixaria um rascunho para trás.
        _isInitializing = true;

        try
        {
            Subject = draft.Subject;
            BodyText = draft.TextBody ?? string.Empty;
            BodyHtml = draft.HtmlBody ?? string.Empty;
            To = JoinAddresses(draft.Recipients, AddressKind.To);
            Cc = JoinAddresses(draft.Recipients, AddressKind.Cc);
            ShowCcBcc = Cc.Length > 0;
        }
        finally
        {
            _isInitializing = false;
        }

        _inReplyTo = draft.InReplyTo;
        _references = draft.References;
        _threadId = draft.ThreadId;

        TemplateOptions.Clear();
        foreach (var template in await _templates.ListAsync(accountId, cancellationToken).ConfigureAwait(true))
        {
            TemplateOptions.Add(new ScopeFilterOption(template.Id, template.Name));
        }
    }

    /// <summary>
    /// Aplica um modelo à mensagem em edição.
    /// </summary>
    /// <remarks>
    /// O corpo do modelo entra <b>antes</b> do texto existente — que em resposta é a
    /// citação, e ela precisa continuar embaixo. O assunto só é preenchido se ainda estiver
    /// vazio: sobrescrever o "Re:" de uma resposta quebraria o encadeamento visual.
    /// </remarks>
    public async Task<bool> ApplyTemplateAsync(
        Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await _templates.GetAsync(templateId, cancellationToken).ConfigureAwait(true);

        if (template is null)
        {
            StatusMessage = "O modelo não existe mais.";
            return false;
        }

        if (Subject.Length == 0 && template.Subject.Length > 0)
        {
            Subject = template.Subject;
        }

        if (template.HtmlBody.Length > 0)
        {
            BodyHtml = BodyHtml.Length > 0
                ? template.HtmlBody + "<br>" + BodyHtml
                : template.HtmlBody;
        }

        return true;
    }

    /// <summary>
    /// Verificação periódica do rascunho automático — a janela a chama em intervalos
    /// curtos, e a gravação só acontece quando a digitação parou.
    /// </summary>
    public async Task AutoSaveTickAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasUnsavedChanges || IsBusy || IsCompleted || AccountId is null)
        {
            return;
        }

        if (_lastEditAt is not { } lastEdit
            || _timeProvider.GetUtcNow() - lastEdit < AutoSaveQuietPeriod)
        {
            return;
        }

        // Compositor esvaziado sem rascunho anterior: não há o que preservar, e gravar
        // criaria um rascunho em branco.
        if (DraftId is null && !HasAnyContent())
        {
            return;
        }

        await SaveDraftAsync(cancellationToken).ConfigureAwait(true);
    }

    private bool HasAnyContent()
        => Subject.Length > 0 || BodyText.Length > 0 || BodyHtml.Length > 0
            || To.Length > 0 || Cc.Length > 0 || Bcc.Length > 0 || Attachments.Count > 0;

    private void RegisterEdit()
    {
        if (_isInitializing)
        {
            return;
        }

        _hasUnsavedChanges = true;
        _lastEditAt = _timeProvider.GetUtcNow();
    }

    partial void OnSubjectChanged(string value) => RegisterEdit();
    partial void OnBodyTextChanged(string value) => RegisterEdit();
    partial void OnBodyHtmlChanged(string value) => RegisterEdit();
    partial void OnToChanged(string value) => RegisterEdit();
    partial void OnCcChanged(string value) => RegisterEdit();
    partial void OnBccChanged(string value) => RegisterEdit();

    /// <summary>Acrescenta um anexo escolhido pelo usuário.</summary>
    public void AddAttachment(string fileName, string filePath, string contentType, long size)
    {
        Attachments.Add(new ComposerAttachmentItem(fileName, filePath, contentType, size));

        // Anexou: o aviso pendente, se havia, perdeu o motivo.
        ShowForgottenAttachmentWarning = false;
    }

    /// <summary>Remove um anexo da lista.</summary>
    public void RemoveAttachment(ComposerAttachmentItem item) => Attachments.Remove(item);

    /// <summary>Grava o rascunho agora.</summary>
    [RelayCommand]
    public async Task SaveDraftAsync(CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _compose.SaveDraftAsync(BuildCommand(accountId), cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage ?? string.Empty;
                return;
            }

            DraftId = result.MessageId;
            StatusMessage = string.Empty;
            _hasUnsavedChanges = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Envia — isto é, entrega à fila de saída.</summary>
    [RelayCommand]
    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        if (AccountId is not { } accountId || IsBusy)
        {
            return;
        }

        var invalid = FirstInvalidAddress();

        if (invalid is not null)
        {
            StatusMessage = $"O endereço '{invalid}' não é válido.";
            return;
        }

        // O aviso de anexo esquecido segura o envio uma vez. Na segunda confirmação, segue.
        if (!ShowForgottenAttachmentWarning
            && ForgottenAttachmentDetector.ShouldWarn(BodyText, Attachments.Count))
        {
            ShowForgottenAttachmentWarning = true;
            StatusMessage =
                "O texto menciona um anexo, mas nenhum arquivo foi anexado. Envie de novo para confirmar.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _compose.SendAsync(BuildCommand(accountId), cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                StatusMessage = result.ErrorMessage ?? string.Empty;
                return;
            }

            StatusMessage = string.Empty;
            IsCompleted = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ComposeMessageCommand BuildCommand(Guid accountId) => new()
    {
        AccountId = accountId,
        DraftId = DraftId,
        Subject = Subject,
        TextBody = BodyText,
        HtmlBody = BodyHtml.Length > 0 ? BodyHtml : null,
        Recipients = ParseAllRecipients(),
        Attachments = Attachments
            .Select(a => new ComposedAttachment(a.FileName, a.FilePath, a.ContentType, a.Size))
            .ToList(),
        InReplyTo = _inReplyTo,
        References = _references,
        ThreadId = _threadId,
        Importance = Importance,
        RequestReadReceipt = RequestReadReceipt,
    };

    private IReadOnlyList<DraftRecipient> ParseAllRecipients()
    {
        var recipients = new List<DraftRecipient>();

        AppendParsed(recipients, To, AddressKind.To);
        AppendParsed(recipients, Cc, AddressKind.Cc);
        AppendParsed(recipients, Bcc, AddressKind.Bcc);

        return recipients;
    }

    private static void AppendParsed(List<DraftRecipient> recipients, string raw, AddressKind kind)
    {
        foreach (var token in SplitAddresses(raw))
        {
            if (EmailAddress.TryParse(token, out var address))
            {
                recipients.Add(new DraftRecipient(kind, address, null));
            }
        }
    }

    /// <summary>
    /// Devolve o primeiro endereço que não valida, para a mensagem de erro apontar o alvo.
    /// </summary>
    /// <remarks>
    /// "Há um endereço inválido" obriga o usuário a caçar qual; apontar o token errado
    /// resolve na primeira olhada.
    /// </remarks>
    internal string? FirstInvalidAddress()
        => SplitAddresses(To).Concat(SplitAddresses(Cc)).Concat(SplitAddresses(Bcc))
            .FirstOrDefault(token => !EmailAddress.TryParse(token, out _));

    /// <summary>Separa por ponto e vírgula ou vírgula — os dois são usados na prática.</summary>
    private static IEnumerable<string> SplitAddresses(string raw)
        => raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JoinAddresses(IReadOnlyList<DraftRecipient> recipients, AddressKind kind)
        => string.Join("; ", recipients.Where(r => r.Kind == kind).Select(r => r.Address.Value));

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
}
