using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Messages;

namespace Sintek.Mail.Application.UseCases.Assistant;

/// <summary>
/// Os recursos de IA sobre a caixa postal: resumir, sugerir resposta e reescrever.
/// </summary>
/// <remarks>
/// Cada recurso é uma tradução fina de "o que o usuário pediu" para
/// <see cref="AssistantRequest"/>. A política — quem processa e se pode — vive inteira no
/// <see cref="AssistantGateway"/>, e é por isso que este handler não conhece provedor
/// nenhum.
/// </remarks>
public sealed class AssistantFeaturesHandler
{
    /// <summary>
    /// Teto de texto enviado ao assistente.
    /// </summary>
    /// <remarks>
    /// Corta o corpo antes de sair da máquina: uma thread de meses tem centenas de
    /// milhares de caracteres, e mandar tudo custa caro sem melhorar o resumo. O corte é
    /// no começo do texto, que é onde a mensagem diz a que veio.
    /// </remarks>
    public const int MaxContentLength = 12_000;

    private readonly AssistantGateway _gateway;
    private readonly IMessageRepository _messages;
    private readonly DownloadMessageContentHandler _download;

    public AssistantFeaturesHandler(
        AssistantGateway gateway,
        IMessageRepository messages,
        DownloadMessageContentHandler download)
    {
        _gateway = gateway;
        _messages = messages;
        _download = download;
    }

    /// <summary>Resume uma mensagem.</summary>
    public Task<AssistantResult> SummarizeMessageAsync(
        Guid messageId, CancellationToken cancellationToken = default)
        => RunOnMessageAsync(messageId, AssistantTask.Summarize, null, cancellationToken);

    /// <summary>Sugere uma resposta para uma mensagem.</summary>
    public Task<AssistantResult> SuggestReplyAsync(
        Guid messageId, string? instruction = null, CancellationToken cancellationToken = default)
        => RunOnMessageAsync(messageId, AssistantTask.SuggestReply, instruction, cancellationToken);

    /// <summary>
    /// Reescreve um texto do compositor.
    /// </summary>
    /// <remarks>
    /// Recebe o texto direto do compositor, e não um identificador de mensagem: o que está
    /// sendo escrito ainda não existe no banco.
    /// </remarks>
    public async Task<AssistantResult> RewriteAsync(
        Guid accountId,
        string text,
        string? instruction = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AssistantResult(
                false, string.Empty, AssistantRefusal.None,
                "Escreva alguma coisa antes de pedir para reescrever.", null);
        }

        return await _gateway.RequestAsync(
            accountId,
            new AssistantRequest(AssistantTask.Rewrite, Truncate(text), instruction),
            messageId: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Se a interface deve oferecer os recursos de IA para esta conta.</summary>
    public Task<bool> IsAvailableForAsync(Guid accountId, CancellationToken cancellationToken = default)
        => _gateway.IsAvailableForAsync(accountId, cancellationToken);

    private async Task<AssistantResult> RunOnMessageAsync(
        Guid messageId, AssistantTask task, string? instruction, CancellationToken cancellationToken)
    {
        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            return new AssistantResult(
                false, string.Empty, AssistantRefusal.None, "A mensagem não existe mais.", null);
        }

        // Resumir a prévia não resumiria nada: o corpo é o que importa aqui.
        if (message.Body?.DownloadedAt is null)
        {
            var downloaded = await _download.DownloadBodyAsync(messageId, cancellationToken)
                .ConfigureAwait(false);

            if (!downloaded.Succeeded)
            {
                return new AssistantResult(
                    false, string.Empty, AssistantRefusal.None,
                    $"O conteúdo da mensagem não pôde ser lido: {downloaded.ErrorMessage}", null);
            }

            message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
                .ConfigureAwait(false) ?? message;
        }

        var content = BuildContent(message);

        if (content.Length == 0)
        {
            return new AssistantResult(
                false, string.Empty, AssistantRefusal.None,
                "Esta mensagem não tem texto para processar.", null);
        }

        return await _gateway.RequestAsync(
            message.AccountId,
            new AssistantRequest(task, content, instruction),
            messageId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Monta o texto enviado: assunto e corpo em texto puro.
    /// </summary>
    /// <remarks>
    /// O corpo em texto, nunca o HTML: marcação não ajuda o modelo e infla o que sai da
    /// máquina. Endereços de participantes também ficam de fora — o resumo não precisa
    /// deles e cada dado a menos é um dado a menos exposto.
    /// </remarks>
    private static string BuildContent(Domain.Entities.Message message)
    {
        var body = message.Body?.TextBody ?? message.Preview;

        var text = string.IsNullOrWhiteSpace(message.Subject)
            ? body
            : $"Assunto: {message.Subject}\n\n{body}";

        return Truncate(text?.Trim() ?? string.Empty);
    }

    private static string Truncate(string text)
        => text.Length <= MaxContentLength ? text : text[..MaxContentLength];
}
