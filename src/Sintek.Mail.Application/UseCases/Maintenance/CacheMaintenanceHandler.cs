using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Maintenance;

/// <summary>O que a limpeza vai apagar, medido antes de apagar.</summary>
/// <param name="BodyCount">Corpos de mensagem a descartar.</param>
/// <param name="AttachmentCount">Arquivos de anexo a descartar.</param>
/// <param name="AttachmentBytes">Espaço em disco ocupado por esses anexos.</param>
public readonly record struct CacheCleanupImpact(int BodyCount, int AttachmentCount, long AttachmentBytes)
{
    /// <summary>Se há algo a limpar.</summary>
    public bool HasAnything => BodyCount > 0 || AttachmentCount > 0;

    /// <summary>Resumo para a confirmação, em português.</summary>
    public string Summary => HasAnything
        ? $"Serão descartados {BodyCount} corpo(s) de mensagem e {AttachmentCount} anexo(s), " +
          $"liberando cerca de {FormatSize(AttachmentBytes)}. As mensagens continuam no servidor " +
          "e o conteúdo é baixado de novo quando você abri-las."
        : "Não há conteúdo em cache para limpar.";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

/// <summary>
/// Limpeza do conteúdo baixado: corpos e anexos que podem ser buscados de novo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Segura por construção</b>: só descarta o que o servidor ainda tem. Mensagem que só
/// existe localmente — rascunho, item na Caixa de Saída, mensagem sem UID — fica intacta,
/// porque para ela o cache <i>é</i> o original.
/// </para>
/// <para>
/// A medição vem antes da execução, no mesmo desenho de duas etapas usado na remoção de
/// conta, de diretório e de pasta: o usuário vê o tamanho do estrago antes de autorizar.
/// </para>
/// </remarks>
public sealed class CacheMaintenanceHandler
{
    private readonly IMessageRepository _messages;
    private readonly IAttachmentStore _attachmentStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CacheMaintenanceHandler> _logger;

    public CacheMaintenanceHandler(
        IMessageRepository messages,
        IAttachmentStore attachmentStore,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CacheMaintenanceHandler> logger)
    {
        _messages = messages;
        _attachmentStore = attachmentStore;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Mede o que uma limpeza descartaria, sem alterar nada.</summary>
    /// <param name="olderThan">
    /// Só entra o que foi baixado antes deste intervalo. <see cref="TimeSpan.Zero"/>
    /// alcança tudo.
    /// </param>
    public async Task<CacheCleanupImpact> AnalyzeAsync(
        TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - olderThan;
        var candidates = await _messages.ListCachedContentAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);

        var bodies = 0;
        var attachments = 0;
        var bytes = 0L;

        foreach (var message in candidates)
        {
            if (message.Body?.DownloadedAt is not null)
            {
                bodies++;
            }

            foreach (var attachment in message.Attachments.Where(a => a.IsDownloaded))
            {
                attachments++;
                bytes += attachment.Size;
            }
        }

        return new CacheCleanupImpact(bodies, attachments, bytes);
    }

    /// <summary>Executa a limpeza.</summary>
    public async Task<CacheCleanupImpact> CleanAsync(
        TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - olderThan;
        var now = _timeProvider.GetUtcNow();

        var candidates = await _messages.ListCachedContentAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);

        var bodies = 0;
        var attachments = 0;
        var bytes = 0L;

        foreach (var message in candidates)
        {
            foreach (var attachment in message.Attachments.Where(a => a.IsDownloaded).ToList())
            {
                // O arquivo sai do disco antes do registro: se a ordem fosse a inversa e o
                // processo caísse no meio, sobraria arquivo órfão que ninguém mais
                // encontraria para apagar.
                await _attachmentStore.DeleteAsync(attachment.Id, cancellationToken)
                    .ConfigureAwait(false);

                attachments++;
                bytes += attachment.Size;
                attachment.ClearDownload(now);
            }

            if (message.Body?.DownloadedAt is not null)
            {
                message.Body.ClearContent(now);
                bodies++;
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Limpeza de cache: {Bodies} corpos e {Attachments} anexos descartados.",
            bodies, attachments);

        return new CacheCleanupImpact(bodies, attachments, bytes);
    }
}
