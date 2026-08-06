using Sintek.Mail.Domain.Common;
using Sintek.Mail.Domain.Enums;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// Pasta de uma conta — padrão (Caixa de Entrada, Enviados…) ou criada pelo usuário.
/// </summary>
/// <remarks>
/// <para>
/// A restrição por domínio existe em duas formas, e a distinção importa:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="RestrictedToDomainDirectoryId"/> é o vínculo <b>explícito</b>, definido
/// pelo usuário nesta pasta.
/// </item>
/// <item>
/// <see cref="EffectiveRestrictionDomainDirectoryId"/> é o vínculo <b>efetivo</b>, já
/// resolvido: o explícito desta pasta ou, na falta dele, o herdado do ancestral mais
/// próximo que tenha um.
/// </item>
/// </list>
/// <para>
/// O valor efetivo é desnormalizado de propósito. Toda operação de arrastar e soltar
/// precisa dele, e recalcular subindo a árvore a cada movimentação transformaria um
/// gesto de interface em uma sequência de consultas recursivas.
/// <c>FolderRestrictionResolver</c> é quem mantém esse campo coerente quando a árvore
/// muda.
/// </para>
/// </remarks>
public sealed class Folder : Entity
{
    private readonly List<Folder> _children = [];

    private Folder(
        Guid id,
        Guid accountId,
        string name,
        FolderType folderType,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        AccountId = accountId;
        Name = name;
        DisplayName = name;
        FolderType = folderType;
    }

    private Folder()
    {
    }

    /// <summary>Conta dona da pasta.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Conta dona da pasta.</summary>
    public Account? Account { get; private set; }

    /// <summary>Pasta-mãe. Nulo quando a pasta está na raiz da conta.</summary>
    public Guid? ParentFolderId { get; private set; }

    /// <summary>Pasta-mãe.</summary>
    public Folder? ParentFolder { get; private set; }

    /// <summary>Subpastas.</summary>
    public IReadOnlyCollection<Folder> Children => _children;

    /// <summary>Nome da pasta no servidor.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Nome exibido, que pode ser localizado (por exemplo, "Caixa de Entrada").</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Papel da pasta.</summary>
    public FolderType FolderType { get; private set; }

    /// <summary>
    /// Caminho completo no servidor IMAP (por exemplo, <c>INBOX/Clientes/2026</c>).
    /// Vazio nas pastas puramente locais, como a de pendências.
    /// </summary>
    public string RemotePath { get; private set; } = string.Empty;

    /// <summary>Separador hierárquico usado pelo servidor IMAP.</summary>
    public char Delimiter { get; private set; } = '/';

    /// <summary>Pasta marcada como favorita.</summary>
    public bool IsFavorite { get; private set; }

    /// <summary>Pasta assinada no servidor (LSUB).</summary>
    public bool IsSubscribed { get; private set; } = true;

    /// <summary>Se a pasta participa da sincronização.</summary>
    public bool SyncEnabled { get; private set; } = true;

    /// <summary>
    /// Pasta local, sem contrapartida no servidor. Pastas de pendências são locais: elas
    /// existem para conter o que a regra de domínio recusou, um conceito que o IMAP
    /// desconhece.
    /// </summary>
    public bool IsLocalOnly { get; private set; }

    /// <summary>Vínculo explícito com um Diretório de Domínio, definido nesta pasta.</summary>
    public Guid? RestrictedToDomainDirectoryId { get; private set; }

    /// <summary>Vínculo efetivo, já considerando herança dos ancestrais.</summary>
    public Guid? EffectiveRestrictionDomainDirectoryId { get; private set; }

    /// <summary>Se a pasta está sujeita a restrição de domínio, própria ou herdada.</summary>
    public bool IsDomainRestricted => EffectiveRestrictionDomainDirectoryId.HasValue;

    /// <summary>Se a restrição vigente veio de um ancestral em vez de ter sido definida aqui.</summary>
    public bool IsRestrictionInherited
        => EffectiveRestrictionDomainDirectoryId.HasValue && !RestrictedToDomainDirectoryId.HasValue;

    /// <summary>Mensagens não lidas. Desnormalizado para exibir o contador sem varrer a pasta.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Total de mensagens.</summary>
    public int TotalCount { get; private set; }

    /// <summary>
    /// UIDVALIDITY da pasta IMAP. Se mudar, todos os UIDs locais viraram lixo e a pasta
    /// precisa ser ressincronizada do zero.
    /// </summary>
    public long? UidValidity { get; private set; }

    /// <summary>HIGHESTMODSEQ (CONDSTORE), para sincronização incremental de marcadores.</summary>
    public long? HighestModSeq { get; private set; }

    /// <summary>Maior UID já visto, ponto de partida da busca por mensagens novas.</summary>
    public long? LastSeenUid { get; private set; }

    /// <summary>Posição manual na árvore.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Cria uma pasta.</summary>
    public static Folder Create(
        Guid accountId,
        string name,
        FolderType folderType,
        DateTimeOffset createdAt,
        Guid? parentFolderId = null,
        string? remotePath = null,
        bool isLocalOnly = false,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var folder = new Folder(id ?? Guid.CreateVersion7(), accountId, name.Trim(), folderType, createdAt)
        {
            ParentFolderId = parentFolderId,
            RemotePath = remotePath?.Trim() ?? name.Trim(),
            IsLocalOnly = isLocalOnly,
        };

        if (isLocalOnly)
        {
            folder.RemotePath = string.Empty;
            folder.SyncEnabled = false;
        }

        return folder;
    }

