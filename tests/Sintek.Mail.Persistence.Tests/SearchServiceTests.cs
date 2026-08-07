using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence;
using Sintek.Mail.Persistence.Repositories;
using Sintek.Mail.Persistence.Search;

namespace Sintek.Mail.Persistence.Tests;

/// <summary>
/// A pesquisa completa da seção 6.4 contra o banco real: índice FTS5 reconstruído com
/// external content, filtros estruturais e pesquisas salvas.
/// </summary>
/// <remarks>
/// Banco em arquivo com SQLCipher, não em memória: o que está em teste inclui os gatilhos
/// criados em SQL puro pela migração, que só existem no schema migrado de verdade.
/// </remarks>
public sealed class SearchServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string EncryptionKey = "chave-de-teste-nao-usada-em-producao";

    private string _directory = string.Empty;
    private string _databasePath = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sintek-mail-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "mail.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza é conveniência, não parte do que está sendo verificado.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SearchAsync_CorpoBaixado_EncontraPeloTexto()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Sem termo no assunto");

        // O corpo chega depois, como no download sob demanda real.
        var body = MessageBody.Create(message.Id, Now);
        body.SetContent(null, "Segue o relatório trimestral de vendas.", null, false, Now);
        context.MessageBodies.Add(body);
        await context.SaveChangesAsync();

        var found = await SearchAsync(context, new MessageSearchQuery { Body = "relatorio" });

        // A busca sem acento precisa achar "relatório": é o caso mais comum em português.
        found.Should().Contain(message.Id);
    }

    [Fact]
    public async Task SearchAsync_CorpoReprocessado_ReindexaSemDuplicar()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Assunto neutro");

        var body = MessageBody.Create(message.Id, Now);
        body.SetContent(null, "orçamento preliminar", null, false, Now);
        context.MessageBodies.Add(body);
        await context.SaveChangesAsync();

        body.SetContent(null, "contrato definitivo", null, false, Now.AddMinutes(5));
        await context.SaveChangesAsync();

        // O modo contentless antigo não tinha como fazer isto: apagar do índice um corpo
        // que vivia em outra tabela. É a razão da reconstrução com external content.
        (await SearchAsync(context, new MessageSearchQuery { Body = "orcamento" }))
            .Should().NotContain(message.Id);
        (await SearchAsync(context, new MessageSearchQuery { Body = "contrato" }))
            .Should().Contain(message.Id);
    }

    [Fact]
    public async Task SearchAsync_Participantes_EncontraPorNomeEEndereco()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Reunião de alinhamento");

        context.MessageAddresses.Add(MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("maria.souza@cliente.com.br"), Now, "Maria Souza"));
        await context.SaveChangesAsync();

        (await SearchAsync(context, new MessageSearchQuery { Text = "maria" }))
            .Should().Contain(message.Id, "o nome exibido do participante entra no índice");
        (await SearchAsync(context, new MessageSearchQuery { Text = "cliente.com.br" }))
            .Should().Contain(message.Id, "o endereço do participante entra no índice");
    }

    [Fact]
    public async Task SearchAsync_NomeDeAnexo_EncontraEAcompanhaExclusao()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Documentos do projeto");

        var attachment = Attachment.Create(
            message.Id, "planilha-orcamentaria.xlsx", "application/vnd.ms-excel", 2048, "2", Now);
        context.Attachments.Add(attachment);
        await context.SaveChangesAsync();

        (await SearchAsync(context, new MessageSearchQuery { AttachmentName = "planilha" }))
            .Should().Contain(message.Id);

        context.Attachments.Remove(attachment);
        await context.SaveChangesAsync();

        (await SearchAsync(context, new MessageSearchQuery { AttachmentName = "planilha" }))
            .Should().NotContain(message.Id, "anexo removido sai do índice");
    }

    [Fact]
    public async Task SearchAsync_Remetente_EncontraPeloNomeExibido()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(
            context, accountId, folderId, "Proposta comercial",
            from: EmailAddress.Parse("joao.silva@parceiro.com"), fromDisplayName: "João Silva");

        (await SearchAsync(context, new MessageSearchQuery { From = "joao" }))
            .Should().Contain(message.Id, "o filtro de remetente cobre o nome exibido");
        (await SearchAsync(context, new MessageSearchQuery { From = "parceiro.com" }))
            .Should().Contain(message.Id, "o filtro de remetente cobre o endereço");
    }

    [Fact]
    public async Task SearchAsync_DestinatarioECopia_NaoSeConfundem()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Alinhamento");

        context.MessageAddresses.Add(MessageAddress.Create(
            message.Id, AddressKind.To, EmailAddress.Parse("direto@cliente.com"), Now, "Destinatário Direto"));
        context.MessageAddresses.Add(MessageAddress.Create(
            message.Id, AddressKind.Cc, EmailAddress.Parse("copiado@cliente.com"), Now, "Em Cópia"));
        await context.SaveChangesAsync();

        // A distinção Para/CC é um filtro estrutural: a coluna de participantes do índice
        // mistura os campos de propósito, para a busca livre.
        (await SearchAsync(context, new MessageSearchQuery { Recipient = "direto@cliente.com" }))
            .Should().Contain(message.Id);
        (await SearchAsync(context, new MessageSearchQuery { Recipient = "copiado@cliente.com" }))
            .Should().BeEmpty();
        (await SearchAsync(context, new MessageSearchQuery { Cc = "copiado@cliente.com" }))
            .Should().Contain(message.Id);
    }

    [Fact]
    public async Task SearchAsync_TextoComFiltroEstrutural_CombinaOsDois()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var lida = await AddMessageAsync(context, accountId, folderId, "Fatura de agosto");
        lida.SetRead(true, Now);

        var naoLida = await AddMessageAsync(context, accountId, folderId, "Fatura de setembro");
        await context.SaveChangesAsync();

        var found = await SearchAsync(
            context, new MessageSearchQuery { Text = "fatura", IsRead = false });

        found.Should().Contain(naoLida.Id);
        found.Should().NotContain(lida.Id);
    }

    [Fact]
    public async Task SearchAsync_IntervaloDeDatas_NormalizaFusosDiferentes()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        // 12:00 em Brasília = 15:00 UTC. Comparar o texto cru da coluna erraria aqui:
        // "12:00-03:00" vem antes de "14:00+00:00" na ordem alfabética, mas depois na linha
        // do tempo.
        var brasilia = await AddMessageAsync(
            context, accountId, folderId, "Reunião marcada",
            receivedAt: new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.FromHours(-3)));

        var utc = await AddMessageAsync(
            context, accountId, folderId, "Reunião cancelada",
            receivedAt: new DateTimeOffset(2026, 8, 4, 14, 0, 0, TimeSpan.Zero));

        var found = await SearchAsync(context, new MessageSearchQuery
        {
            ReceivedFrom = new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero),
        });

        found.Should().Contain(brasilia.Id, "12:00-03:00 é 15:00 UTC, dentro do intervalo");
        found.Should().NotContain(utc.Id, "14:00 UTC fica antes do início do intervalo");
    }

    [Fact]
    public async Task SearchAsync_FiltroPorContaEDiretorio_RestringeOEscopo()
    {
        await using var context = await CreateMigratedContextAsync();

        var (contaSintek, pastaSintek) = await SeedAccountAsync(context);
        var (contaOutra, pastaOutra) = await SeedAccountAsync(
            context, domain: "outra.com", address: "suporte@outra.com");

        var daSintek = await AddMessageAsync(context, contaSintek, pastaSintek, "Chamado aberto");
        var daOutra = await AddMessageAsync(context, contaOutra, pastaOutra, "Chamado fechado");

        var porConta = await SearchAsync(
            context, new MessageSearchQuery { Text = "chamado", AccountId = contaSintek });
        porConta.Should().Contain(daSintek.Id);
        porConta.Should().NotContain(daOutra.Id);

        var directoryId = (await context.Accounts.SingleAsync(a => a.Id == contaOutra)).DomainDirectoryId;
        var porDiretorio = await SearchAsync(
            context, new MessageSearchQuery { DomainDirectoryId = directoryId });
        porDiretorio.Should().Contain(daOutra.Id);
        porDiretorio.Should().NotContain(daSintek.Id);
    }

    [Fact]
    public async Task SearchAsync_FiltroPorCategoria_TrazSoAsCategorizadas()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var categorizada = await AddMessageAsync(context, accountId, folderId, "Pauta da diretoria");
        var comum = await AddMessageAsync(context, accountId, folderId, "Pauta do time");

        var category = Category.Create("Diretoria", "#FF0000", Now);
        context.Categories.Add(category);
        context.MessageCategories.Add(MessageCategory.Create(categorizada.Id, category.Id, Now));
        await context.SaveChangesAsync();

        var found = await SearchAsync(
            context, new MessageSearchQuery { CategoryId = category.Id });

        found.Should().Contain(categorizada.Id);
        found.Should().NotContain(comum.Id);
    }

    [Fact]
    public async Task SearchAsync_MensagemExcluida_NaoAparece()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);

        var message = await AddMessageAsync(context, accountId, folderId, "Comprovante de pagamento");
        message.MarkDeleted(Now);
        await context.SaveChangesAsync();

        (await SearchAsync(context, new MessageSearchQuery { Text = "comprovante" }))
            .Should().BeEmpty("mensagem na lixeira não volta pela pesquisa");
    }

    [Fact]
    public async Task SearchAsync_SemNenhumCriterio_DevolveVazioSemConsultar()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);
        await AddMessageAsync(context, accountId, folderId, "Qualquer coisa");

        (await SearchAsync(context, new MessageSearchQuery()))
            .Should().BeEmpty("pesquisa vazia não pode listar a caixa inteira");
    }

    [Fact]
    public async Task SearchAsync_TermoComAspas_NaoQuebraASintaxeDoFts()
    {
        await using var context = await CreateMigratedContextAsync();
        var (accountId, folderId) = await SeedAccountAsync(context);
        await AddMessageAsync(context, accountId, folderId, "Assunto comum");

        // Operadores e aspas digitados pelo usuário são texto, nunca sintaxe FTS5.
        var act = async () => await SearchAsync(
            context, new MessageSearchQuery { Text = "\"aspas OR (parenteses" });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PesquisaSalva_PersisteERecarregaPeloNome()
    {
        await using var context = await CreateMigratedContextAsync();
        var repository = new SavedSearchRepository(context);

        var saved = SavedSearch.Create(
            "Não lidas da diretoria", """{"Text":"diretoria","IsRead":false}""", Now, isPinned: true);
        await repository.AddAsync(saved);
        await context.SaveChangesAsync();

        var reloaded = await repository.GetByNameAsync("Não lidas da diretoria");
        reloaded.Should().NotBeNull();
        reloaded!.QueryJson.Should().Contain("diretoria");
        reloaded.IsPinned.Should().BeTrue();

        (await repository.ListAsync()).Should().ContainSingle(s => s.Id == saved.Id);
    }

    private MailDbContext CreateContext()
    {
        SqlCipherConnectionFactory.EnsureProviderInitialized();

        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite(SqlCipherConnectionFactory.BuildConnectionString(
                new DatabaseOptions(_databasePath, EncryptionKey)))
            .Options;

        return new MailDbContext(options);
    }

    private async Task<MailDbContext> CreateMigratedContextAsync()
    {
        var context = CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static Task<IReadOnlyList<Guid>> SearchAsync(MailDbContext context, MessageSearchQuery query)
        => new Fts5SearchService(context).SearchAsync(query);

    private static async Task<(Guid AccountId, Guid FolderId)> SeedAccountAsync(
        MailDbContext context, string domain = "sintek.com.br", string address = "contato@sintek.com.br")
    {
        var directory = DomainDirectory.Create(EmailDomain.Parse(domain), Now);
        var account = Account.Create(directory.Id, EmailAddress.Parse(address), "Conta", Now);
        directory.AttachAccount(account, Now);

        var folder = Folder.Create(account.Id, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");

        context.DomainDirectories.Add(directory);
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        return (account.Id, folder.Id);
    }

    private static async Task<Message> AddMessageAsync(
        MailDbContext context,
        Guid accountId,
        Guid folderId,
        string subject,
        EmailAddress? from = null,
        string? fromDisplayName = null,
        DateTimeOffset? receivedAt = null)
    {
        var message = Message.Create(
            accountId, folderId, $"<{Guid.CreateVersion7():N}@teste.local>",
            receivedAt ?? Now, receivedAt ?? Now, Now);
        message.SetHeaders(subject, from, fromDisplayName, null, null, Now);

        context.Messages.Add(message);
        await context.SaveChangesAsync();

        return message;
    }
}
