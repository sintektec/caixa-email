using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Services;
using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Application.Abstractions.Persistence;

/// <summary>Acesso aos Diretórios de Domínio.</summary>
public interface IDomainDirectoryRepository
{
    /// <summary>Carrega um diretório com seus domínios adicionais.</summary>
    Task<DomainDirectory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca um diretório pelo domínio exato.</summary>
    Task<DomainDirectory?> GetByDomainAsync(EmailDomain domain, CancellationToken cancellationToken = default);

    /// <summary>Lista todos os diretórios, para montar a árvore de navegação.</summary>
    Task<IReadOnlyList<DomainDirectory>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Registra um novo diretório.</summary>
    Task AddAsync(DomainDirectory directory, CancellationToken cancellationToken = default);

    /// <summary>Remove um diretório.</summary>
    void Remove(DomainDirectory directory);
}

/// <summary>Acesso às contas de e-mail.</summary>
public interface IAccountRepository
{
    /// <summary>Carrega uma conta.</summary>
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca uma conta pelo endereço.</summary>
    Task<Account?> GetByAddressAsync(EmailAddress address, CancellationToken cancellationToken = default);

    /// <summary>Lista as contas de um diretório.</summary>
    Task<IReadOnlyList<Account>> ListByDomainAsync(Guid domainDirectoryId, CancellationToken cancellationToken = default);

    /// <summary>Lista todas as contas ativas.</summary>
    Task<IReadOnlyList<Account>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Registra uma nova conta.</summary>
    Task AddAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Remove uma conta.</summary>
    void Remove(Account account);
}

/// <summary>Acesso às pastas.</summary>
public interface IFolderRepository
{
    /// <summary>Carrega uma pasta.</summary>
    Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Carrega todas as pastas de uma conta em uma única consulta.
    /// </summary>
    /// <remarks>
    /// A resolução de herança de restrição percorre a árvore inteira. Carregá-la de uma
    /// vez evita a sequência de consultas que uma travessia nó a nó produziria.
    /// </remarks>
    Task<IReadOnlyList<Folder>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Busca a pasta padrão de um tipo dentro da conta.</summary>
    Task<Folder?> GetByTypeAsync(Guid accountId, FolderType folderType, CancellationToken cancellationToken = default);

    /// <summary>Registra uma nova pasta.</summary>
    Task AddAsync(Folder folder, CancellationToken cancellationToken = default);

    /// <summary>Remove uma pasta.</summary>
    void Remove(Folder folder);

    /// <summary>Conta quantas mensagens não excluídas a pasta tem.</summary>
    Task<int> CountMessagesAsync(Guid folderId, CancellationToken cancellationToken = default);
}

/// <summary>Acesso às mensagens.</summary>
public interface IMessageRepository
{
    /// <summary>Carrega uma mensagem sem o corpo.</summary>
    Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Carrega uma mensagem com participantes e anexos.</summary>
    Task<Message?> GetWithParticipantsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Projeta apenas os participantes de uma mensagem.
    /// </summary>
    /// <remarks>
    /// É o caminho usado pela verificação de regra de domínio em arrastar e soltar:
    /// materializar a mensagem inteira só para ler os domínios dos participantes seria
    /// desperdício em uma operação que acontece a cada gesto do usuário.
    /// </remarks>
    Task<IReadOnlyList<MessageParticipant>> GetParticipantsAsync(
        Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Projeta os participantes de várias mensagens de uma vez.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageParticipant>>> GetParticipantsAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default);