    /// <summary>
    /// Define o vínculo explícito desta pasta com um Diretório de Domínio.
    /// </summary>
    /// <remarks>
    /// Passar <see langword="null"/> remove o vínculo próprio; a pasta volta a herdar do
    /// ancestral, se houver. Quem chama deve, em seguida, recalcular a subárvore com
    /// <c>FolderRestrictionResolver</c>.
    /// </remarks>
    public void SetExplicitRestriction(Guid? domainDirectoryId, DateTimeOffset now)
    {
        RestrictedToDomainDirectoryId = domainDirectoryId;
        Touch(now);
    }

    /// <summary>
    /// Aplica a restrição efetiva calculada pelo resolvedor.
    /// </summary>
    /// <exception cref="InvalidFolderHierarchyException">
    /// A pasta tem vínculo explícito com um diretório e o ancestral impõe outro. A
    /// especificação proíbe que uma pasta responda a dois Diretórios de Domínio.
    /// </exception>
    public void ApplyEffectiveRestriction(Guid? inheritedFromAncestor, DateTimeOffset now)
    {
        if (RestrictedToDomainDirectoryId.HasValue
            && inheritedFromAncestor.HasValue
            && RestrictedToDomainDirectoryId.Value != inheritedFromAncestor.Value)
        {
            throw new InvalidFolderHierarchyException(
                $"A pasta '{DisplayName}' não pode ser vinculada ao Diretório de Domínio escolhido " +
                "porque uma pasta acima dela já está vinculada a outro diretório. " +
                "Uma pasta responde a um único Diretório de Domínio.");
        }

        EffectiveRestrictionDomainDirectoryId = RestrictedToDomainDirectoryId ?? inheritedFromAncestor;
        Touch(now);
    }

    /// <summary>Move a pasta para outra pasta-mãe (ou para a raiz, com <see langword="null"/>).</summary>
    /// <exception cref="InvalidFolderHierarchyException">O destino criaria um ciclo.</exception>
    public void Reparent(Folder? newParent, DateTimeOffset now)
    {
        if (newParent is not null)
        {
            if (newParent.Id == Id)
            {
                throw new InvalidFolderHierarchyException(
                    $"A pasta '{DisplayName}' não pode ser movida para dentro dela mesma.");
            }

            // Um descendente como novo pai desconectaria o ramo da árvore.
            for (var ancestor = newParent; ancestor is not null; ancestor = ancestor.ParentFolder)
            {
                if (ancestor.Id == Id)
                {
                    throw new InvalidFolderHierarchyException(
                        $"A pasta '{DisplayName}' não pode ser movida para dentro de uma de suas subpastas.");
                }
            }
        }

        ParentFolderId = newParent?.Id;
        ParentFolder = newParent;
        Touch(now);
    }

    /// <summary>Renomeia a pasta.</summary>
    public void Rename(string name, string? remotePath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        DisplayName = name.Trim();
        if (remotePath is not null)
        {
            RemotePath = remotePath.Trim();
        }

        Touch(now);
    }

    /// <summary>Define o nome exibido sem alterar o nome no servidor.</summary>
    public void SetDisplayName(string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
        Touch(now);
    }

    /// <summary>Marca ou desmarca como favorita.</summary>
    public void SetFavorite(bool isFavorite, DateTimeOffset now)
    {
        IsFavorite = isFavorite;
        Touch(now);
    }

    /// <summary>Ajusta as preferências de sincronização da pasta.</summary>
    public void ConfigureSync(bool syncEnabled, bool isSubscribed, DateTimeOffset now)
    {
        SyncEnabled = syncEnabled;
        IsSubscribed = isSubscribed;
        Touch(now);
    }

    /// <summary>Atualiza os contadores exibidos na árvore.</summary>
    public void UpdateCounts(int totalCount, int unreadCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unreadCount);

        TotalCount = totalCount;
        UnreadCount = unreadCount;
        Touch(now);
    }

    /// <summary>
    /// Grava o estado de sincronização IMAP da pasta.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> quando o UIDVALIDITY mudou — sinal de que os UIDs locais
    /// não valem mais e a pasta precisa ser ressincronizada por completo.
    /// </returns>
    public bool UpdateSyncState(long uidValidity, long? highestModSeq, long? lastSeenUid, DateTimeOffset now)
    {
        var invalidated = UidValidity.HasValue && UidValidity.Value != uidValidity;

        UidValidity = uidValidity;
        HighestModSeq = highestModSeq;
        LastSeenUid = invalidated ? null : lastSeenUid ?? LastSeenUid;
        Touch(now);

        return invalidated;
    }

    /// <summary>
    /// Manda a próxima sincronização reler a pasta desde o começo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A leitura incremental parte de <see cref="LastSeenUid"/> e busca só o que está acima
    /// dele — é o que a torna barata, e é também o que a impede de consertar o que já está
    /// gravado. Linha antiga com UID errado nunca é revisitada, então não se corrige sozinha:
    /// o corpo dela falha para sempre, e a única pista é o servidor dizer que não conhece
    /// aquele UID.
    /// </para>
    /// <para>
    /// Zerar o marcador transforma a próxima passada em leitura completa. Ela não duplica
    /// nada — <c>UpsertAsync</c> reconhece cada mensagem pelo <c>Message-ID</c> dentro da
    /// pasta e corrige o UID em vez de inserir de novo (D-042).
    /// </para>
    /// </remarks>
    public void RequestFullReread(DateTimeOffset now)
    {
        LastSeenUid = null;
        Touch(now);
    }

    /// <summary>Define a posição manual na árvore.</summary>
    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        Touch(now);
    }

    /// <summary>Registra uma subpasta na coleção em memória.</summary>
    public void AddChild(Folder child, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (_children.Any(f => f.Id == child.Id))
        {
            return;
        }

        _children.Add(child);
        child.Reparent(this, now);
    }
}
