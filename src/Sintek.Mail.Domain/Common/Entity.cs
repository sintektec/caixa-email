namespace Sintek.Mail.Domain.Common;

/// <summary>
/// Base das entidades persistidas: identidade, carimbos de tempo e igualdade por Id.
/// </summary>
/// <remarks>
/// <para>
/// Duas decisões deliberadas aqui, ambas divergentes do esboço da especificação:
/// </para>
/// <para>
/// <b>Identificadores são GUID v7</b>, não v4. GUIDs v7 embutem o instante de criação e
/// por isso são monotonicamente crescentes, o que mantém a localidade dos índices do
/// SQLite. Com v4 aleatório, cada inserção cai em uma página distinta da árvore — em uma
/// caixa postal com centenas de milhares de mensagens isso degrada visivelmente a
/// sincronização.
/// </para>
/// <para>
/// <b>Carimbos são <see cref="DateTimeOffset"/></b>, não <c>DateTime</c>. O cabeçalho
/// Date da RFC 5322 carrega deslocamento de fuso, e o MimeKit o entrega como
/// <see cref="DateTimeOffset"/>; usar <c>DateTime</c> descartaria essa informação e faria
/// uma mensagem enviada às 23h em -03:00 aparecer no dia errado.
/// </para>
/// <para>
/// O horário nunca é lido do relógio do sistema dentro do domínio: quem chama informa o
/// instante. Isso mantém esta camada determinística e testável sem congelar o relógio.
/// </para>
/// </remarks>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id, DateTimeOffset createdAt)
    {
        Id = id == Guid.Empty ? Guid.CreateVersion7() : id;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>Construtor usado apenas pelo Entity Framework Core ao materializar.</summary>
    protected Entity()
    {
    }

    /// <summary>Chave primária.</summary>
    public Guid Id { get; private set; }

    /// <summary>Instante de criação do registro local.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Instante da última alteração local.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Registra que a entidade mudou agora.</summary>
    protected void Touch(DateTimeOffset now) => UpdatedAt = now;

    public bool Equals(Entity? other)
        => other is not null
            && GetType() == other.GetType()
            && Id != Guid.Empty
            && Id == other.Id;

    public override bool Equals(object? obj) => Equals(obj as Entity);

    public override int GetHashCode() => Id.GetHashCode();
}
