using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>
/// Base dos ViewModels que vivem enquanto a janela viver e precisam falar com o banco.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um ViewModel residente não pode receber repositório por construtor.</b> Ele é resolvido
/// uma vez e guardado pela janela; o repositório e o <c>DbContext</c> que vierem com ele
/// ficam presos junto, e passam a valer para a aplicação inteira. É a dependência cativa, e
/// ela não falha de forma honesta: o contêiner aceita, a aplicação abre, e o defeito só
/// aparece quando duas escritas se cruzam.
/// </para>
/// <para>
/// Foi assim que apareceu aqui — <c>DbUpdateConcurrencyException</c> ao clicar numa mensagem
/// enquanto a sincronização rodava, mais travamento, porque <c>DbContext</c> também não é
/// seguro para uso concorrente. O contexto da interface guardava valores antigos; o laço de
/// sincronização, que abre escopo próprio a cada rodada, atualizava as mesmas linhas.
/// </para>
/// <para>
/// A saída é a mesma que o <c>AccountSyncWorker</c> já usa: <b>um escopo por operação</b>.
/// O ViewModel guarda a fábrica de escopos, não o serviço. Cada comando abre o seu, resolve
/// o que precisa, e descarta ao terminar — inclusive o <c>DbContext</c>, com tudo que ele
/// rastreava.
/// </para>
/// <para>
/// <b>Entidade não atravessa escopo.</b> O que sai de um escopo é dado — identificador, texto,
/// número —, nunca entidade rastreada. Guardar uma e gravá-la noutro escopo devolve o mesmo
/// defeito por caminho oposto, e nenhuma validação de contêiner pega isso.
/// </para>
/// </remarks>
public abstract class ScopedViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopes;

    protected ScopedViewModel(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <summary>Executa a operação num escopo próprio, descartado ao final.</summary>
    /// <remarks>
    /// O <c>ConfigureAwait(true)</c> é deliberado: quem chama está na linha de execução da
    /// interface e atualiza propriedades ligadas à tela logo depois.
    /// </remarks>
    protected async Task InScopeAsync(
        Func<IServiceProvider, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = _scopes.CreateAsyncScope();
        await work(scope.ServiceProvider).ConfigureAwait(true);
    }

    /// <inheritdoc cref="InScopeAsync(Func{IServiceProvider, Task}, CancellationToken)" />
    protected async Task<T> InScopeAsync<T>(
        Func<IServiceProvider, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = _scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider).ConfigureAwait(true);
    }
}
