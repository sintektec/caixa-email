using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Sintek.Mail.App.Services;

/// <summary>
/// Amarra o tempo de vida de um escopo de injeção ao de um diálogo.
/// </summary>
/// <remarks>
/// <para>
/// Cada diálogo é uma operação do usuário com começo e fim claros, e é essa a unidade certa
/// para um <c>DbContext</c>: ele nasce quando a tela abre e morre quando ela fecha, levando
/// junto tudo que rastreou.
/// </para>
/// <para>
/// O que havia antes era o oposto — os ViewModels vinham do provedor raiz, que é o escopo
/// mais longo que existe, e com eles um contexto que valia para a aplicação inteira. Abrir e
/// fechar cinco diálogos deixava cinco conjuntos de entidades rastreadas para sempre, todas
/// envelhecendo enquanto o laço de sincronização escrevia nas mesmas linhas.
/// </para>
/// </remarks>
internal static class DialogScopes
{
    /// <summary>Descarta <paramref name="scope"/> quando <paramref name="dialog"/> fechar.</summary>
    /// <remarks>
    /// O evento <c>Closed</c> dispara tanto no botão quanto no fechamento por código ou por
    /// tecla, que é o que garante o descarte em todos os caminhos de saída. Um
    /// <c>ContentDialog</c> não é reaberto depois de fechado — cada exibição passa por uma
    /// chamada nova da fábrica —, então não há risco de usar um escopo já descartado.
    /// </remarks>
    public static T WithScope<T>(this T dialog, AsyncServiceScope scope)
        where T : ContentDialog
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.Closed += async (_, _) => await scope.DisposeAsync().ConfigureAwait(true);
        return dialog;
    }
}
