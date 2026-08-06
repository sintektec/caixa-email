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
    /// Busca em toda a conta, sem distinguir pasta. Serve para deduplicar e para relacionar
    /// cópias; <b>não</b> serve para decidir a identidade de rede de uma linha — para isso é
    /// <see cref="GetByMessageIdInFolderAsync"/>, e a diferença já custou um defeito.
    /// </remarks>
    Task<Message?> GetByMessageIdAsync(
        Guid accountId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca a mensagem de <b>uma pasta</b> pelo <c>Message-ID</c> da RFC 5322.
    /// </summary>
    /// <remarks>
    /// <para>
    /// É o caminho de reconciliação quando o UID não serve: depois de um MOVE em servidor
    /// sem UIDPLUS, a mensagem reaparece na pasta com UID novo e desconhecido, e o
    /// <c>Message-ID</c> é o único identificador que atravessou a operação.
    /// </para>
    /// <para>
    /// O recorte por pasta é o que torna a reconciliação correta, e não um detalhe de
    /// desempenho. <b>UID é identidade por pasta</b>, e o mesmo <c>Message-ID</c> existe em
    /// várias pastas ao mesmo tempo — no Gmail isso é a regra, não a exceção, porque cada
    /// rótulo é uma pasta e a mensagem da Caixa de Entrada aparece em todas elas com UID
    /// próprio. Buscar na conta inteira devolvia a linha de <i>outra</i> pasta e carimbava
    /// nela o UID desta, deixando a linha apontando para um UID que não existe onde ela mora.
    /// O corpo então nunca baixava, e o servidor respondia, com razão, que não conhecia
    /// aquele UID (D-037).
    /// </para>
    /// </remarks>
    Task<Message?> GetByMessageIdInFolderAsync(
        Guid folderId, string messageId, CancellationToken cancellationToken = default);

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

/// <summary>Acesso ao histórico de destinatários.</summary>
public interface IRecipientHistoryRepository
{
    /// <summary>Carrega uma entrada.</summary>
    Task<RecipientHistory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca a entrada de um endereço dentro da conta.</summary>
    Task<RecipientHistory?> GetByAddressAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista as entradas candidatas a sugestão, das mais usadas para as menos.
    /// </summary>
    /// <remarks>
    /// O recorte é feito no banco por uso bruto e o peso final — que combina uso e
    /// recência — é calculado em memória pelo <see cref="RecipientSuggestionRanker"/>.
    /// Reproduzir o decaimento exponencial em SQL faria a ordem depender do que o SQLite
    /// implementa, e tiraria do teste a única coisa que o usuário percebe.
    /// </remarks>
    Task<IReadOnlyList<RecipientHistory>> ListForSuggestionAsync(
        Guid accountId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lista todas as entradas da conta, para a tela de gestão.</summary>
    Task<IReadOnlyList<RecipientHistory>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Registra uma entrada.</summary>
    Task AddAsync(RecipientHistory entry, CancellationToken cancellationToken = default);

    /// <summary>Remove uma entrada.</summary>
    void Remove(RecipientHistory entry);
}

/// <summary>Acesso ao catálogo de contatos.</summary>
public interface IContactRepository
{
    /// <summary>Carrega um contato com seus endereços.</summary>
    Task<Contact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca um contato pelo identificador de origem da importação.</summary>
    Task<Contact?> GetByExternalIdAsync(
        Guid accountId, string externalId, CancellationToken cancellationToken = default);

    /// <summary>Busca o contato da conta que tem o endereço informado.</summary>
    Task<Contact?> GetByEmailAsync(
        Guid accountId, EmailAddress address, CancellationToken cancellationToken = default);

    /// <summary>Lista os contatos da conta, com endereços, em ordem de nome exibido.</summary>
    Task<IReadOnlyList<Contact>> ListAsync(
        Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Registra um contato.</summary>
    Task AddAsync(Contact contact, CancellationToken cancellationToken = default);

    /// <summary>Remove um contato.</summary>
    void Remove(Contact contact);
}

/// <summary>Acesso à agenda.</summary>
public interface ICalendarRepository
{
    /// <summary>Carrega um evento com seus participantes.</summary>
    Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca um evento da conta pelo <c>UID</c> do iCalendar.
    /// </summary>
    /// <remarks>
    /// É o caminho pelo qual uma atualização enviada pelo organizador encontra o evento que
    /// já está aqui. A chave local não serve: quem envia o convite não a conhece.
    /// </remarks>
    Task<CalendarEvent?> GetByUidAsync(
        Guid accountId, string uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca o evento importado de uma mensagem específica.
    /// </summary>
    /// <remarks>
    /// Segunda via de identidade, para quando o <c>UID</c> não serve: a biblioteca de
    /// iCalendar <b>gera um UID aleatório</b> quando o documento não traz um, e sem esta
    /// busca o mesmo convite viraria compromisso novo a cada vez que o corpo da mensagem
    /// fosse baixado de novo.
    /// </remarks>
    Task<CalendarEvent?> GetBySourceMessageAsync(
        Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista os eventos de uma conta que tocam a janela informada.
    /// </summary>
    /// <remarks>
    /// Eventos recorrentes entram sempre que começaram antes do fim da janela, porque suas
    /// ocorrências podem cair dentro dela mesmo com o primeiro encontro muito no passado.
    /// A expansão fica com o <c>ICalendarSerializer</c>.
    /// </remarks>
    Task<IReadOnlyList<CalendarEvent>> ListInRangeAsync(
        Guid? accountId, DateTimeOffset from, DateTimeOffset until,
        CancellationToken cancellationToken = default);

    /// <summary>Busca um evento pelo endereço do recurso no servidor.</summary>
    /// <remarks>
    /// O <c>href</c> é a identidade de rede, independente do <c>UID</c>: servidores nomeiam
    /// o recurso como querem, e a Google e o iCloud não usam o UID.
    /// </remarks>
    Task<CalendarEvent?> GetByRemoteHrefAsync(
        Guid remoteCalendarId, string href, CancellationToken cancellationToken = default);

    /// <summary>Lista os eventos de um calendário remoto que aguardam envio.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListPendingAsync(
        Guid remoteCalendarId, CancellationToken cancellationToken = default);

    /// <summary>Lista os endereços de recurso conhecidos de um calendário remoto.</summary>
    /// <remarks>
    /// Serve à passada completa: o que existe aqui e não apareceu na listagem do servidor
    /// foi removido lá.
    /// </remarks>
    Task<IReadOnlyList<string>> ListRemoteHrefsAsync(
        Guid remoteCalendarId, CancellationToken cancellationToken = default);

    /// <summary>Lista os compromissos em conflito, para a interface pedir decisão.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListConflictedAsync(
        Guid? accountId, CancellationToken cancellationToken = default);

    /// <summary>Registra um evento.</summary>
    Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    /// <summary>Remove um evento.</summary>
    void Remove(CalendarEvent calendarEvent);
}

/// <summary>Acesso aos calendários remotos espelhados.</summary>
public interface IRemoteCalendarRepository
{
    /// <summary>Carrega um calendário remoto.</summary>
    Task<RemoteCalendar?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Busca pelo endereço da coleção dentro da conta.</summary>
    Task<RemoteCalendar?> GetByCollectionUrlAsync(
        Guid accountId, string collectionUrl, CancellationToken cancellationToken = default);

    /// <summary>Lista os calendários remotos de uma conta.</summary>
    Task<IReadOnlyList<RemoteCalendar>> ListByAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Registra um calendário remoto.</summary>
    Task AddAsync(RemoteCalendar calendar, CancellationToken cancellationToken = default);

    /// <summary>Remove um calendário remoto.</summary>
    void Remove(RemoteCalendar calendar);
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
