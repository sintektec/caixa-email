using Microsoft.Extensions.DependencyInjection;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Monta um <see cref="IServiceScopeFactory"/> com os dublês que o teste quer entregar.
/// </summary>
/// <remarks>
/// <para>
/// Os ViewModels residentes deixaram de receber repositórios e passaram a receber a fábrica
/// de escopos, para não prender um <c>DbContext</c> pela vida inteira da aplicação. O teste
/// precisa acompanhar essa mudança, e o caminho honesto é montar um contêiner de verdade:
/// substituir a fábrica por um dublê que devolvesse sempre o mesmo provedor esconderia
/// justamente o que se quer verificar — que cada operação abre e descarta o seu escopo.
/// </para>
/// <para>
/// Os dublês são registrados como <c>Scoped</c> a partir de instância única, então o teste
/// continua inspecionando o mesmo objeto que o ViewModel usou. É o comportamento que os
/// testes já esperavam quando recebiam o serviço por construtor.
/// </para>
/// </remarks>
internal sealed class TestScopes
{
    private readonly ServiceCollection _services = [];

    /// <summary>Registra um serviço sob o tipo declarado em <typeparamref name="T"/>.</summary>
    /// <remarks>
    /// O tipo genérico é que manda, e não <c>instance.GetType()</c>: um dublê do NSubstitute
    /// é uma classe de proxy, e registrá-lo pelo tipo concreto deixaria a interface que o
    /// ViewModel pede sem registro nenhum.
    /// </remarks>
    public TestScopes With<T>(T instance)
        where T : class
    {
        _services.AddScoped(_ => instance);
        return this;
    }

    /// <remarks>
    /// Com as mesmas opções que a aplicação usa: o teste não teria valor se aceitasse um
    /// registro que o contêiner de verdade recusa.
    /// </remarks>
    public IServiceScopeFactory Build()
        => _services
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            })
            .GetRequiredService<IServiceScopeFactory>();
}
