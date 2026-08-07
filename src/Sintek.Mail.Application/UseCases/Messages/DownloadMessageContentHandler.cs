using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Application.UseCases.Messages;

/// <summary>
/// Guarda o conteúdo de anexos fora do banco de dados.
/// </summary>
/// <remarks>
/// Anexo é o que domina o volume de uma caixa postal, e blobs dentro do SQLite incham o
/// arquivo e degradam o WAL. O banco guarda apenas o caminho; o conteúdo vai para a pasta de
/// anexos da aplicação. A implementação real vive na camada de App, que sabe onde essa pasta
/// fica; os testes usam uma em memória.
/// </remarks>
public interface IAttachmentStore
{
    /// <summary>Grava o conteúdo e devolve o caminho de armazenamento.</summary>
    Task<string> SaveAsync(
        Guid messageId, Guid attachmentId, string fileName, Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apaga o arquivo de um anexo, se existir.
    /// </summary>
    /// <remarks>
    /// Arquivo já ausente não é erro: a limpeza precisa ser idempotente para que uma
    /// execução interrompida possa ser repetida sem susto.
    /// </remarks>
    Task DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default);
}

/// <summary>Resultado do download de corpo.</summary>
/// <param name="Succeeded">Se o corpo está disponível ao final.</param>
/// <param name="ErrorMessage">Motivo exibível quando não está.</param>
public readonly record struct DownloadBodyResult(bool Succeeded, string? ErrorMessage);

/// <summary>
/// Baixa corpo e anexos sob demanda — o caminho do clique em uma mensagem ainda não baixada.
/// </summary>
/// <remarks>
/// <para>
/// A sincronização traz só cabeçalhos; o corpo desce quando o usuário abre a mensagem, como
/// manda a política <c>OnDemand</c>/<c>RecentOnly</c>. O HTML é higienizado <b>no momento da
/// gravação</b>, e o original fica guardado — é o que permite reprocessar mensagens antigas
/// quando as regras de sanitização mudarem.
/// </para>
/// <para>
/// O download é idempotente: corpo já presente devolve sucesso sem tocar na rede. É o que
/// torna seguro chamá-lo em todo clique de mensagem.
/// </para>
/// </remarks>
public sealed class DownloadMessageContentHandler
{
    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IImapClient _imapClient;
    private readonly IHtmlSanitizer _sanitizer;
    private readonly IAttachmentStore _attachmentStore;
    private readonly Calendar.ImportInvitationHandler _invitations;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DownloadMessageContentHandler> _logger;

    public DownloadMessageContentHandler(
        IMessageRepository messages,
        IFolderRepository folders,
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        IImapClient imapClient,
        IHtmlSanitizer sanitizer,
        IAttachmentStore attachmentStore,
        Calendar.ImportInvitationHandler invitations,
        TimeProvider timeProvider,
        ILogger<DownloadMessageContentHandler> logger)
    {
        _messages = messages;
        _folders = folders;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _imapClient = imapClient;
        _sanitizer = sanitizer;
        _attachmentStore = attachmentStore;
        _invitations = invitations;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Baixa o corpo da mensagem, se ainda não estiver no banco.</summary>
    public async Task<DownloadBodyResult> DownloadBodyAsync(
        Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            return new DownloadBodyResult(false, "A mensagem não existe mais.");
        }

        if (message.Body?.DownloadedAt is not null)
        {
            return new DownloadBodyResult(true, null);
        }

        var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);

        if (folder is null || folder.IsLocalOnly || message.Uid is not > 0)
        {
            // Sem contrapartida no servidor não há de onde baixar. Rascunho local e mensagem
            // desviada para pendências caem aqui — o corpo delas ou já existe ou nunca existiu.
            return new DownloadBodyResult(false, "Esta mensagem não tem conteúdo no servidor.");
        }

        if (await ConnectAsync(message.AccountId, cancellationToken).ConfigureAwait(false)
            is { Succeeded: false } failure)
        {
            return new DownloadBodyResult(false, failure.ErrorMessage);
        }

        FetchedBody? fetched;

