using NSubstitute;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Application.Abstractions.Search;
using Sintek.Mail.Application.UseCases.Search;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.Tests.UseCases;

/// <summary>
/// Cobre o ciclo das pesquisas salvas e o contrato de serialização dos critérios.
/// </summary>
public class SavedSearchesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly ISavedSearchRepository _savedSearches = Substitute.For<ISavedSearchRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _clock = new(Now);

    private SavedSearchesHandler Handler() => new(_savedSearches, _unitOfWork, _clock);

    [Fact]
    public async Task Salvar_NomeNovo_CriaAPesquisaEPersiste()
    {
        _savedSearches.GetByNameAsync("Diretoria", Arg.Any<CancellationToken>())
            .Returns((SavedSearch?)null);

        var query = new MessageSearchQuery { Text = "pauta", IsRead = false };

        var saved = await Handler().SaveAsync("  Diretoria  ", query);

        saved.Name.Should().Be("Diretoria");
        saved.QueryJson.Should().Contain("pauta");

        await _savedSearches.Received(1).AddAsync(Arg.Any<SavedSearch>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Salvar_NomeExistente_AtualizaEmVezDeDuplicar()
    {
        var existing = SavedSearch.Create("Diretoria", """{"Text":"antigo"}""", Now.AddDays(-1));
        _savedSearches.GetByNameAsync("Diretoria", Arg.Any<CancellationToken>())
            .Returns(existing);

        var saved = await Handler().SaveAsync("Diretoria", new MessageSearchQuery { Text = "novo" });

        // O nome é a identidade visível: duas entradas homônimas na barra lateral seriam
        // indistinguíveis.
        saved.Should().BeSameAs(existing);
        saved.QueryJson.Should().Contain("novo");
        await _savedSearches.DidNotReceive().AddAsync(Arg.Any<SavedSearch>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Excluir_PesquisaExistente_RemoveEPersiste()
    {
        var saved = SavedSearch.Create("Antiga", "{}", Now);
        _savedSearches.GetByIdAsync(saved.Id, Arg.Any<CancellationToken>()).Returns(saved);

        var removed = await Handler().DeleteAsync(saved.Id);

        removed.Should().BeTrue();
        _savedSearches.Received(1).Remove(saved);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Excluir_PesquisaInexistente_NaoPersisteNada()
    {
        _savedSearches.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((SavedSearch?)null);

        var removed = await Handler().DeleteAsync(Guid.CreateVersion7());

        removed.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Serializacao_IdaEVolta_PreservaTodosOsCriterios()
    {
        var accountId = Guid.CreateVersion7();
        var original = new MessageSearchQuery
        {
            Text = "orçamento",
            From = "João",
            Recipient = "contato@sintek.com.br",
            Cc = "copiado@sintek.com.br",
            Subject = "proposta",
            Body = "valores",
            AttachmentName = "planilha",
            ReceivedFrom = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            ReceivedUntil = new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero),
            AccountId = accountId,
            IsRead = false,
            IsFlagged = true,
            HasAttachments = true,
            Importance = MessageImportance.High,
            SyncState = MessageSyncState.Synced,
            Limit = 50,
        };

        var roundTripped = SavedSearchesHandler.Deserialize(SavedSearchesHandler.Serialize(original));

        // QueryJson é um contrato de persistência: o que o usuário salvou hoje precisa
        // reabrir idêntico nas versões futuras.
        roundTripped.Should().Be(original);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ json quebrado")]
    [InlineData("null")]
    public void Deserializar_ConteudoInvalido_DevolvePesquisaVaziaSemLancar(string json)
    {
        var query = SavedSearchesHandler.Deserialize(json);

        // Uma entrada corrompida no banco não pode impedir a lista de pesquisas salvas de
        // abrir.
        query.Should().NotBeNull();
        query.HasAnyCriteria.Should().BeFalse();
    }

    [Fact]
    public void HasAnyCriteria_SemNenhumCampo_EhFalso()
    {
        new MessageSearchQuery().HasAnyCriteria.Should().BeFalse();
        new MessageSearchQuery { Text = "  " }.HasAnyCriteria.Should().BeFalse();
        new MessageSearchQuery { Text = "a" }.HasAnyCriteria.Should().BeTrue();
        new MessageSearchQuery { IsFlagged = false }.HasAnyCriteria.Should().BeTrue();
    }
}
