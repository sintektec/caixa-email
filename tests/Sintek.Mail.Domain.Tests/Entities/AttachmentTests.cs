using Sintek.Mail.Domain.Entities;

namespace Sintek.Mail.Domain.Tests.Entities;

public class AttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static Attachment Create(string fileName)
        => Attachment.Create(Guid.CreateVersion7(), fileName, "application/octet-stream", 1024, "2", Now);

    [Theory]
    [InlineData("fatura.exe")]
    [InlineData("boleto.scr")]
    [InlineData("script.ps1")]
    [InlineData("macro.docm")]
    [InlineData("atalho.lnk")]
    [InlineData("instalador.msi")]
    [InlineData("imagem.iso")]
    [InlineData("FATURA.EXE")]
    public void Create_MarcaComoSuspeito_ExtensoesExecutaveisOuInterpretaveis(string fileName)
    {
        Create(fileName).IsSuspicious.Should().BeTrue();
    }

    [Theory]
    [InlineData("contrato.pdf")]
    [InlineData("planilha.xlsx")]
    [InlineData("foto.jpg")]
    [InlineData("documento.docx")]
    [InlineData("relatorio.txt")]
    public void Create_NaoMarcaComoSuspeito_ExtensoesInofensivas(string fileName)
    {
        Create(fileName).IsSuspicious.Should().BeFalse();
    }

    [Theory]
    [InlineData("../../../Startup/backdoor.exe", "backdoor.exe")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("subpasta/relatorio.pdf", "relatorio.pdf")]
    public void Create_RemoveComponentesDeCaminho(string fileName, string expected)
    {
        // Um servidor hostil pode anunciar um anexo com caminho relativo. Sem esta
        // normalização, salvar o anexo escreveria fora da pasta prevista — em Inicializar,
        // por exemplo.
        Create(fileName).FileName.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Create_UsaNomePadrao_QuandoONomeEhInutilizavel(string fileName)
    {
        Create(fileName).FileName.Should().Be("anexo.bin");
    }

    [Fact]
    public void Create_SubstituiCaracteresInvalidosDeNomeDeArquivo()
    {
        var attachment = Create("rela\u0000torio:final.pdf");

        attachment.FileName.Should().NotContain("\u0000");
        attachment.FileName.Should().EndWith(".pdf");
    }

    [Fact]
    public void MarkDownloaded_RegistraOCaminhoLocal()
    {
        var attachment = Create("contrato.pdf");

        attachment.MarkDownloaded(@"C:\Users\ana\AppData\Sintek.Mail\Attachments\a\contrato.pdf", Now);

        attachment.IsDownloaded.Should().BeTrue();
        attachment.StoragePath.Should().NotBeNullOrWhiteSpace();
    }
}
