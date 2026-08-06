using System.Text;
using AwesomeAssertions;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Infrastructure.Security;

namespace Sintek.Mail.Infrastructure.Tests.Security;

/// <summary>
/// Cobre a gravação de segredo maior do que uma entrada do cofre comporta.
/// </summary>
/// <remarks>
/// <para>
/// O Gerenciador de Credenciais do Windows limita cada entrada a 2560 bytes, e o caminho
/// até lá inflava o cache do MSAL 2,67 vezes — Base64 multiplica por 4/3 e a gravação em
/// UTF-16 dobra. O consentimento da conta Microsoft acontecia, o provedor confirmava por
/// e-mail que o aplicativo fora conectado, e o cofre local ficava vazio.
/// </para>
/// <para>
/// A alternativa que primeiro ocorre — não gravar quando não cabe — troca um erro visível
/// por uma degradação silenciosa e permanente. Estes testes existem para que ela não volte.
/// </para>
/// </remarks>
public class ChunkedSecretTests
{
    private readonly LimitedCredentialStore _credentials = new();

    /// <summary>Um valor grande e pouco compressível, para forçar mais de uma fatia.</summary>
    private static byte[] Grande(int bytes)
    {
        var value = new byte[bytes];

        // Padrão determinístico e sem repetição longa: dados repetidos seriam comprimidos a
        // quase nada e o teste deixaria de exercitar o fatiamento.
        for (var index = 0; index < bytes; index++)
        {
            value[index] = (byte)((index * 31) ^ (index >> 3));
        }

        return value;
    }

    [Fact]
    public async Task Gravar_ValorPequeno_VoltaIgual()
    {
        var original = Encoding.UTF8.GetBytes("""{"AccessToken":{},"RefreshToken":{}}""");

        await ChunkedSecret.WriteAsync(_credentials, "chave", original);

        (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().Equal(original);
    }

    /// <summary>
    /// O caso que motivou tudo: um cache que não caberia numa entrada só.
    /// </summary>
    [Fact]
    public async Task Gravar_ValorMaiorQueUmaEntrada_VoltaIgual()
    {
        var original = Grande(32_768);

        await ChunkedSecret.WriteAsync(_credentials, "chave", original);

        (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().Equal(original);

        _credentials.Keys.Should().HaveCountGreaterThan(
            2, "um valor deste tamanho precisa de várias fatias além do cabeçalho");
    }

    /// <summary>
    /// Regravar menor não pode deixar fatias da versão anterior para trás.
    /// </summary>
    /// <remarks>
    /// Sem a limpeza, uma leitura futura com contagem maior encontraria fatias antigas e
    /// remontaria um valor misturado — que o MSAL rejeitaria com erro de formato, sem dizer
    /// de onde veio.
    /// </remarks>
    [Fact]
    public async Task Regravar_ValorMenor_NaoDeixaFatiasAntigas()
    {
        await ChunkedSecret.WriteAsync(_credentials, "chave", Grande(32_768));

        var menor = Encoding.UTF8.GetBytes("cache curto");
        await ChunkedSecret.WriteAsync(_credentials, "chave", menor);

        (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().Equal(menor);
        _credentials.Keys.Should().HaveCount(2, "sobra o cabeçalho e uma fatia só");
    }

    [Fact]
    public async Task Remover_ApagaOCabecalhoETodasAsFatias()
    {
        await ChunkedSecret.WriteAsync(_credentials, "chave", Grande(32_768));

        await ChunkedSecret.DeleteAsync(_credentials, "chave");

        _credentials.Keys.Should().BeEmpty(
            "token de uma conta removida não pode sobreviver no Gerenciador de Credenciais");
        (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().BeNull();
    }

    [Fact]
    public async Task Ler_SemNada_DevolveNulo()
        => (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().BeNull();

    /// <summary>
    /// Fatia faltando é valor incompleto, e um cache de token truncado não falha de forma
    /// útil. Tratar como ausente manda o fluxo para o consentimento, que é o que resolve.
    /// </summary>
    [Fact]
    public async Task Ler_ComFatiaFaltando_DevolveNulo()
    {
        await ChunkedSecret.WriteAsync(_credentials, "chave", Grande(32_768));

        await _credentials.DeleteSecretAsync("chave#1");

        (await ChunkedSecret.ReadAsync(_credentials, "chave")).Should().BeNull();
    }

    /// <summary>
    /// Cofre em memória que recusa entradas grandes, como o do Windows recusa.
    /// </summary>
    /// <remarks>
    /// Um dublê sem limite passaria em tudo e não provaria nada: o defeito que estes testes
    /// cobrem só existe porque a entrada tem teto.
    /// </remarks>
    private sealed class LimitedCredentialStore : ICredentialStore
    {
        private const int MaxBlobBytes = 2560;

        private readonly Dictionary<string, string> _secrets = [];

        public IReadOnlyCollection<string> Keys => _secrets.Keys;

        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            if (Encoding.Unicode.GetByteCount(secret) > MaxBlobBytes)
            {
                throw new ArgumentException(
                    $"O segredo excede o limite de {MaxBlobBytes} bytes do Gerenciador de Credenciais.",
                    nameof(secret));
            }

            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.GetValueOrDefault(key));

        public Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.Remove(key));

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.ContainsKey(key));
    }
}
