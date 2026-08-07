using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Sintek.Mail.Application.Abstractions.Security;
using Sintek.Mail.Domain.Enums;

namespace Sintek.Mail.Infrastructure.Security;

/// <summary>
/// Autenticação OAuth 2.0 no Microsoft Entra ID para Outlook.com, Microsoft 365 e
/// Exchange Online.
/// </summary>
/// <remarks>
/// O cache de tokens do MSAL é persistido no <see cref="ICredentialStore"/> — ou seja, no
/// Windows Credential Manager — e não no arquivo que o MSAL usaria por padrão. Um token
/// de atualização vale tanto quanto a senha: gravá-lo em arquivo contrariaria a exigência
/// da especificação de manter todo segredo no cofre do sistema.
/// </remarks>
public sealed class MicrosoftOAuthProvider : IOAuthProvider
{
    /// <summary>
    /// Escopo específico de IMAP/SMTP no Outlook. Sem ele o token vem válido para o Graph
    /// e o servidor de e-mail recusa a autenticação XOAUTH2.
    /// </summary>
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
        "offline_access",
    ];

    /// <summary>
    /// Escopo do Microsoft Graph para agenda.
    /// </summary>
    /// <remarks>
    /// <b>Não pode ser pedido junto com os de IMAP.</b> O Entra emite um token por recurso, e
    /// misturar públicos numa só chamada é recusado antes de sair da máquina. O consentimento
    /// interativo cobre os dois porque pede os dois em sequência; a renovação silenciosa pede
    /// só o que a chamada precisa.
    /// </remarks>
    public static readonly string[] CalendarScopes =
    [
        "https://graph.microsoft.com/Calendars.ReadWrite",
        "offline_access",
    ];

    /// <summary>Teto para a segunda janela de consentimento, a da agenda.</summary>
    /// <remarks>
    /// Mais curto que o teto do consentimento principal porque a situação é outra: aqui o
    /// usuário já autorizou uma vez e não espera ser perguntado de novo, então a janela
    /// abandonada é o caso comum, não a exceção.
    /// </remarks>
    private static readonly TimeSpan CalendarConsentTimeout = TimeSpan.FromMinutes(2);

    private readonly OAuthClientOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MicrosoftOAuthProvider> _logger;
    private IPublicClientApplication? _application;

    public MicrosoftOAuthProvider(
        IOptions<OAuthOptions> options,
        ICredentialStore credentials,
        TimeProvider timeProvider,
        ILogger<MicrosoftOAuthProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Microsoft;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public OAuthProviderKind Provider => OAuthProviderKind.Microsoft;

    /// <inheritdoc />
    public bool IsConfigured => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<OAuthAccessToken> AuthenticateInteractivelyAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        var result = await application
            .AcquireTokenInteractive(Scopes)
            .WithLoginHint(emailAddress)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        // O e-mail é gravado aqui, antes de qualquer coisa que possa dar errado. A ordem é o
        // ponto: a gravação ficava só no fim, depois da etapa de agenda, e a agenda abre uma
        // **segunda** janela de navegador. Quando ela não se completava, nada era gravado — e
        // o consentimento de e-mail, que já tinha dado certo, era jogado fora junto. O
        // sintoma é cruel: o provedor avisa por e-mail que o aplicativo foi conectado, e o
        // cofre local está vazio.
        await PersistCacheAsync(application, emailAddress, cancellationToken).ConfigureAwait(false);

        await TryGrantCalendarAsync(application, result, emailAddress, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Autenticação Microsoft concluída para uma conta de e-mail.");
        return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
    }

    /// <summary>
    /// Pede o consentimento da agenda, que é opcional e não pode custar o do e-mail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O Entra emite token <b>por recurso</b>: pedir os escopos de `outlook.office.com` e os
    /// de `graph.microsoft.com` na mesma chamada é recusado com <c>AADSTS28000</c>. Por isso
    /// são duas idas, e por isso a segunda pode abrir outra janela de navegador.
    /// </para>
    /// <para>
    /// Teto próprio porque essa segunda janela é a que o usuário tende a fechar: ele já
    /// autorizou uma vez e não espera ser perguntado de novo. Sem teto, a espera não termina
    /// e leva o cadastro junto. Recusar a agenda é resultado aceitável — a conta é cadastrada
    /// e o espelho remoto fica para quando o usuário consentir.
    /// </para>
    /// </remarks>
    private async Task TryGrantCalendarAsync(
        IPublicClientApplication application,
        Microsoft.Identity.Client.AuthenticationResult mail,
        string emailAddress,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CalendarConsentTimeout, _timeProvider);
        using var linked = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            try
            {
                await application
                    .AcquireTokenSilent(CalendarScopes, mail.Account)
                    .ExecuteAsync(linked.Token)
                    .ConfigureAwait(false);
            }
            catch (MsalUiRequiredException)
            {
                await application
                    .AcquireTokenInteractive(CalendarScopes)
                    .WithLoginHint(emailAddress)
                    .ExecuteAsync(linked.Token)
                    .ConfigureAwait(false);
            }

            await PersistCacheAsync(application, emailAddress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MsalException or OperationCanceledException)
        {
            _logger.LogInformation(
                ex, "O consentimento de agenda não foi concedido; o e-mail segue autorizado.");
        }
    }

    /// <inheritdoc />
    public Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress, CancellationToken cancellationToken = default)
        => GetAccessTokenAsync(emailAddress, Scopes, cancellationToken);

    /// <inheritdoc />
    public async Task<OAuthAccessToken> GetAccessTokenAsync(
        string emailAddress,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, emailAddress, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw new ReauthenticationRequiredException(emailAddress);
        }

        try
        {
            var result = await application
                .AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            await PersistCacheAsync(application, emailAddress, cancellationToken).ConfigureAwait(false);
            return new OAuthAccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            // O token de atualização venceu ou foi revogado. Quem chama precisa levar o
            // usuário de volta ao consentimento — tentar de novo em silêncio só repetiria
            // a falha.
            throw new ReauthenticationRequiredException(emailAddress, ex);
        }
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(emailAddress, cancellationToken).ConfigureAwait(false);

        foreach (var account in await application.GetAccountsAsync().ConfigureAwait(false))
        {
            await application.RemoveAsync(account).ConfigureAwait(false);
        }

        // Pelo ChunkedSecret, e não pelo cofre direto: o cache ocupa várias entradas, e
        // apagar só o cabeçalho deixaria as fatias para trás — tokens de uma conta removida
        // sobrevivendo no Gerenciador de Credenciais.
        await ChunkedSecret.DeleteAsync(_credentials, CacheKey(emailAddress), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IPublicClientApplication> GetApplicationAsync(
        string emailAddress, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "A autenticação Microsoft não está configurada: registre um aplicativo no Entra ID e " +
                "informe o Client ID em OAuth:Microsoft:ClientId.");
        }

        if (_application is not null)
        {
            return _application;
        }

        _application = PublicClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _options.TenantId)
            .WithRedirectUri(_options.RedirectUri)
            .Build();

        AttachCredentialStoreCache(_application, emailAddress);

        // Primeira carga do cache: o MSAL só dispara o evento de leitura na primeira
        // operação, e precisamos do cache disponível antes disso.
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return _application;
    }

    /// <summary>
    /// Liga o cache de tokens do MSAL ao Windows Credential Manager.
    /// </summary>
    private void AttachCredentialStoreCache(IPublicClientApplication application, string emailAddress)
    {
        var key = CacheKey(emailAddress);

        // As sobrecargas assíncronas existem e são as corretas aqui: as síncronas obrigariam
        // a um `GetAwaiter().GetResult()` sobre uma chamada P/Invoke, e o MSAL dispara esses
        // eventos na linha de execução de quem pediu o token — que neste aplicativo é a da
        // interface.
        application.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            var stored = await ChunkedSecret.ReadAsync(_credentials, key).ConfigureAwait(false);

            if (stored is { Length: > 0 })
            {
                args.TokenCache.DeserializeMsalV3(stored);
            }
        });

        application.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            // Comprimido e fatiado: o cache do MSAL não cabe numa entrada do Gerenciador de
            // Credenciais, e o caminho até lá ainda o inflava 2,67 vezes. Ver ChunkedSecret.
            await ChunkedSecret
                .WriteAsync(_credentials, key, args.TokenCache.SerializeMsalV3())
                .ConfigureAwait(false);
        });
    }

    private static string CacheKey(string emailAddress)
        => $"Sintek.Mail:oauth:microsoft:{emailAddress}";

    /// <summary>
    /// Força a gravação do cache logo após uma aquisição de token.
    /// </summary>
    private async Task PersistCacheAsync(
        IPublicClientApplication application, string emailAddress, CancellationToken cancellationToken)
    {
        // A serialização já acontece no evento SetAfterAccess; este método existe para
        // deixar explícito no fluxo que o token foi persistido, e para permitir
        // verificação em teste.
        _ = application;
        _ = emailAddress;
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Monta a cadeia SASL XOAUTH2 no formato exigido pelos servidores de e-mail.
    /// </summary>
    /// <remarks>
    /// O formato tem separadores <c>\x01</c> obrigatórios; errá-los produz uma falha de
    /// autenticação genérica que não indica a causa.
    /// </remarks>
    public static string BuildXOAuth2Token(string userName, string accessToken)
        => Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"user={userName}\x01auth=Bearer {accessToken}\x01\x01"));
}
