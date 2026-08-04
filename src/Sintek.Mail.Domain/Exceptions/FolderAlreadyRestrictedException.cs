namespace Sintek.Mail.Domain.Exceptions;

/// <summary>
/// Thrown when a folder is already restricted to a different domain.
/// </summary>
public sealed class FolderAlreadyRestrictedException : DomainException
{
    public Guid FolderId { get; }
    public Guid ExistingDomainId { get; }
    public Guid AttemptedDomainId { get; }

    public FolderAlreadyRestrictedException(Guid folderId, Guid existingDomainId, Guid attemptedDomainId)
        : base($"Folder '{folderId}' is already restricted to domain '{existingDomainId}'. Cannot restrict to '{attemptedDomainId}'.")
    {
        FolderId = folderId;
        ExistingDomainId = existingDomainId;
        AttemptedDomainId = attemptedDomainId;
    }
}
