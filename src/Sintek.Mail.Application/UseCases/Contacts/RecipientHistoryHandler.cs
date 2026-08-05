using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.UseCases.Contacts;

/// <summary>Um destinatário para registrar no histórico.</summary>
/// <param name="Address">Endereço.</param>
/// <param name="DisplayName">Nome exibido, quando digitado.</param>
public readonly record struct UsedRecipient(EmailAddress Address, string? DisplayName);

/// <summary>
/// Mantém o histórico de destinatários e monta as sugestões do compositor.
/// </summary>
/// <remarks>
/// <para>
/// É o equivalente do cache de autocompletar do Outlook, com a diferença de ser por conta:
/// escrever pelo endereço de um cliente não sugere os contatos de outro.
/// </para>
/// <para>
/// <b>Registrar o uso nunca pode derrubar o envio.</b> O histórico é conveniência; a
/// mensagem é o trabalho. Se a gravação falhar, o erro é registrado e o envio segue — daí
/// <see cref="RecordUseAsync"/> devolver quantas entradas gravou em vez de lançar.
/// </para>
/// </remarks>
public sealed class RecipientHistoryHandler
{
    /// <summary>
    /// Quantas entradas do histórico entram no cálculo da sugestão.
    /// </summary>
    /// <remarks>
    /// Teto generoso o bastante para que quem some da rotina ainda apareça ao ser digitado,
    /// e baixo o bastante para não carregar um histórico de anos a cada tecla.
    /// </remarks>
    public const int SuggestionCandidateLimit = 500;

    private readonly IRecipientHistoryRepository _history;
    private readonly IContactRepository _contacts;
    private readonly IAccountRepository _accounts;
    private readonly IDomainDirectoryRepository _directories;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecipientHistoryHandler> _logger;

    public RecipientHistoryHandler(
        IRecipientHistoryRepository history,
        IContactRepository contacts,
        IAccountRepository accounts,
        IDomainDirectoryRepository directories,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<RecipientHistoryHandler> logger)
    {
        _history = history;
        _contacts = contacts;
        _accounts = accounts;
        _directories = directories;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Registra que a conta escreveu para estes endereços.
    /// </summary>
    /// <returns>Quantos endereços distintos foram registrados.</returns>
    /// <remarks>
    /// Chamado no envio, não na entrega: o que registra a intenção é ter escrito. Endereço
    /// repetido na mesma mensagem — em Para e em CC, por exemplo — conta uma vez só.
    /// </remarks>
    public async Task<int> RecordUseAsync(
        Guid accountId,
        IReadOnlyCollection<UsedRecipient> recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        if (recipients.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var recorded = 0;

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var recipient in recipients)
            {
                if (!seen.Add(recipient.Address.Value))
                {
                    continue;
                }

                var entry = await _history
                    .GetByAddressAsync(accountId, recipient.Address, cancellationToken)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    entry = RecipientHistory.Create(
                        accountId, recipient.Address, now, recipient.DisplayName);

                    await _history.AddAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    entry.RegisterUse(now, recipient.DisplayName);
                }

                recorded++;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O endereço não é registrado no log: ele é conteúdo da mensagem.
            _logger.LogWarning(
                ex, "Falha ao registrar o histórico de destinatários da conta {AccountId}.", accountId);

            return 0;
        }

        return recorded;
    }

    /// <summary>
    /// Monta as sugestões para o que o usuário digitou.
    /// </summary>
    /// <remarks>
    /// Catálogo e histórico entram juntos, e o <see cref="RecipientSuggestionRanker"/>
    /// decide a ordem. Endereço fora do Diretório de Domínio da conta vem marcado, nunca
    /// omitido.
    /// </remarks>
    public async Task<IReadOnlyList<RecipientSuggestion>> SuggestAsync(
        Guid accountId,
        string? term,
        int limit = RecipientSuggestionRanker.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return [];
        }

        var directory = await _directories
            .GetByIdAsync(account.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        var history = await _history
            .ListForSuggestionAsync(accountId, SuggestionCandidateLimit, cancellationToken)
            .ConfigureAwait(false);

        var contacts = await _contacts.ListAsync(accountId, cancellationToken).ConfigureAwait(false);

        return RecipientSuggestionRanker.Rank(
            term, history, contacts, directory, _timeProvider.GetUtcNow(), limit);
    }

    /// <summary>Lista o histórico da conta, para a tela de gestão.</summary>
    public Task<IReadOnlyList<RecipientHistory>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default)
        => _history.ListAsync(accountId, cancellationToken);

    /// <summary>
    /// Apaga uma entrada do histórico.
    /// </summary>
    /// <remarks>
    /// Requisito, não refinamento: o endereço digitado errado uma vez volta a ser sugerido
    /// para sempre, e o usuário reenvia para o endereço errado — que é como um cliente de
    /// e-mail vaza mensagem para fora da empresa sem que ninguém tenha errado de novo.
    /// </remarks>
    public async Task<bool> RemoveAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await _history.GetByIdAsync(entryId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return false;
        }

        _history.Remove(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Apaga todo o histórico da conta.</summary>
    /// <returns>Quantas entradas foram apagadas.</returns>
    public async Task<int> ClearAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var entries = await _history.ListAsync(accountId, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            _history.Remove(entry);
        }

        if (entries.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return entries.Count;
    }
}
