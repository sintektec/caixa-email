namespace Sintek.Mail.Application.Abstractions.Security;

/// <summary>
/// Guarda segredos fora do banco de dados.
/// </summary>
/// <remarks>
/// A implementação de produção é o Windows Credential Manager. Nenhuma senha, token ou
/// chave de banco pode ser gravada em tabela, arquivo de configuração ou log — esta
/// interface é o único caminho autorizado.
///
/// Ela vive na camada de Aplicação, e não na de Infraestrutura, para que o núcleo possa
/// depender dela sem depender do Windows: é o que mantém as camadas multiplataforma
/// compiláveis e testáveis em Linux.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>Grava ou substitui um segredo.</summary>
    Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default);

    /// <summary>Lê um segredo. Devolve <see langword="null"/> quando não existe.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Remove um segredo.</summary>
    Task<bool> DeleteSecretAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Indica se há um segredo gravado sob a chave.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fornece a chave de criptografia do banco local.
/// </summary>
/// <remarks>
/// A chave é gerada aleatoriamente na primeira execução e guardada no
/// <see cref="ICredentialStore"/>. Ela nunca é derivada de senha do usuário nem gravada
/// em disco fora do cofre do Windows.
/// </remarks>
public interface IDatabaseKeyProvider
{
    /// <summary>
    /// Devolve a chave do banco, criando-a na primeira execução.
    /// </summary>
    Task<string> GetOrCreateKeyAsync(CancellationToken cancellationToken = default);
}
