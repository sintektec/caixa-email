using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Presentation.ViewModels;

namespace Sintek.Mail.Presentation.Tests.ViewModels;

/// <summary>
/// Cobre o painel central nos dois modos: pasta e resultados de pesquisa.
/// </summary>
public class MessageListViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.CreateVersion7();
    private static readonly Guid FolderId = Guid.CreateVersion7();

    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IFolderRepository _folders = Substitute.For<IFolderRepository>();

    private MessageListViewModel CreateViewModel() => new(
        new TestScopes().With(_messages).With(_folders).Build());

    private static Message CreateMessage(string subject)
    {
        var message = Message.Create(
            AccountId, FolderId, $"<{Guid.CreateVersion7():N}@teste.local>", Now, Now, Now);
        message.SetHeaders(subject, null, null, null, null, Now);
        return message;
    }

    [Fact]
    public async Task ExibirResultados_TrocaOTituloEEntraEmModoPesquisa()
    {
        var message = CreateMessage("Fatura de agosto");
        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        var viewModel = CreateViewModel();
        await viewModel.ShowSearchResultsAsync("Resultados de \"fatura\"", [message.Id]);

        viewModel.IsSearchResults.Should().BeTrue();
        viewModel.FolderName.Should().Be("Resultados de \"fatura\"");
        viewModel.Messages.Should().ContainSingle(m => m.Subject == "Fatura de agosto");

        // Sem pasta corrente: as ações que recarregam "a pasta atual" não podem devolver
        // o usuário a uma pasta que ele não está vendo.
        viewModel.FolderId.Should().BeNull();
    }

    [Fact]
    public async Task CarregarPasta_DepoisDeUmaPesquisa_SaiDoModoPesquisa()
    {
        var message = CreateMessage("Qualquer");
        _messages.GetByIdAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);

        var folder = Folder.Create(AccountId, "Caixa de Entrada", FolderType.Inbox, Now, remotePath: "INBOX");
        _folders.GetByIdAsync(folder.Id, Arg.Any<CancellationToken>()).Returns(folder);
        _messages.ListIdsByFolderAsync(folder.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { message.Id });

        var viewModel = CreateViewModel();
        await viewModel.ShowSearchResultsAsync("Resultados", [message.Id]);
        await viewModel.LoadFolderAsync(folder.Id);

        viewModel.IsSearchResults.Should().BeFalse();
        viewModel.FolderId.Should().Be(folder.Id);
        viewModel.FolderName.Should().Be("Caixa de Entrada");
    }

    [Fact]
    public async Task ExibirResultados_MensagemRemovidaEntreABuscaEAExibicao_EhIgnorada()
    {
        var existing = CreateMessage("Ainda existe");
        var removedId = Guid.CreateVersion7();

        _messages.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _messages.GetByIdAsync(removedId, Arg.Any<CancellationToken>()).Returns((Message?)null);

        var viewModel = CreateViewModel();
        await viewModel.ShowSearchResultsAsync("Resultados", [existing.Id, removedId]);

        viewModel.Messages.Should().ContainSingle();
    }

    // ----- Agrupamento por conversa ----------------------------------------------------

    [Fact]
    public void AgruparPorConversa_MantemAMaisRecenteEContaAsDemais()
    {
        var threadId = Guid.CreateVersion7();

        var items = new List<MessageListItemViewModel>
        {
            Item("Terceira resposta", threadId, Now),
            Item("Segunda resposta", threadId, Now.AddHours(-1)),
            Item("Mensagem original", threadId, Now.AddHours(-2)),
        };

        var collapsed = MessageListViewModel.CollapseConversations(items);

        collapsed.Should().ContainSingle();
        collapsed[0].Subject.Should().Be("Terceira resposta", "a lista já vem da mais recente para a mais antiga");
        collapsed[0].ConversationCount.Should().Be(3);
        collapsed[0].IsConversation.Should().BeTrue();
    }

    [Fact]
    public void AgruparPorConversa_MensagemSemConversa_ContinuaSozinha()
    {
        // Agrupar todas as sem-ThreadId sob uma linha esconderia mensagens que nada têm a
        // ver umas com as outras.
        var items = new List<MessageListItemViewModel>
        {
            Item("Avulsa A", null, Now),
            Item("Avulsa B", null, Now.AddHours(-1)),
        };

        var collapsed = MessageListViewModel.CollapseConversations(items);

        collapsed.Should().HaveCount(2);
        collapsed.Should().OnlyContain(i => !i.IsConversation);
    }

    [Fact]
    public void AgruparPorConversa_ConversasDiferentes_PreservamAOrdem()
    {
        var primeira = Guid.CreateVersion7();
        var segunda = Guid.CreateVersion7();

        var items = new List<MessageListItemViewModel>
        {
            Item("Recente da primeira", primeira, Now),
            Item("Recente da segunda", segunda, Now.AddMinutes(-30)),
            Item("Antiga da primeira", primeira, Now.AddHours(-2)),
        };

        var collapsed = MessageListViewModel.CollapseConversations(items);

        collapsed.Should().HaveCount(2);
        collapsed[0].Subject.Should().Be("Recente da primeira");
        collapsed[1].Subject.Should().Be("Recente da segunda");
        collapsed[0].ConversationCount.Should().Be(2);
        collapsed[1].ConversationCount.Should().Be(1);
    }

    private static MessageListItemViewModel Item(string subject, Guid? threadId, DateTimeOffset receivedAt)
        => new()
        {
            MessageId = Guid.CreateVersion7(),
            From = "Cliente",
            Subject = subject,
            Preview = string.Empty,
            ReceivedAt = receivedAt,
            ThreadId = threadId,
        };
}
