using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Presentation.ViewModels;

/// <summary>Que tipo de item da árvore de navegação um nó representa.</summary>
public enum NavigationNodeKind
{
    /// <summary>Cabeçalho de seção ("Favoritos", "Contas e Diretórios").</summary>
    Section,

    /// <summary>Diretório de Domínio.</summary>
    DomainDirectory,

    /// <summary>Conta de e-mail.</summary>
    Account,

    /// <summary>Pasta.</summary>
    Folder,

    /// <summary>Pesquisa salva.</summary>
    SavedSearch,
}

/// <summary>
/// Um nó da árvore de navegação da barra lateral.
/// </summary>
/// <remarks>
/// Reproduz a hierarquia obrigatória da especificação:
/// <c>Domínio → Conta → Pastas</c>, com os ícones do Fluent Design correspondentes a cada
/// nível e o contador de não lidas ao lado.
/// </remarks>
public sealed partial class NavigationNode : ObservableObject
{
    public NavigationNode(NavigationNodeKind kind, string title, string icon)
    {
        Kind = kind;
        Title = title;
        Icon = icon;
    }

    /// <summary>Tipo do nó.</summary>
    public NavigationNodeKind Kind { get; }

    /// <summary>Texto exibido.</summary>
    [ObservableProperty]
    private string _title;

    /// <summary>
    /// Glifo do Segoe Fluent Icons.
    /// </summary>
    /// <remarks>
    /// Guardamos o glifo, e não um caminho de imagem, porque a fonte de ícones do sistema
    /// acompanha automaticamente o tema claro/escuro e o alto contraste do Windows.
    /// </remarks>
    [ObservableProperty]
    private string _icon;

    /// <summary>Identificador da entidade correspondente (conta, pasta, diretório).</summary>
    public Guid EntityId { get; init; }

    /// <summary>Se a pasta está entre os favoritos — alimenta o menu de contexto.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Conta à qual o nó pertence, quando aplicável.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>Mensagens não lidas.</summary>
    [ObservableProperty]
    private int _unreadCount;

    /// <summary>Total de mensagens.</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>Se o nó está expandido na árvore.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Se a pasta é restrita por um Diretório de Domínio.</summary>
    [ObservableProperty]
    private bool _isDomainRestricted;

    /// <summary>Se a restrição foi herdada de uma pasta acima.</summary>
    [ObservableProperty]
    private bool _isRestrictionInherited;

    /// <summary>Nós filhos.</summary>
    public ObservableCollection<NavigationNode> Children { get; } = [];

    /// <summary>Se o contador de não lidas deve aparecer.</summary>
    public bool ShowUnreadBadge => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(ShowUnreadBadge));

    /// <summary>
    /// Glifos do Segoe Fluent Icons para cada tipo de pasta.
    /// </summary>
    /// <remarks>
    /// A escolha segue a especificação: inbox para a caixa de entrada, envio para
    /// enviados, documento para rascunhos, descarte para lixeira, alerta para spam e
    /// pasta para as personalizadas.
    /// </remarks>
    public static string IconForFolder(FolderType folderType) => folderType switch
    {
        FolderType.Inbox => "",
        FolderType.Sent => "",
        FolderType.Drafts => "",
        FolderType.Trash => "",
        FolderType.Junk => "",
        FolderType.Archive => "",
        FolderType.Pending => "",
        FolderType.Outbox => "",
        FolderType.Templates => "",
        _ => "",
    };

    /// <summary>Ícone de organização/rede, usado no nível de domínio.</summary>
    public const string DomainIcon = "";

    /// <summary>Ícone de e-mail, usado no nível de conta.</summary>
    public const string AccountIcon = "";

    /// <summary>Ícone de favorito.</summary>
    public const string FavoriteIcon = "";

    /// <summary>Ícone de pesquisa salva.</summary>
    public const string SavedSearchIcon = "";
}
