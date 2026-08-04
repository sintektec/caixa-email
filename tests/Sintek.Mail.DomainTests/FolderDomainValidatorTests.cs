using Sintek.Mail.Domain.Entities;
using Sintek.Mail.Domain.Exceptions;
using Sintek.Mail.Domain.Services;
using Xunit;

namespace Sintek.Mail.DomainTests;

public class FolderDomainValidatorTests
{
    [Fact]
    public void ValidateRestriction_UnrestrictedFolder_DoesNotThrow()
    {
        var folder = new Folder { IsDomainRestricted = false };
        FolderDomainValidator.ValidateRestriction(folder, Guid.NewGuid());
    }

    [Fact]
    public void ValidateRestriction_SameDomain_DoesNotThrow()
    {
        var domainId = Guid.NewGuid();
        var folder = new Folder { IsDomainRestricted = true, RestrictedToDomainId = domainId };
        FolderDomainValidator.ValidateRestriction(folder, domainId);
    }

    [Fact]
    public void ValidateRestriction_DifferentDomain_Throws()
    {
        var folder = new Folder { IsDomainRestricted = true, RestrictedToDomainId = Guid.NewGuid() };
        Assert.Throws<FolderAlreadyRestrictedException>(() =>
            FolderDomainValidator.ValidateRestriction(folder, Guid.NewGuid()));
    }

    [Fact]
    public void GetEffectiveDomainRestriction_DirectRestriction_ReturnsDomainId()
    {
        var domainId = Guid.NewGuid();
        var folder = new Folder { IsDomainRestricted = true, RestrictedToDomainId = domainId };
        Assert.Equal(domainId, FolderDomainValidator.GetEffectiveDomainRestriction(folder));
    }

    [Fact]
    public void GetEffectiveDomainRestriction_InheritedFromParent_ReturnsDomainId()
    {
        var domainId = Guid.NewGuid();
        var parent = new Folder { IsDomainRestricted = true, RestrictedToDomainId = domainId };
        var child = new Folder { ParentFolder = parent };
        Assert.Equal(domainId, FolderDomainValidator.GetEffectiveDomainRestriction(child));
    }

    [Fact]
    public void GetEffectiveDomainRestriction_NoRestriction_ReturnsNull()
    {
        var folder = new Folder { IsDomainRestricted = false };
        Assert.Null(FolderDomainValidator.GetEffectiveDomainRestriction(folder));
    }

    [Fact]
    public void IsEffectivelyRestricted_RestrictedFolder_ReturnsTrue()
    {
        var folder = new Folder { IsDomainRestricted = true, RestrictedToDomainId = Guid.NewGuid() };
        Assert.True(FolderDomainValidator.IsEffectivelyRestricted(folder));
    }

    [Fact]
    public void IsEffectivelyRestricted_UnrestrictedFolder_ReturnsFalse()
    {
        var folder = new Folder { IsDomainRestricted = false };
        Assert.False(FolderDomainValidator.IsEffectivelyRestricted(folder));
    }
}
