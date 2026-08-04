namespace Sintek.Mail.Domain.Entities;

/// <summary>
/// A saved search query.
/// </summary>
public sealed class SavedSearch : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
}
