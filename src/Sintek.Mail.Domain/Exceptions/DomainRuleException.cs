using Sintek.Mail.Domain.ValueObjects;

namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Base das violações de regra de negócio. Distingue "o usuário tentou algo que a regra
/// não permite" de falhas técnicas — a interface trata as duas de formas diferentes.
/// </summary>
public abstract class DomainRuleException : Exception
{
    protected DomainRuleException(string message) : base(message)
    {
    }

    protected DomainRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Mensagem pronta para exibição ao usuário. Por padrão é a própria
    /// <see cref="Exception.Message"/>, já que estas exceções nascem com texto redigido
    /// para leitura humana.
    /// </summary>
    public virtual string UserMessage => Message;
}

/// <summary>
/// Uma conta de e-mail não pertence ao domínio do Diretório de Domínio em que se tentou
/// vinculá-la.
/// </summary>
public sealed class DomainMismatchException : DomainRuleException
{
    public DomainMismatchException(EmailAddress account, EmailDomain expectedDomain, bool subdomainsAllowed)
        : base(BuildMessage(account, expectedDomain, subdomainsAllowed))
    {
        Account = account;
        ExpectedDomain = expectedDomain;
        ActualDomain = account.Domain;
        SubdomainsAllowed = subdomainsAllowed;
    }

    /// <summary>Conta recusada.</summary>
    public EmailAddress Account { get; }

    /// <summary>Domínio configurado no diretório.</summary>
    public EmailDomain ExpectedDomain { get; }

    /// <summary>Domínio efetivo da conta.</summary>
    public EmailDomain ActualDomain { get; }

    /// <summary>Se o diretório aceitava subdomínios no momento da recusa.</summary>
    public bool SubdomainsAllowed { get; }

    private static string BuildMessage(EmailAddress account, EmailDomain expected, bool subdomainsAllowed)
    {
        var baseMessage =
            $"A conta '{account.Value}' pertence ao domínio '{account.Domain.Value}' e não pode ser " +
            $"vinculada ao Diretório de Domínio '{expected.Value}'.";

        // Quando o domínio da conta é um subdomínio do diretório, a recusa costuma
        // surpreender: apontamos a configuração exata que a resolveria.
        if (!subdomainsAllowed
            && account.Domain.IsSameOrSubdomainOf(expected, allowSubdomains: true))
        {
            return baseMessage +
                " Este é um subdomínio do diretório; para aceitá-lo, habilite 'Permitir subdomínios' " +
                "nas configurações do Diretório de Domínio.";
        }

        return baseMessage;
    }
}

/// <summary>
/// Uma mensagem não satisfaz a regra de domínio da pasta restrita para a qual se tentou
/// movê-la.
/// </summary>
public sealed class FolderDomainRestrictionException : DomainRuleException
{
    /// <summary>
    /// Texto exigido literalmente pela especificação. Ele é a mensagem que o usuário vê
    /// ao tentar mover um e-mail incompatível — não alterar sem revisar a especificação.
    /// </summary>
    public const string RestrictionMessage =
        "Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local.";

    public FolderDomainRestrictionException(Guid messageId, Guid folderId, EmailDomain expectedDomain)
        : base(RestrictionMessage)
    {
        MessageId = messageId;
        FolderId = folderId;
        ExpectedDomain = expectedDomain;
    }

    /// <summary>Mensagem recusada.</summary>
    public Guid MessageId { get; }

    /// <summary>Pasta restrita de destino.</summary>
    public Guid FolderId { get; }

    /// <summary>Domínio exigido pela pasta.</summary>
    public EmailDomain ExpectedDomain { get; }
}

/// <summary>
/// Uma invariante estrutural do modelo foi violada — por exemplo, tentar vincular uma
/// pasta a um segundo Diretório de Domínio, ou criar um ciclo na hierarquia de pastas.
/// </summary>
public sealed class InvalidFolderHierarchyException : DomainRuleException
{
    public InvalidFolderHierarchyException(string message) : base(message)
    {
    }
}