        try
        {
            fetched = await _imapClient
                .FetchBodyAsync(folder.RemotePath, message.Uid.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Conectar já era protegido; buscar não era. Rede que cai no meio do FETCH,
            // servidor que derruba a sessão, tempo esgotado do MailKit — tudo isso vinha como
            // exceção, subia pelo manipulador `async void` da seleção de mensagem e derrubava
            // a aplicação. Um clique não pode fechar o programa.
            _logger.LogWarning(
                ex, "Falha ao baixar o corpo da mensagem {MessageId}.", message.Id);

            return new DownloadBodyResult(
                false, "Não foi possível baixar o conteúdo. Verifique a conexão e tente de novo.");
        }

        if (fetched is null)
        {
            // Nulo aqui quer dizer uma coisa só: o servidor respondeu e não achou o UID na
            // pasta. Falha de rede vem como exceção, tratada acima.
            //
            // Isso é prova de que o marcador desta pasta não corresponde ao servidor, e a
            // leitura incremental jamais descobriria sozinha: ela parte do último UID visto
            // e nunca revisita linha antiga. Pedir releitura completa é o que dá à próxima
            // sincronização a chance de corrigir — reconhecendo cada mensagem pelo
            // Message-ID dentro da pasta, sem duplicar nada (D-042).
            folder.RequestFullReread(_timeProvider.GetUtcNow());
            await SaveQuietlyAsync(cancellationToken).ConfigureAwait(false);

            return new DownloadBodyResult(
                false,
                "O conteúdo desta mensagem não foi encontrado no servidor. "
                    + "A pasta será relida na próxima sincronização; tente de novo em seguida.");
        }

        var now = _timeProvider.GetUtcNow();

        // Higieniza na gravação: o HtmlBody original fica guardado para reprocessamento
        // futuro, e o SanitizedHtml é o único que a interface entrega ao WebView2.
        var sanitized = _sanitizer.Sanitize(fetched.HtmlBody, allowRemoteContent: false);

        var body = message.Body ?? MessageBody.Create(message.Id, now);
        body.SetContent(
            fetched.HtmlBody,
            fetched.TextBody,
            sanitized.SanitizedHtml,
            sanitized.HasRemoteContent,
            now);

        if (message.Body is null)
        {
            // As duas coisas, e não uma: SetBody liga a navegação em memória, que é o que o
            // painel de leitura lê logo em seguida; AddBody registra a inserção, que é o que
            // faz a gravação funcionar. Sem a segunda, o EF vê a chave preenchida, decide
            // Modified e emite UPDATE para uma linha que nunca existiu (D-047).
            message.SetBody(body, now);
            _messages.AddBody(body);
        }

        foreach (var meta in fetched.Attachments)
        {
            // Anexos já conhecidos não são recriados: o download de corpo pode rodar de
            // novo após uma falha parcial, e duplicá-los mostraria o mesmo arquivo duas
            // vezes no painel.
            if (message.Attachments.Any(a => a.PartSpecifier == meta.PartSpecifier))
            {
                continue;
            }

            var attachment = Attachment.Create(
                message.Id,
                meta.FileName,
                meta.ContentType,
                meta.Size,
                meta.PartSpecifier,
                now,
                meta.ContentId,
                meta.IsInline);

            message.AddAttachment(attachment);
            _messages.AddAttachment(attachment);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentModificationException ex)
        {
            // Entre carregar a mensagem e chegar aqui houve uma ida à rede — conectar e
            // baixar o corpo, segundos —, e nesse intervalo o laço de sincronização escreve
            // nas mesmas linhas, em escopo próprio. Se ele removeu a mensagem, a gravação
            // encontra zero linhas.
            //
            // Vira resultado exibível, nunca exceção: o caminho sai de um clique, e a
            // exceção subia pelo manipulador `async void` da seleção e derrubava a
            // aplicação (D-041).
            _logger.LogWarning(
                ex, "A mensagem {MessageId} mudou durante o download do corpo.", message.Id);

            return new DownloadBodyResult(
                false,
                "Esta mensagem foi alterada pela sincronização enquanto era aberta. "
                    + "Abra-a novamente.");
        }

        // O convite entra na agenda ao abrir a mensagem — é quando o corpo desce, e é o
        // momento em que o usuário espera vê-lo lá. Falha na importação não derruba o
        // download: o corpo já está gravado, e perder a mensagem por causa de um .ics
        // malformado seria trocar um problema pequeno por um grande.
        if (!string.IsNullOrWhiteSpace(fetched.CalendarPayload))
        {
            try
            {
                await _invitations
                    .ImportAsync(message.AccountId, fetched.CalendarPayload, message.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex, "Convite da mensagem {MessageId} não pôde ser importado.", message.Id);
            }
        }

        return new DownloadBodyResult(true, null);
    }

