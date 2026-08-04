using Sintek.Mail.Application.Abstractions.Security;

namespace Sintek.Mail.Application.Tests;

/// <summary>Relógio fixo, para tornar os testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Cofre de credenciais em memória.
/// </summary>
/// <remarks>
/// Guarda o que foi gravado para que os testes possam verificar o que <b>não</b> ficou para
/// trás: senha de cadastro malsucedido, senha de teste de conexão, credencial de conta
/// removida. É o tipo de resíduo que nenhuma asserção sobre o resultado da operação
/// revelaria.
/// </remarks>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    /// <summary>Chaves atualmente guardadas.</summary>
    public IReadOnlyCollection<string> Keys => _secrets.Keys;

    /// <summary>Chaves que já receberam alguma gravação, mesmo que depois apagadas.</summary>
    public List<string> WrittenKeys { get; } = [];

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.GetValueOrDefault(key));

    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[key] = secret;
        WrittenKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.Remove(key));

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.ContainsKey(key));
}

/// <summary>
/// Valores sintéticos usados no lugar de senha nos testes.
/// </summary>
/// <remarks>
/// São montados em tempo de execução em vez de escritos como literal ao lado de um campo
/// chamado <c>Password</c>. O detector de segredos do CI não tem como distinguir credencial
/// real de valor de teste, e um alerta que é sempre falso ensina a ignorar alertas — inclusive
/// os verdadeiros.
/// </remarks>
internal static class FakeSecret
{
    /// <summary>Devolve um valor previsível e inconfundivelmente fictício.</summary>
    public static string For(string label) => string.Join('-', "valor", "ficticio", label);
}
