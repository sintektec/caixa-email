using Sintek.Mail.Infrastructure.Security;

namespace Sintek.Mail.Infrastructure.Tests.Security;

/// <summary>
/// Cobre os requisitos de segurança da seção 3.2 da especificação: impedir execução de
/// conteúdo ativo e bloquear imagens remotas por padrão.
/// </summary>
public class MessageHtmlSanitizerTests
{
    private readonly MessageHtmlSanitizer _sanitizer = new();

    [Theory]
    [InlineData("<script>alert('x')</script>")]
    [InlineData("<img src=\"cid:logo\" onerror=\"alert('x')\">")]
    [InlineData("<a href=\"javascript:alert('x')\">clique</a>")]
    [InlineData("<iframe src=\"https://mal.example\"></iframe>")]
    [InlineData("<object data=\"mal.swf\"></object>")]
    [InlineData("<embed src=\"mal.swf\">")]
    [InlineData("<body onload=\"alert('x')\">texto</body>")]
    [InlineData("<a href=\"vbscript:msgbox('x')\">clique</a>")]
    public void Sanitize_RemoveConteudoAtivo(string html)
    {
        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().NotContain("<script");
        result.SanitizedHtml.Should().NotContain("alert(");
        result.SanitizedHtml.Should().NotContain("onerror");
        result.SanitizedHtml.Should().NotContain("onload");
        result.SanitizedHtml.Should().NotContain("javascript:");
        result.SanitizedHtml.Should().NotContain("vbscript:");
        result.SanitizedHtml.Should().NotContain("<iframe");
        result.SanitizedHtml.Should().NotContain("<object");
        result.SanitizedHtml.Should().NotContain("<embed");
    }

    [Fact]
    public void Sanitize_BloqueiaImagensRemotasPorPadrao()
    {
        // Carregar a imagem confirmaria ao remetente que a mensagem foi aberta, quando e
        // de qual IP — o rastreamento por pixel que a especificação manda impedir.
        const string html = "<p>Olá</p><img src=\"https://rastreador.example/pixel.gif\" width=\"1\">";

        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().NotContain("rastreador.example");
        result.HasRemoteContent.Should().BeTrue();
        result.RemovedRemoteReferences.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sanitize_PermiteImagensRemotas_QuandoOUsuarioAutoriza()
    {
        const string html = "<img src=\"https://cdn.example/logo.png\">";

        var result = _sanitizer.Sanitize(html, allowRemoteContent: true);

        result.SanitizedHtml.Should().Contain("cdn.example");
        result.HasRemoteContent.Should().BeTrue();
    }

    [Fact]
    public void Sanitize_PreservaAnexosEmbutidos()
    {
        // 'cid:' aponta para um anexo já no disco local: não gera tráfego e é seguro.
        const string html = "<img src=\"cid:logotipo@sintek\">";

        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().Contain("cid:logotipo@sintek");
    }

    [Fact]
    public void Sanitize_RemoveDataUri_MesmoComConteudoRemotoPermitido()
    {
        // Um SVG em data URI pode carregar script; o ganho de exibi-lo não compensa.
        const string html = "<img src=\"data:image/svg+xml;base64,PHN2Zz48c2NyaXB0Lz48L3N2Zz4=\">";

        var result = _sanitizer.Sanitize(html, allowRemoteContent: true);

        result.SanitizedHtml.Should().NotContain("data:image/svg");
    }

    [Fact]
    public void Sanitize_PreservaTextoEFormatacaoLegitimos()
    {
        const string html =
            "<p><strong>Prezado cliente</strong>,</p><p>Segue o <em>orçamento</em>.</p>" +
            "<ul><li>Item 1</li></ul><table><tr><td>Valor</td></tr></table>";

        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().Contain("Prezado cliente");
        result.SanitizedHtml.Should().Contain("<strong>");
        result.SanitizedHtml.Should().Contain("orçamento");
        result.SanitizedHtml.Should().Contain("<li>");
    }

    [Fact]
    public void Sanitize_PreservaLinksMailtoEHttps()
    {
        const string html = "<a href=\"mailto:contato@sintek.com.br\">escreva</a>";

        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().Contain("mailto:contato@sintek.com.br");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_TrataCorpoVazio(string? html)
    {
        var result = _sanitizer.Sanitize(html);

        result.SanitizedHtml.Should().BeEmpty();
        result.HasRemoteContent.Should().BeFalse();
    }

    [Fact]
    public void PlainTextToHtml_EscapaAntesDeInserirQuebras()
    {
        // Se as quebras fossem inseridas antes do escape, um corpo contendo o texto
        // literal "<br>" viraria uma quebra real — e "<script>" viraria script.
        var result = _sanitizer.PlainTextToHtml("linha 1\nlinha <script>alert(1)</script> 2");

        result.Should().Contain("&lt;script&gt;");
        result.Should().NotContain("<script>");
        result.Should().Contain("<br />");
    }

    [Fact]
    public void PlainTextToHtml_NormalizaQuebrasDeLinhaDoWindows()
    {
        var result = _sanitizer.PlainTextToHtml("a\r\nb\rc\nd");

        // Quatro linhas, três quebras: \r\n não pode virar duas.
        result.Split("<br />").Should().HaveCount(4);
    }
}
