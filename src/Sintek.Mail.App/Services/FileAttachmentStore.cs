using Sintek.Mail.Application.UseCases.Messages;

namespace Sintek.Mail.App.Services;

/// <summary>
/// Grava anexos na pasta de dados da aplicação.
/// </summary>
/// <remarks>
/// <para>
/// Cada anexo vive em uma subpasta com o identificador da mensagem, e o nome do arquivo é o
/// identificador do anexo mais a extensão original. O nome vindo da mensagem <b>não</b> é
/// usado como nome físico: ele é escolhido pelo remetente, e um remetente hostil mandaria
/// <c>..\..\algo.exe</c> ou dois anexos com o mesmo nome para sobrescrever um ao outro. O
/// nome original fica no banco e é usado só na hora de exibir e de salvar-como.
/// </para>
/// <para>
/// O conteúdo fica fora do banco de propósito: anexo é o que domina o volume de uma caixa
/// postal, e blobs no SQLite incham o arquivo e degradam o WAL.
/// </para>
/// </remarks>
public sealed class FileAttachmentStore : IAttachmentStore
{
    private readonly string _root;

    public FileAttachmentStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sintek.Mail",
            "Attachments"))
    {
    }

    /// <summary>Cria o armazém com raiz explícita — usado nos testes.</summary>
    public FileAttachmentStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        Guid messageId,
        Guid attachmentId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.Combine(_root, messageId.ToString("N"));
        Directory.CreateDirectory(directory);

        var extension = SafeExtension(fileName);
        var path = Path.Combine(directory, attachmentId.ToString("N") + extension);

        // Escrita em arquivo temporário com troca ao final: um download interrompido no
        // meio não pode deixar meio arquivo com o nome definitivo, que pareceria íntegro.
        var temporary = path + ".baixando";

        await using (var file = File.Create(temporary))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        return path;
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        // O nome físico começa pelo identificador do anexo, então a busca por padrão
        // encontra o arquivo sem precisar saber a extensão nem a mensagem de origem.
        var pattern = attachmentId.ToString("N") + ".*";

        if (!Directory.Exists(_root))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(_root, pattern, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Arquivo em uso por um visualizador aberto: fica para a próxima limpeza.
                // Falhar aqui abortaria a limpeza inteira por causa de um arquivo.
            }
            catch (UnauthorizedAccessException)
            {
                // Mesma razão.
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Extrai a extensão do nome declarado, recusando o que não for um sufixo simples.
    /// </summary>
    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);

        if (extension.Length is 0 or > 10
            || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return ".bin";
        }

        return extension;
    }
}
