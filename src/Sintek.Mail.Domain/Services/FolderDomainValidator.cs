using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Exceptions;

namespace Sintek.Mail.Domain.Services;

/// <summary>
/// Validates folder-domain restrictions, including inheritance rules.
/// </summary>
public sealed class FolderDomainValidator
{
    /// <summary>
    /// Validates that a folder can be restricted to a domain.
    /// Throws FolderAlreadyRestrictedException if already restricted to a different domain.
    /// </summary>
    public static void ValidateRestriction(Folder folder, Guid domainId)
    {
        if (folder.IsDomainRestricted && folder.RestrictedToDomainId.HasValue && folder.RestrictedToDomainId.Value != domainId)
        {
            throw new FolderAlreadyRestrictedException(folder.Id, folder.RestrictedToDomainId.Value, domainId);
        }
    }

    /// <summary>
    /// Gets the effective domain restriction for a folder, walking up the hierarchy.
    /// Subfolders inherit the restriction from their parent.
    /// </summary>
    public static Guid? GetEffectiveDomainRestriction(Folder folder)
    {
        if (folder.IsDomainRestricted && folder.RestrictedToDomainId.HasValue)
            return folder.RestrictedToDomainId.Value;

        if (folder.ParentFolder is not null)
            return GetEffectiveDomainRestriction(folder.ParentFolder);

        return null;
    }

    /// <summary>
    /// Checks if a folder is effectively restricted to any domain (directly or inherited).
    /// </summary>
    public static bool IsEffectivelyRestricted(Folder folder)
    {
        return GetEffectiveDomainRestriction(folder).HasValue;
    }
}
