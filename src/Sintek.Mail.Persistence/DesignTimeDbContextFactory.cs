using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sintek.Mail.Persistence;

/// <summary>
/// Contexto usado exclusivamente pelas ferramentas de linha de comando do EF Core
/// (<c>dotnet ef migrations add</c>, <c>dotnet ef dbcontext script</c>).
/// </summary>
/// <remarks>
/// Gerar migrações exige apenas o modelo, não o banco real — por isso este contexto
/// aponta para um arquivo descartável e <b>sem chave de criptografia</b>. Ele nunca é
/// usado em execução: a aplicação monta o contexto via
/// <see cref="DependencyInjection.AddSintekMailPersistence"/>, que obtém a chave do
/// Windows Credential Manager.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        SqlCipherConnectionFactory.EnsureProviderInitialized();

        var builder = new DbContextOptionsBuilder<MailDbContext>();
        builder.UseSqlite(
            "Data Source=sintek-mail-design.db",
            sqlite => sqlite.MigrationsAssembly(typeof(MailDbContext).Assembly.FullName));

        return new MailDbContext(builder.Options);
    }
}
