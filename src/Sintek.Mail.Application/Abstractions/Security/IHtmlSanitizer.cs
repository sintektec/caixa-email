namespace Sintek.Mail.Application.Abstractions.Security;

/// <summary>Resultado da higienização de um corpo HTML.</summary>
/// <param name="SanitizedHtml">HTML seguro para renderizar.</param>
/// <param name="HasRemoteContent">
/// Se o original referenciava recursos externos. Quando verdadeiro, a interface exibe a
/// barra "Exibir imagens" e só libera o carregamento após o usuário concordar.
/// </param>
/// <param name="RemovedRemoteReferences">Quantas referências remotas foram neutralizadas.</param>
public readonly record struct SanitizedHtmlResult(
    string SanitizedHtml,
    bool HasRemoteContent,
    int RemovedRemoteReferences);

/// <summary>
/// Higieniza o HTML de mensagens antes de qualquer renderização.
/// </summary>
/// <remarks>
/// <para>
/// Um corpo de e-mail é conteúdo hostil por definição: qualquer pessoa pode enviá-lo. A
/// higienização remove scripts, manipuladores de evento, iframes, objetos incorporados e
/// esquemas de URI perigosos (<c>javascript:</c>, <c>vbscript:</c>, <c>data:</c> em
/// contexto ativo).
/// </para>
/// <para>
/// Ela é a <b>primeira</b> de duas camadas. A segunda é o WebView2 configurado sem
/// scripts, sem DevTools e com navegação bloqueada. Nenhuma das duas sozinha é suficiente.
/// </para>
/// </remarks>
public interface IHtmlSanitizer
{
    /// <summary>
    /// Higieniza <paramref name="html"/>.
    /// </summary>
    /// <param name="html">HTML original da mensagem.</param>
    /// <param name="allowRemoteContent">
    /// Se as referências remotas devem ser preservadas. Falso por padrão, como exige a
    /// especificação: imagens remotas confirmam ao remetente que a mensagem foi aberta.
    /// </param>
    SanitizedHtmlResult Sanitize(string? html, bool allowRemoteContent = false);

    /// <summary>Converte texto puro em HTML seguro, preservando quebras de linha.</summary>
    string PlainTextToHtml(string? text);
}
