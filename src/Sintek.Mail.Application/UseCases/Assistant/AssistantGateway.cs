using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sintek.Mail.Application.Abstractions.Assistant;
using Sintek.Mail.Application.Abstractions.Persistence;
using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Application.UseCases.Assistant;

/// <summary>Por que um pedido de IA não foi atendido.</summary>
public enum AssistantRefusal
{
    /// <summary>Não houve recusa.</summary>
    None = 0,

    /// <summary>Nenhum provedor está configurado e disponível.</summary>
    NoProviderAvailable = 1,

    /// <summary>
    /// O único provedor disponível processa em nuvem e o Diretório de Domínio da conta
    /// não autoriza.
    /// </summary>
    CloudNotConsented = 2,

    /// <summary>O provedor falhou ao processar.</summary>
    ProviderFailed = 3,
}

/// <summary>
/// Resultado de um pedido ao assistente.
/// </summary>
/// <param name="Succeeded">Se houve resposta.</param>
/// <param name="Text">Texto gerado.</param>
/// <param name="Refusal">Motivo, quando não houve resposta.</param>
/// <param name="UserMessage">Explicação redigida para o usuário.</param>
/// <param name="ProviderId">Provedor que atendeu, quando houve.</param>
public readonly record struct AssistantResult(
    bool Succeeded,
    string Text,
    AssistantRefusal Refusal,
    string? UserMessage,
    string? ProviderId);

/// <summary>
/// Porta única de entrada da assistência por IA: escolhe o provedor, aplica o
/// consentimento do Diretório de Domínio e registra os envios externos em auditoria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhum recurso de IA chama um provedor diretamente.</b> Tudo passa por aqui, pelo
/// mesmo motivo que toda movimentação passa pelo <c>MoveMessageHandler</c>: uma segunda
/// versão da política divergiria da primeira, e a divergência sempre termina com alguém
/// mandando para a nuvem o que o diretório proíbe.
/// </para>
/// <para>
/// A ordem de escolha é deliberada: <b>local primeiro, sempre</b>. O provedor em nuvem só
/// entra quando não há local disponível <i>e</i> o diretório consentiu. Preferir a nuvem
/// por ser melhor transformaria o consentimento em formalidade — o usuário concordou que
/// <i>pode</i>, não que <i>deve</i>.
/// </para>
/// </remarks>
public sealed class AssistantGateway
{
    private readonly IReadOnlyList<IAssistantProvider> _providers;
    private readonly IAccountRepository _accounts;
    private readonly IDomainDirectoryRepository _directories;
    private readonly IAuditLogRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AssistantGateway> _logger;

    public AssistantGateway(
        IEnumerable<IAssistantProvider> providers,
        IAccountRepository accounts,
        IDomainDirectoryRepository directories,
        IAuditLogRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<AssistantGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToList();
        _accounts = accounts;
        _directories = directories;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Se há algum provedor utilizável para a conta — o que a interface consulta para
    /// decidir se mostra os botões de IA.
    /// </summary>
    public async Task<bool> IsAvailableForAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var (provider, _) = await ResolveProviderAsync(accountId, cancellationToken).ConfigureAwait(false);
        return provider is not null;
    }

