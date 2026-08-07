using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Application;
using Sintek.Mail.Infrastructure;
using Sintek.Mail.Persistence;
using Sintek.Mail.Presentation;

namespace Sintek.Mail.Composition;

/// <summary>
/// Encadeia o registro das quatro camadas que não dependem do Windows.
/// </summary>
/// <remarks>
/// <para>
/// O aplicativo chama isto e acrescenta só o que é Windows-only. O teste de composição chama
/// a mesma coisa — e é essa igualdade que dá valor ao teste: uma lista paralela de chamadas
/// <c>AddSintekMail*</c> divergiria da real com o tempo, e o teste passaria a provar um
/// contêiner que ninguém executa.
/// </para>
/// </remarks>
public static class CoreComposition
{
    /// <summary>Registra Aplicação, Infraestrutura, Persistência e Apresentação.</summary>
    public static IServiceCollection AddSintekMailCore(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, DatabaseOptions> databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddSintekMailApplication()
            .AddSintekMailInfrastructure(configuration)
            .AddSintekMailPersistence(databaseOptions)
            .AddSintekMailPresentation();
    }
}
