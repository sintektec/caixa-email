using System.Globalization;
using System.IO.Compression;
using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>
/// Grava no cofre um segredo maior do que uma entrada comporta, comprimido e fatiado.
/// </summary>
/// <remarks>
/// <para>
/// <b>O Gerenciador de Credenciais do Windows limita cada entrada a 2560 bytes</b>, e o
/// caminho até lá infla o valor <b>2,67 vezes</b>: o cache do MSAL sai como JSON, o Base64
/// o multiplica por 4/3, e o <c>Encoding.Unicode</c> da gravação dobra de novo. Um cache de
/// 1 KB já não cabe — e um cache com dois recursos, que é o nosso caso porque o Entra emite
/// token por recurso, passa longe disso.
/// </para>
/// <para>
/// <b>Não persistir quando não cabe seria pior do que falhar.</b> É a saída que primeiro
/// ocorre, e ela troca um erro visível por uma degradação silenciosa e permanente: o usuário
/// consente, o aplicativo funciona naquela sessão, e na próxima abertura o consentimento
/// sumiu — sem mensagem, sem log que ele veja, sem relação aparente com nada. É a mesma
/// forma de falha do provider de SQLite sem criptografia, que "funciona" enquanto grava tudo
/// em claro.
/// </para>
/// <para>
/// A compressão vem antes da fatia porque o cache é JSON e encolhe muito — na prática o
/// suficiente para a maioria dos casos caber numa entrada só. A fatia existe para o resto:
/// ela remove o teto em vez de empurrá-lo, o que importa quando a mesma conta acumula tokens
/// de mais recursos ao longo do tempo.
/// </para>
/// </remarks>
internal static class ChunkedSecret
{
    /// <summary>
    /// Caracteres por fatia.
    /// </summary>
    /// <remarks>
    /// Cada caractere vira 2 bytes na gravação (UTF-16), então 1200 caracteres ocupam 2400
    /// bytes — abaixo dos 2560 permitidos, com folga para o que a implementação acrescente.
    /// </remarks>
    private const int ChunkLength = 1200;

    /// <summary>Grava o valor, fatiando quando necessário.</summary>
    public static async Task WriteAsync(
        ICredentialStore credentials,
        string key,
        byte[] value,
        CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Compress(value));

        var chunks = (encoded.Length + ChunkLength - 1) / ChunkLength;

        for (var index = 0; index < chunks; index++)
        {
            var slice = encoded.Substring(
                index * ChunkLength,
                Math.Min(ChunkLength, encoded.Length - (index * ChunkLength)));

            await credentials.SetSecretAsync(ChunkKey(key, index), slice, cancellationToken)
                .ConfigureAwait(false);
        }

        // A contagem é gravada **por último**: se algo falhar no meio, a leitura seguinte não
        // encontra o cabeçalho e trata como ausente, em vez de montar um valor truncado.
        await credentials
            .SetSecretAsync(key, chunks.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        // Fatias de uma gravação anterior mais longa precisam sair, senão uma leitura futura
        // com contagem maior as encontraria e remontaria um valor misturado.
        await DeleteChunksFromAsync(credentials, key, chunks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lê o valor, ou <see langword="null"/> quando não há nenhum.</summary>
    public static async Task<byte[]?> ReadAsync(
        ICredentialStore credentials, string key, CancellationToken cancellationToken = default)
    {
        var header = await credentials.GetSecretAsync(key, cancellationToken).ConfigureAwait(false);

        if (!int.TryParse(header, CultureInfo.InvariantCulture, out var chunks) || chunks < 1)
        {
            return null;
        }

        var encoded = new System.Text.StringBuilder();

        for (var index = 0; index < chunks; index++)
        {
            var slice = await credentials
                .GetSecretAsync(ChunkKey(key, index), cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(slice))
            {
                // Fatia faltando é valor incompleto, e um cache de token truncado não falha
                // de forma útil: o MSAL o rejeitaria com erro de formato. Tratar como ausente
                // manda o fluxo para o consentimento, que é o que resolve.
                return null;
            }

            encoded.Append(slice);
        }

        try
        {
            return Decompress(Convert.FromBase64String(encoded.ToString()));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Remove o valor e todas as suas fatias.</summary>
    public static async Task DeleteAsync(
        ICredentialStore credentials, string key, CancellationToken cancellationToken = default)
    {
        await DeleteChunksFromAsync(credentials, key, 0, cancellationToken).ConfigureAwait(false);
        await credentials.DeleteSecretAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Apaga fatias a partir de um índice, parando na primeira ausente.
    /// </summary>
    private static async Task DeleteChunksFromAsync(
        ICredentialStore credentials, string key, int start, CancellationToken cancellationToken)
    {
        for (var index = start; ; index++)
        {
            if (!await credentials.DeleteSecretAsync(ChunkKey(key, index), cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static string ChunkKey(string key, int index)
        => string.Create(CultureInfo.InvariantCulture, $"{key}#{index}");

    private static byte[] Compress(byte[] value)
    {
        using var destination = new MemoryStream();

        using (var gzip = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(value, 0, value.Length);
        }

        return destination.ToArray();
    }

    private static byte[] Decompress(byte[] value)
    {
        using var source = new MemoryStream(value);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();

        gzip.CopyTo(destination);
        return destination.ToArray();
    }
}