    /// <summary>Lista os identificadores das mensagens de uma pasta.</summary>
    Task<IReadOnlyList<Guid>> ListIdsByFolderAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>Busca a mensagem de uma pasta pelo UID do servidor.</summary>
    Task<Message?> GetByUidAsync(Guid folderId, long uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca uma mensagem da conta pelo <c>Message-ID</c> da RFC 5322.
    /// </summary>
    /// <remarks>
    /// É o caminho de reconciliação quando o UID não serve: depois de um MOVE em servidor
    /// sem UIDPLUS, a mensagem reaparece na pasta de destino com UID novo e desconhecido, e
    /// o <c>Message-ID</c> é o único identificador que atravessou a operação.
    /// </remarks>
    Task<Message?> GetByMessageIdAsync(
        Guid accountId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista os UIDs conhecidos de uma pasta, do menor para o maior.
    /// </summary>
    /// <remarks>
    /// Serve à detecção de mensagens apagadas fora deste cliente: o que existe localmente e
    /// sumiu da listagem do servidor foi removido por outra sessão.
    /// </remarks>
    Task<IReadOnlyList<long>> ListUidsByFolderAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>Conta as mensagens não lidas de uma pasta.</summary>
    Task<int> CountUnreadAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista os correspondentes conhecidos da conta: nome exibido e domínio de quem o
    /// usuário já leu mensagens.
    /// </summary>
    /// <remarks>
    /// Alimenta a detecção de remetente disfarçado. Só entram mensagens lidas e não marcadas
    /// como spam — é a leitura que indica que o usuário reconhece aquele remetente. Incluir
    /// tudo faria o primeiro golpe "ensinar" o nome falso como legítimo.
    /// </remarks>
    Task<IReadOnlyList<KnownCorrespondent>> ListKnownCorrespondentsAsync(
        Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista as mensagens cujo conteúdo baixado pode ser descartado — as que ainda existem
    /// no servidor e cujo download é anterior ao corte.
    /// </summary>
    /// <remarks>
    /// Mensagem sem contrapartida no servidor fica de fora: rascunho, item da Caixa de
    /// Saída e mensagem sem UID não podem ser baixados de novo, e para elas o "cache" é o
    /// original.
    /// </remarks>
    Task<IReadOnlyList<Message>> ListCachedContentAsync(
        DateTimeOffset downloadedBefore, CancellationToken cancellationToken = default);

    /// <summary>Lista as mensagens presentes em pastas restritas por um diretório.</summary>
    Task<IReadOnlyList<Message>> ListInRestrictedFoldersAsync(
        Guid domainDirectoryId, CancellationToken cancellationToken = default);

    /// <summary>Registra uma nova mensagem.</summary>
    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>Remove definitivamente uma mensagem.</summary>
    void Remove(Message message);
}

/// <summary>Acesso à fila de saída.</summary>
public interface IOutboxRepository
{
    /// <summary>Enfileira uma operação.</summary>
    Task AddAsync(OutboxOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Lista as operações prontas para execução, em ordem de sequência.</summary>
    Task<IReadOnlyList<OutboxOperation>> ListReadyAsync(
        Guid accountId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lista as operações pendentes de uma conta, para exibir a fila ao usuário.</summary>
    Task<IReadOnlyList<OutboxOperation>> ListPendingAsync(
        Guid? accountId, CancellationToken cancellationToken = default);

    /// <summary>Carrega uma operação.</summary>
    Task<OutboxOperation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserva o próximo número de sequência da conta.
    /// </summary>
    /// <remarks>
    /// A sequência precisa ser monotônica por conta: é ela que garante que "mover para
    /// Arquivados" seja aplicado antes de "marcar como lida", e não o inverso.
    /// </remarks>
    Task<long> NextSequenceAsync(Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>Acesso às regras automáticas.</summary>
public interface IRuleRepository
{
    /// <summary>Carrega uma regra com condições e ações.</summary>
    Task<Rule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista todas as regras, para a tela de gestão.</summary>
    Task<IReadOnlyList<Rule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista as regras ativas aplicáveis a uma conta, em ordem de prioridade — o que o
    /// motor avalia a cada mensagem que chega.
    /// </summary>
    Task<IReadOnlyList<Rule>> ListEnabledForAccountAsync(
        Guid accountId, Guid domainDirectoryId, CancellationToken cancellationToken = default);

    /// <summary>Registra uma regra.</summary>
    Task AddAsync(Rule rule, CancellationToken cancellationToken = default);

    /// <summary>Remove uma regra.</summary>
    void Remove(Rule rule);
}

/// <summary>Acesso às categorias e à sua aplicação em mensagens.</summary>
public interface ICategoryRepository
{
    /// <summary>Carrega uma categoria.</summary>
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista as categorias visíveis para uma conta: as globais e as dela.</summary>
    Task<IReadOnlyList<Category>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default);

    /// <summary>Registra uma categoria.</summary>
    Task AddAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Remove uma categoria.</summary>
    void Remove(Category category);

    /// <summary>Se a mensagem já tem a categoria aplicada.</summary>
    Task<bool> IsAssignedAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>Aplica uma categoria a uma mensagem.</summary>
    Task AssignAsync(MessageCategory link, CancellationToken cancellationToken = default);

    /// <summary>Retira uma categoria de uma mensagem.</summary>
    Task<bool> UnassignAsync(
        Guid messageId, Guid categoryId, CancellationToken cancellationToken = default);
}

/// <summary>Acesso aos modelos de mensagem.</summary>
public interface IMessageTemplateRepository
{
    /// <summary>Carrega um modelo.</summary>
    Task<MessageTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista os modelos visíveis para uma conta: os globais e os dela.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListAsync(
        Guid? accountId = null, CancellationToken cancellationToken = default);

    /// <summary>Registra um modelo.</summary>
    Task AddAsync(MessageTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Remove um modelo.</summary>
    void Remove(MessageTemplate template);
}

/// <summary>Acesso às listas de remetentes bloqueados e confiáveis.</summary>
public interface ISenderReputationRepository
{
    /// <summary>Carrega uma entrada.</summary>
    Task<SenderReputation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista as entradas, opcionalmente de um só tipo.</summary>
    Task<IReadOnlyList<SenderReputation>> ListAsync(
        SenderReputationKind? kind = null, CancellationToken cancellationToken = default);

    /// <summary>Registra uma entrada.</summary>
    Task AddAsync(SenderReputation entry, CancellationToken cancellationToken = default);

    /// <summary>Remove uma entrada.</summary>
    void Remove(SenderReputation entry);
}

/// <summary>Acesso às pesquisas salvas.</summary>
public interface ISavedSearchRepository
{
    /// <summary>Carrega uma pesquisa salva.</summary>
    Task<SavedSearch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca pelo nome, que é único.</summary>
    Task<SavedSearch?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lista todas, fixadas primeiro.</summary>
    Task<IReadOnlyList<SavedSearch>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Registra uma pesquisa salva.</summary>
    Task AddAsync(SavedSearch search, CancellationToken cancellationToken = default);

    /// <summary>Remove uma pesquisa salva.</summary>
    void Remove(SavedSearch search);
}

/// <summary>Registro de auditoria.</summary>
/// <remarks>
/// As implementações nunca podem gravar conteúdo de mensagem: apenas identificadores,
/// tipo do evento e o motivo da decisão.
/// </remarks>
public interface IAuditLogRepository
{
    /// <summary>Registra um evento.</summary>
    Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Lista os eventos mais recentes.</summary>
    Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
}
