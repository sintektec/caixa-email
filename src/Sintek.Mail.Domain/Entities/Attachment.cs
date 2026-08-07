using Sintek.Mail.Domain.Common;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Anexo de uma mensagem.
/// </summary>
/// <remarks>
/// O conteúdo do arquivo fica no disco, em <see cref="StoragePath"/>, e não como BLOB no
/// banco. Um único anexo de dezenas de megabytes dentro do SQLite inflaria o arquivo
/// para sempre — o SQLite não devolve espaço ao sistema sem um VACUUM completo — e faria
/// cada backup da base carregar todos os anexos junto.
/// </remarks>
public sealed class Attachment : Entity
{
    /// <summary>
    /// Extensões que o Windows executa ou interpreta e que, portanto, merecem alerta
    /// antes de abrir.
    /// </summary>
    /// <remarks>
    /// A lista cobre executáveis, scripts, atalhos e contêineres de macro. Ela é um
    /// alerta ao usuário, não um bloqueio: a decisão final é dele, mas precisa ser
    /// informada.
    /// </remarks>
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst", ".dll", ".sys",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".jar", ".reg", ".lnk", ".url", ".inf", ".scf", ".chm", ".application",
        ".gadget", ".msc", ".jnlp", ".ade", ".adp", ".mde", ".sct", ".shb", ".ws",
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm",
        ".iso", ".img", ".vhd", ".vhdx",
    };

    private Attachment(
        Guid id,
        Guid messageId,
        string fileName,
        string contentType,
        long size,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        MessageId = messageId;
        FileName = fileName;
        ContentType = contentType;
        Size = size;
        IsSuspicious = IsDangerousFileName(fileName);
    }

    private Attachment()
    {
    }

    /// <summary>Mensagem dona do anexo.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Mensagem dona do anexo.</summary>
    public Message? Message { get; private set; }

    /// <summary>Nome do arquivo, já sanitizado contra travessia de diretório.</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>Tipo MIME declarado.</summary>
    public string ContentType { get; private set; } = "application/octet-stream";

    /// <summary>Tamanho em bytes.</summary>
    public long Size { get; private set; }

    /// <summary>Content-ID, quando o anexo é embutido no corpo HTML.</summary>
    public string? ContentId { get; private set; }

    /// <summary>Se o anexo é embutido (referenciado por <c>cid:</c>) em vez de avulso.</summary>
    public bool IsInline { get; private set; }

    /// <summary>Caminho local do arquivo baixado. Nulo enquanto não foi baixado.</summary>
    public string? StoragePath { get; private set; }

    /// <summary>Identificador da parte MIME no BODYSTRUCTURE, para baixar sob demanda.</summary>
    public string PartSpecifier { get; private set; } = string.Empty;

    /// <summary>Se o conteúdo já foi baixado para o disco.</summary>
    public bool IsDownloaded { get; private set; }

    /// <summary>Se a extensão do arquivo merece alerta ao usuário.</summary>
    public bool IsSuspicious { get; private set; }

    /// <summary>Cria um anexo a partir dos metadados vindos do BODYSTRUCTURE.</summary>
    public static Attachment Create(
        Guid messageId,
        string fileName,
        string contentType,
        long size,
        string partSpecifier,
        DateTimeOffset createdAt,
        string? contentId = null,
        bool isInline = false,
        Guid? id = null)
    {
        return new Attachment(
            id ?? Guid.CreateVersion7(),
            messageId,
            SanitizeFileName(fileName),
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            size,
            createdAt)
        {
            PartSpecifier = partSpecifier ?? string.Empty,
            ContentId = contentId,
            IsInline = isInline,
        };
    }

    /// <summary>Registra que o anexo foi gravado no disco.</summary>
    public void MarkDownloaded(string storagePath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        StoragePath = storagePath;
        IsDownloaded = true;
        Touch(now);
    }

    /// <summary>
    /// Descarta o conteúdo baixado, mantendo os metadados do anexo.
    /// </summary>
    /// <remarks>
    /// O anexo continua listado na mensagem — nome, tipo e tamanho vieram do
    /// BODYSTRUCTURE, não do arquivo — e volta a ser baixável sob demanda. É o que faz a
    /// limpeza de cache não parecer perda de dados.
    /// </remarks>
    public void ClearDownload(DateTimeOffset now)
    {
        StoragePath = null;
        IsDownloaded = false;
        Touch(now);
    }

    /// <summary>Indica se a extensão do arquivo é executável ou interpretável no Windows.</summary>
    public static bool IsDangerousFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && DangerousExtensions.Contains(extension);
    }

    /// <summary>
    /// Reduz o nome a um nome de arquivo simples, sem componentes de caminho.
    /// </summary>
    /// <remarks>
    /// Um servidor hostil pode anunciar um anexo chamado <c>..\..\Startup\x.exe</c>.
    /// Sem esta normalização, salvar o anexo escreveria fora da pasta prevista.
    /// </remarks>
    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "anexo.bin";
        }

        // Corta qualquer componente de diretório, nos dois separadores — a mensagem pode
        // ter sido montada em um sistema com convenção diferente da nossa.
        var name = fileName.Trim();
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim(' ', '.');

        return string.IsNullOrWhiteSpace(name) ? "anexo.bin" : name;
    }
}
