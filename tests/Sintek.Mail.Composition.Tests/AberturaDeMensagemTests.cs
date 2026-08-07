using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sintek.Mail.Application.Abstractions.Mail;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Application.Sync;
using Sintek.Mail.Application.UseCases.Messages;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.ValueObjects;
using Sintek.Mail.Persistence;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Composition.Tests;

/// <summary>
/// Percorre o caminho que o usuário percorre: sincronizar, clicar, ler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Este é o teste que faltava desde o começo.</b> A suíte tinha 1035 casos e nenhum
/// atravessava o fluxo inteiro contra o banco real: os de Aplicação montam repositórios
/// dublês, os de Persistência exercitam consultas isoladas, e os de Composição só verificam o
/// registro do contêiner. Cada camada estava provada, e a costura entre elas não.
/// </para>
/// <para>
/// Foi exatamente na costura que os defeitos moraram — o filho que nasce <c>Modified</c>
/// (D-047), o corpo que não persiste, o escopo envenenado. Corrigir por hipótese e pedir ao
/// usuário que validasse falhou repetidamente; reproduzir contra o banco real acertou de
/// primeira. Este arquivo é a aplicação desse método ao fluxo todo.
/// </para>
/// <para>
/// Real: banco SQLCipher em arquivo, migrações, contêiner de injeção com os mesmos registros
/// da aplicação, escopo por operação, casos de uso e ViewModels de verdade. Dublê: só a rede
/// (<see cref="IImapClient"/>) e o cofre de credenciais, que falam com servidor e com o
/// Windows.
/// </para>
/// </remarks>
public sealed class AberturaDeMensagemTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
    private const string EncryptionKey = "chave-de-teste-da-abertura";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"sintek-abertura-{Guid.CreateVersion7():N}.db");

    private readonly IImapClient _imap = Substitute.For<IImapClient>();

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<ICredentialStore>());
        services.AddSingleton(Substitute.For<IAttachmentStore>());

        services.AddSintekMailCore(
            new ConfigurationBuilder().Build(),
            _ => new DatabaseOptions(_databasePath, EncryptionKey));

        // A rede é o único dublê do caminho. Tudo o mais é o que roda na máquina do usuário.
        services.AddScoped(_ => _imap);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MailDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    /// <summary>
    /// Clicar numa mensagem baixa o corpo, <b>grava</b> e o entrega ao painel.
    /// </summary>
    /// <remarks>
    /// A gravação é metade do teste, e é a metade que faltava. Sem persistir, o painel até
    /// mostra o corpo na sessão em que o download aconteceu — a instância em memória é a mesma
    /// —, mas <c>DownloadedAt</c> fica nulo, todo clique seguinte refaz o download pela rede, e
    /// ao reabrir o aplicativo não há corpo nenhum.
    /// </remarks>
    [Fact]
    public async Task ClicarNaMensagem_BaixaOCorpoEOEntregaAoPainel()
    {
        var messageId = await ArrangeSyncedMessageAsync();

        ArrangeServerBody(uid: 42, html: "<p>Bom dia</p>", text: "Bom dia");

        var reading = _provider.GetRequiredService<ReadingPaneViewModel>();
        await reading.LoadMessageAsync(messageId);

        reading.DownloadError.Should().BeEmpty("não deveria haver erro nenhum neste caminho");
        reading.SanitizedHtml.Should().Contain("Bom dia", "é o que o WebView2 recebe");

        // A prova da persistência: um contexto novo, que não viu nada do que aconteceu acima.
        await using var conferencia = _provider.CreateAsyncScope();
        var corpo = await conferencia.ServiceProvider.GetRequiredService<MailDbContext>()
            .MessageBodies.FirstOrDefaultAsync(b => b.MessageId == messageId);

        corpo.Should().NotBeNull("sem gravar, reabrir o aplicativo perde o corpo");
        corpo!.DownloadedAt.Should().NotBeNull();
    }

    /// <summary>
    /// O segundo clique não vai à rede.
    /// </summary>
    /// <remarks>
    /// É o curto-circuito de idempotência, e ele só passa a valer quando
    /// <c>DownloadedAt</c> é gravado de fato. Enquanto a gravação falhava, todo clique refazia
    /// o <c>FETCH</c> — e falhava igual.
    /// </remarks>
    [Fact]
    public async Task ClicarDuasVezes_NaoRebaixaOCorpo()
    {
        var messageId = await ArrangeSyncedMessageAsync();
        ArrangeServerBody(uid: 42, html: "<p>Bom dia</p>", text: "Bom dia");

        var reading = _provider.GetRequiredService<ReadingPaneViewModel>();

        await reading.LoadMessageAsync(messageId);
        await reading.LoadMessageAsync(messageId);

        await _imap.Received(1)
            .FetchBodyAsync("INBOX", 42, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sincronizar duas vezes não duplica mensagem nem enfileira movimentação à toa.
    /// </summary>
    /// <remarks>
    /// A fila do usuário acumulou dezoito movimentações que ele não pediu. Se a segunda
    /// sincronização tratasse as mesmas mensagens como novas, cada volta reclassificaria tudo
    /// e enfileiraria de novo — e a fila cresceria sozinha até o fim dos tempos.
    /// </remarks>
    [Fact]
    public async Task SincronizarDuasVezes_NaoDuplicaNemEnfileiraDeNovo()
    {
        var accountId = await ArrangeAccountAsync();

        ArrangeServerFolders();
        ArrangeServerHeaders(uid: 42);

        await SyncAsync(accountId);
        await SyncAsync(accountId);

        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MailDbContext>();

        (await context.Messages.CountAsync()).Should().Be(1, "a mesma mensagem, uma linha só");
        (await context.OutboxOperations.CountAsync()).Should().Be(
            0, "nada foi movido pelo usuário nem pela regra de domínio");
    }

    /// <summary>
    /// Movimentação para pasta não espelhada não vai ao servidor — e não trava a fila.
    /// </summary>
    /// <remarks>
    /// <para>
    /// As pastas padrão nascem com <c>RemotePath</c> adivinhado, e só "INBOX" é padronizado
    /// pela RFC 3501. Num Gmail — "[Gmail]/Trash", "[Gmail]/Sent Mail" — nenhum dos chutes
    /// casa, e o espelhamento desliga a sincronização delas.
    /// </para>
    /// <para>
    /// Emitir o MOVE para esse caminho rendia <c>FolderNotFoundException</c>. E como a fila é
    /// sequencial e para na primeira falha, a operação travava todas as seguintes: o usuário
    /// viu dezoito movimentações presas com "The requested folder could not be found", e a
    /// exclusão nunca chegando ao servidor (D-050).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExcluirMensagem_ComLixeiraNaoEspelhada_NaoTravaAFila()
    {
        var messageId = await ArrangeSyncedMessageAsync();

        // A Lixeira existe, mas o servidor nunca anunciou o caminho dela: o espelhamento a
        // desligou. É o estado real de toda conta Gmail deste projeto.
        var trashId = await ArrangeDisabledTrashAsync(messageId);

        _imap.IsConnected.Returns(true);
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MoveMessageHandler>()
                .HandleAsync(new MoveMessageCommand(messageId, trashId, UserConfirmed: true));
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var account = await scope.ServiceProvider.GetRequiredService<MailDbContext>()
                .Accounts.FirstAsync();

            await scope.ServiceProvider.GetRequiredService<IOutboxDrainer>().DrainAsync(account);
        }

        // Nenhum comando de servidor: não há para onde mover num servidor que não conhece a pasta.
        await _imap.DidNotReceive().MoveAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());

        await using var conferencia = _provider.CreateAsyncScope();
        var pendentes = await conferencia.ServiceProvider.GetRequiredService<MailDbContext>()
            .OutboxOperations.CountAsync(o => o.Status != OutboxOperationStatus.Completed);

        pendentes.Should().Be(0, "a operação precisa concluir, não ficar travando as seguintes");
    }

    private async Task<Guid> ArrangeDisabledTrashAsync(Guid messageId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MailDbContext>();

        var message = await context.Messages.FirstAsync(m => m.Id == messageId);

        var trash = Folder.Create(
            message.AccountId, "Lixeira", FolderType.Trash, Now, remotePath: "Trash");

        // É o que FolderMirrorService faz quando o caminho não veio na listagem do servidor.
        trash.ConfigureSync(syncEnabled: false, isSubscribed: false, Now);

        context.Folders.Add(trash);
        await context.SaveChangesAsync();

        return trash.Id;
    }

    // ----- Arranjo -------------------------------------------------------------------

    private async Task<Guid> ArrangeAccountAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MailDbContext>();

        var directory = DomainDirectory.Create(EmailDomain.Parse("sintek.com.br"), Now, "Sintek");
        var account = Account.Create(
            directory.Id, EmailAddress.Parse("contato@sintek.com.br"), "Contato", Now);

        account.ConfigureServers(
            "imap.sintek.com.br", 993, SecureSocketMode.SslOnConnect,
            "smtp.sintek.com.br", 587, SecureSocketMode.StartTls, Now);

        directory.AttachAccount(account, Now);

        context.DomainDirectories.Add(directory);
        context.Folders.Add(
            Folder.Create(account.Id, "INBOX", FolderType.Inbox, Now, remotePath: "INBOX"));

        await context.SaveChangesAsync();

        return account.Id;
    }

    /// <summary>Conta com uma mensagem já sincronizada, sem corpo — o estado do clique.</summary>
    private async Task<Guid> ArrangeSyncedMessageAsync()
    {
        var accountId = await ArrangeAccountAsync();

        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MailDbContext>();

        var folder = await context.Folders.FirstAsync(f => f.AccountId == accountId);
        var message = Message.Create(accountId, folder.Id, "<42@servidor>", Now, Now, Now);
        message.SetRemoteIdentity(42, null, Now);
        message.MarkSynced(Now);

        context.Messages.Add(message);
        await context.SaveChangesAsync();

        return message.Id;
    }

    private void ArrangeServerBody(long uid, string html, string text)
    {
        _imap.IsConnected.Returns(true);
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());

        _imap.FetchBodyAsync("INBOX", uid, Arg.Any<CancellationToken>())
            .Returns(new FetchedBody { HtmlBody = html, TextBody = text });
    }

    private void ArrangeServerFolders()
    {
        _imap.IsConnected.Returns(true);
        _imap.ConnectAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success());

        _imap.ListFoldersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new RemoteFolder("INBOX", "INBOX", '/', FolderType.Inbox, true) });
    }

    private void ArrangeServerHeaders(long uid)
    {
        _imap.OpenFolderAsync("INBOX", Arg.Any<CancellationToken>())
            .Returns(new FolderSyncState(1, null, uid + 1, 1, 0));

        // O motor busca em laço até vir vazio: o primeiro lote traz o cabeçalho, o seguinte
        // nada. Devolver sempre o mesmo faria o teste rodar para sempre.
        var entregue = false;

        _imap.FetchHeadersAsync("INBOX", Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (entregue)
                {
                    return Array.Empty<FetchedMessage>();
                }

                entregue = true;
                return new[]
                {
                    new FetchedMessage
                    {
                        Uid = uid,
                        MessageId = $"<{uid}@servidor>",
                        Subject = "Assunto",
                        FromAddress = "cliente@externo.com",
                        SentAt = Now,
                        ReceivedAt = Now,
                    },
                };
            });
    }

    private async Task SyncAsync(Guid accountId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SyncAccountHandler>()
            .HandleAsync(accountId);
    }
}