    /// <summary>Executa um pedido, aplicando a política antes de qualquer envio.</summary>
    public async Task<AssistantResult> RequestAsync(
        Guid accountId,
        AssistantRequest request,
        Guid? messageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (provider, refusal) = await ResolveProviderAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        if (provider is null)
        {
            if (refusal == AssistantRefusal.CloudNotConsented)
            {
                await RecordAsync(
                    AuditEventType.AssistantBlockedByConsent,
                    "Pedido de assistência recusado: o Diretório de Domínio não autoriza " +
                    "processamento em nuvem.",
                    AuditSeverity.Information,
                    accountId,
                    messageId,
                    detailsJson: null,
                    cancellationToken).ConfigureAwait(false);
            }

            return new AssistantResult(false, string.Empty, refusal, MessageFor(refusal), null);
        }

        // O registro vem ANTES do envio. Registrar depois perderia exatamente o caso que
        // importa: a chamada que saiu e falhou no meio do caminho.
        if (provider.Locality == AssistantLocality.Cloud)
        {
            await RecordAsync(
                AuditEventType.AssistantCloudRequest,
                $"Conteúdo enviado ao provedor de IA em nuvem '{provider.DisplayName}'.",
                AuditSeverity.Warning,
                accountId,
                messageId,
                // Só identificadores e tamanho: o conteúdo em si nunca entra na auditoria,
                // pela mesma regra que vale para o resto do produto.
                JsonSerializer.Serialize(new
                {
                    provider = provider.Id,
                    task = request.Task.ToString(),
                    contentLength = request.Content.Length,
                }),
                cancellationToken).ConfigureAwait(false);
        }

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            _logger.LogWarning(
                "O provedor de assistência {ProviderId} não conseguiu atender: {Reason}",
                provider.Id, response.ErrorMessage);

            return new AssistantResult(
                false, string.Empty, AssistantRefusal.ProviderFailed,
                response.ErrorMessage ?? MessageFor(AssistantRefusal.ProviderFailed), provider.Id);
        }

        return new AssistantResult(true, response.Text, AssistantRefusal.None, null, provider.Id);
    }

    /// <summary>
    /// Escolhe o provedor: local disponível primeiro; nuvem só com consentimento do
    /// Diretório de Domínio da conta.
    /// </summary>
    private async Task<(IAssistantProvider? Provider, AssistantRefusal Refusal)> ResolveProviderAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        foreach (var local in _providers.Where(p => p.Locality == AssistantLocality.Local))
        {
            if (await local.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return (local, AssistantRefusal.None);
            }
        }

        var cloudProviders = new List<IAssistantProvider>();

        foreach (var cloud in _providers.Where(p => p.Locality == AssistantLocality.Cloud))
        {
            if (await cloud.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                cloudProviders.Add(cloud);
            }
        }

        if (cloudProviders.Count == 0)
        {
            return (null, AssistantRefusal.NoProviderAvailable);
        }

        return await HasCloudConsentAsync(accountId, cancellationToken).ConfigureAwait(false)
            ? (cloudProviders[0], AssistantRefusal.None)
            : (null, AssistantRefusal.CloudNotConsented);
    }

    /// <summary>
    /// Se o Diretório de Domínio da conta autoriza processamento em nuvem.
    /// </summary>
    /// <remarks>
    /// Conta sem diretório resolvível não é autorizada. Na dúvida, o conteúdo fica na
    /// máquina — o custo dos dois erros não é simétrico.
    /// </remarks>
    private async Task<bool> HasCloudConsentAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return false;
        }

        var directory = await _directories
            .GetByIdAsync(account.DomainDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        return directory?.AllowsCloudAssistant == true;
    }

    private async Task RecordAsync(
        AuditEventType eventType,
        string description,
        AuditSeverity severity,
        Guid accountId,
        Guid? messageId,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(AuditLogEntry.Record(
            eventType,
            description,
            _timeProvider.GetUtcNow(),
            severity: severity,
            entityType: messageId is null ? nameof(Account) : nameof(Message),
            entityId: messageId ?? accountId,
            accountId: accountId,
            detailsJson: detailsJson), cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string MessageFor(AssistantRefusal refusal) => refusal switch
    {
        AssistantRefusal.NoProviderAvailable =>
            "Nenhum assistente de IA está configurado. Configure um modelo local nas preferências.",
        AssistantRefusal.CloudNotConsented =>
            "Este Diretório de Domínio não autoriza o envio de conteúdo a serviços de IA em nuvem. " +
            "Autorize nas configurações do diretório, se for o caso, ou instale o modelo local.",
        AssistantRefusal.ProviderFailed =>
            "O assistente não conseguiu processar o pedido. Tente de novo.",
        _ => string.Empty,
    };
}
