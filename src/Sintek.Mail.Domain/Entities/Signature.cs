namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// An e-mail signature for an account.
/// </summary>
public sealed class Signature : Entity
{
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    // Navigation
    public Account Account { get; set; } = null!;
}
