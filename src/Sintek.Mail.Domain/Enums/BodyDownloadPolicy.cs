namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Policy for downloading message bodies.
/// </summary>
public enum BodyDownloadPolicy
{
    /// <summary>Download full body immediately.</summary>
    Full,

    /// <summary>Download headers + preview only, body on demand.</summary>
    HeadersOnly,

    /// <summary>Download body only when message is opened.</summary>
    OnDemand
}