    /// <summary>Baixa o conteúdo de um anexo para o disco.</summary>
    public async Task<DownloadBodyResult> DownloadAttachmentAsync(
        Guid messageId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetWithParticipantsAsync(messageId, cancellationToken)
            .ConfigureAwait(false);

        var attachment = message?.Attachments.FirstOrDefault(a => a.Id == attachmentId);

        if (message is null || attachment is null)
        {
            return new DownloadBodyResult(false, "O anexo não existe mais.");
        }

        if (attachment.IsDownloaded)
        {
            return new DownloadBodyResult(true, null);
        }

        var folder = await _folders.GetByIdAsync(message.FolderId, cancellationToken).ConfigureAwait(false);

        if (folder is null || folder.IsLocalOnly || message.Uid is not > 0)
        {
            return new DownloadBodyResult(false, "Este anexo não tem conteúdo no servidor.");
        }

        if (await ConnectAsync(message.AccountId, cancellationToken).ConfigureAwait(false)
            is { Succeeded: false } failure)
        {
            return new DownloadBodyResult(false, failure.ErrorMessage);
        }

        Stream? fetched;

        try
        {
            fetched = await _imapClient
                .FetchAttachmentAsync(
                    folder.RemotePath, message.Uid.Value, attachment.PartSpecifier, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mesmo motivo do corpo: este caminho sai de um clique, e exceção aqui sobe pelo
            // manipulador `async void` e derruba a aplicação.
            _logger.LogWarning(
                ex, "Falha ao baixar o anexo {AttachmentId}.", attachment.Id);

            return new DownloadBodyResult(
                false, "Não foi possível baixar o anexo. Verifique a conexão e tente de novo.");
        }

        if (fetched is null)
        {
            return new DownloadBodyResult(
                false,
                "Esta mensagem não está mais na pasta do servidor, então o anexo não pode ser baixado.");
        }

        await using var content = fetched;

        var path = await _attachmentStore
            .SaveAsync(message.Id, attachment.Id, attachment.FileName, content, cancellationToken)
            .ConfigureAwait(false);

        attachment.MarkDownloaded(path, _timeProvider.GetUtcNow());
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Anexo {AttachmentId} da mensagem {MessageId} baixado.", attachment.Id, message.Id);

        return new DownloadBodyResult(true, null);
    }

    /// <summary>
    /// Grava sem deixar a falha da gravação virar a falha do usuário.
    /// </summary>
    /// <remarks>
    /// Usado para efeitos secundários — pedir releitura de uma pasta, por exemplo. O que o
    /// usuário pediu foi abrir a mensagem, e ele já vai receber a explicação do que houve;
    /// deixar um conflito de concorrência nesta gravação sobrepor aquela explicação trocaria
    /// uma resposta útil por outra sem relação com o que ele fez.
    /// </remarks>
    private async Task SaveQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentModificationException ex)
        {
            _logger.LogDebug(ex, "Efeito secundário não gravado por conflito de concorrência.");
        }
    }

    /// <summary>
    /// Garante que o cliente IMAP deste escopo esteja conectado.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Quem baixa também conecta.</b> O <c>IImapClient</c> tem escopo, e este caso de uso
    /// roda no escopo do clique em uma mensagem — não no do laço de sincronização, que conecta
    /// o cliente dele. São instâncias diferentes, e a daqui nasce desconectada: sem esta etapa,
    /// <c>FetchBodyAsync</c> cai no <c>EnsureConnected</c> da implementação e lança
    /// <see cref="InvalidOperationException"/>, que sobe pelo manipulador <c>async void</c> da
    /// seleção de mensagem e <b>derruba a aplicação</b> — o clique em uma mensagem ainda não
    /// baixada fechava o programa.
    /// </para>
    /// <para>
    /// A falha vira <see cref="DownloadBodyResult"/>, e não exceção, porque este caminho é
    /// disparado por um clique: sem rede, o esperado é a faixa de aviso do painel de leitura,
    /// nunca uma tela de erro.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> quando a conexão está de pé; o resultado da falha, quando não.
    /// </returns>
    private async Task<ConnectionTestResult?> ConnectAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        if (_imapClient.IsConnected)
        {
            return null;
        }

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return ConnectionTestResult.Failure("A conta desta mensagem não existe mais.");
        }

        var connection = await _imapClient.ConnectAsync(account, cancellationToken).ConfigureAwait(false);

        if (connection.Succeeded)
        {
            return null;
        }

        _logger.LogWarning(
            "Não foi possível conectar à conta {AccountId} para baixar conteúdo.", accountId);

        return connection;
    }
}
