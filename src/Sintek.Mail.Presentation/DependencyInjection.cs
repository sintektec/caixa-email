using Microsoft.Extensions.DependencyInjection;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation;

/// <summary>Registro dos ViewModels.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra a camada de apresentação.
    /// </summary>
    /// <remarks>
    /// O ViewModel da janela principal é singleton porque a árvore de navegação é estado
    /// compartilhado da sessão. Os demais são transitórios: cada assistente, editor ou
    /// painel aberto começa do zero, e reaproveitar a instância traria de volta o endereço
    /// digitado no cadastro anterior.
    /// </remarks>
    public static IServiceCollection AddSintekMailPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ShellViewModel>();

        services.AddTransient<MessageListViewModel>();
        services.AddTransient<ReadingPaneViewModel>();
        services.AddTransient<AccountSetupViewModel>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<DomainDirectoryEditorViewModel>();
        services.AddTransient<OutboxQueueViewModel>();
        services.AddTransient<ComposerViewModel>();

        return services;
    }
}
