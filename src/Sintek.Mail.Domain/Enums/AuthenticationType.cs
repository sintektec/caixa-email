namespace Sintek.Mail.Domain.Enums;

/// <summary>
/// Type of authentication used by an e-mail account.
/// </summary>
public enum AuthenticationType
{
    /// <summary>Basic IMAP/SMTP with password.</summary>
    Basic,

    /// <summary>OAuth 2.0 via Microsoft 365 (MSAL).</summary>
    OAuthMicrosoft,

    /// <summary>OAuth 2.0 via Google (Google.Apis.Auth).</summary>
    OAuthGoogle,

    /// <summary>OAuth 2.0 via a custom/other provider.</summary>
    OAuthCustom
}
