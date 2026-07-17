using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StayActive.Remotes;

/// <summary>
/// Retrieves a bearer token for the self-hosted RemoteHub API.  Calling
/// <see cref="GetAccessTokenAsync"/> never opens a browser: the user must start
/// the interactive, local sign-in flow explicitly through <see cref="SignInAsync"/>.
/// </summary>
internal interface IRemoteHubAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task SignInAsync(CancellationToken cancellationToken) => Task.FromException(
        new RemoteHubOidcException("Interactive sign-in is not available for this token provider."));

    Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Validated configuration for a public OIDC client owned by the RemoteHub
/// deployment.  This type intentionally has no client-secret field: a Windows
/// tray application is a public client and must use Authorization Code + PKCE.
/// </summary>
internal sealed class RemoteHubOidcConfiguration
{
    internal const string RequiredScopes = "openid profile remotehub.fleet.read offline_access";
    internal const string EnrollmentRequiredScopes = "openid profile stayactive.enrollment.write";

    private RemoteHubOidcConfiguration(
        Uri issuer,
        string clientId,
        string requestedScopes,
        bool requireFreshAuthentication,
        bool allowRefreshTokens)
    {
        Issuer = issuer;
        ClientId = clientId;
        RequestedScopes = requestedScopes;
        RequireFreshAuthentication = requireFreshAuthentication;
        AllowRefreshTokens = allowRefreshTokens;
    }

    public Uri Issuer { get; }

    public string ClientId { get; }

    // Scopes are selected by a fixed local flow, never entered in settings.
    internal string RequestedScopes { get; }

    // Ticket issuance must not silently reuse the fleet browser session.
    internal bool RequireFreshAuthentication { get; }

    // The enrollment flow has no need for offline access or refresh tokens.
    internal bool AllowRefreshTokens { get; }

    public static bool TryCreate(
        string? issuer,
        string? clientId,
        out RemoteHubOidcConfiguration configuration)
    {
        return TryCreate(
            issuer,
            clientId,
            RequiredScopes,
            requireFreshAuthentication: false,
            allowRefreshTokens: true,
            out configuration);
    }

    internal static bool TryCreateEnrollment(
        string? issuer,
        string? clientId,
        out RemoteHubOidcConfiguration configuration)
    {
        return TryCreate(
            issuer,
            clientId,
            EnrollmentRequiredScopes,
            requireFreshAuthentication: true,
            allowRefreshTokens: false,
            out configuration);
    }

    private static bool TryCreate(
        string? issuer,
        string? clientId,
        string requestedScopes,
        bool requireFreshAuthentication,
        bool allowRefreshTokens,
        out RemoteHubOidcConfiguration configuration)
    {
        configuration = null!;
        if (!RemoteHubOidcEndpointValidator.TryGetIssuer(issuer, out var issuerUri)
            || !IsSafeClientId(clientId)
            || !IsSafeScopes(requestedScopes))
        {
            return false;
        }

        configuration = new RemoteHubOidcConfiguration(
            issuerUri,
            clientId!,
            requestedScopes,
            requireFreshAuthentication,
            allowRefreshTokens);
        return true;
    }

    private static bool IsSafeClientId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 512
            && !value.Any(char.IsControl);
    }

    private static bool IsSafeScopes(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 1024
            && !value.Any(char.IsControl);
    }
}

/// <summary>
/// Token data is deliberately a class rather than a record, because the default
/// record <c>ToString()</c> would expose bearer and refresh tokens in diagnostics.
/// </summary>
internal sealed class RemoteHubTokenSet
{
    public RemoteHubTokenSet(
        string issuer,
        string clientId,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAt)
    {
        if (!RemoteHubOidcEndpointValidator.TryGetIssuer(issuer, out var issuerUri))
        {
            throw new ArgumentException("The token issuer must be a valid HTTPS OIDC issuer.", nameof(issuer));
        }

        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 512 || clientId.Any(char.IsControl))
        {
            throw new ArgumentException("The token client ID is invalid.", nameof(clientId));
        }

        if (!IsSafeTokenValue(accessToken))
        {
            throw new ArgumentException("The access token is invalid.", nameof(accessToken));
        }

        if (refreshToken is not null && !IsSafeTokenValue(refreshToken))
        {
            throw new ArgumentException("The refresh token is invalid.", nameof(refreshToken));
        }

        Issuer = issuerUri.AbsoluteUri;
        ClientId = clientId;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
    }

    public string Issuer { get; }

    public string ClientId { get; }

    public string AccessToken { get; }

    public string? RefreshToken { get; }

    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => "[RemoteHub OIDC tokens redacted]";

    internal bool IsBoundTo(RemoteHubOidcConfiguration configuration)
    {
        return Uri.Compare(
                   new Uri(Issuer, UriKind.Absolute),
                   configuration.Issuer,
                   UriComponents.HttpRequestUrl,
                   UriFormat.UriEscaped,
                   StringComparison.OrdinalIgnoreCase) == 0
            && string.Equals(ClientId, configuration.ClientId, StringComparison.Ordinal);
    }

    private static bool IsSafeTokenValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 16 * 1024
            && !value.Any(char.IsControl);
    }
}

internal interface IRemoteHubTokenStorage
{
    Task<RemoteHubTokenSet?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(RemoteHubTokenSet tokenSet, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

internal interface IRemoteHubOidcHttpClient
{
    Task<RemoteHubOidcHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken);

    Task<RemoteHubOidcHttpResponse> PostFormAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken);
}

/// <summary>
/// This response can contain OAuth token JSON, so it must never be formatted
/// into a log line or exception message.
/// </summary>
internal sealed class RemoteHubOidcHttpResponse
{
    public RemoteHubOidcHttpResponse(int statusCode, string body)
    {
        StatusCode = statusCode;
        Body = body ?? string.Empty;
    }

    public int StatusCode { get; }

    public string Body { get; }

    public override string ToString() => "[RemoteHub OIDC HTTP response redacted]";
}

internal interface IRemoteHubOidcBrowser
{
    Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken);
}

internal interface IRemoteHubLoopbackCallbackListenerFactory
{
    IRemoteHubLoopbackCallbackListener Create();
}

internal interface IRemoteHubLoopbackCallbackListener : IDisposable
{
    Uri Start();

    Task<Uri> WaitForCallbackAsync(CancellationToken cancellationToken);
}

internal class RemoteHubOidcException : Exception
{
    public RemoteHubOidcException(string message)
        : base(message)
    {
    }
}

internal sealed class RemoteHubAuthenticationRequiredException : RemoteHubOidcException
{
    public RemoteHubAuthenticationRequiredException()
        : base("Sign in to the self-hosted RemoteHub to continue.")
    {
    }
}

/// <summary>
/// Implements a public-client OIDC flow for the self-hosted RemoteHub.  It uses
/// a loopback callback and S256 PKCE, never persists a client secret, and keeps
/// all potentially sensitive protocol data out of exception messages.
/// </summary>
internal sealed class RemoteHubOidcAccessTokenProvider : IRemoteHubAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultAuthorizationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    private readonly Func<RemoteHubOidcConfiguration?> _getConfiguration;
    private readonly IRemoteHubTokenStorage _tokenStorage;
    private readonly IRemoteHubOidcHttpClient _httpClient;
    private readonly IRemoteHubOidcBrowser _browser;
    private readonly IRemoteHubLoopbackCallbackListenerFactory _callbackListenerFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _authorizationTimeout;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IDisposable? _ownedHttpClient;

    private RemoteHubOidcDiscovery? _cachedDiscovery;
    private bool _disposed;

    public RemoteHubOidcAccessTokenProvider(Func<RemoteHubOidcConfiguration?> getConfiguration)
        : this(
            getConfiguration,
            new DpapiCurrentUserRemoteHubTokenStorage(),
            new SystemRemoteHubOidcHttpClient(),
            new SystemRemoteHubOidcBrowser(),
            new SystemRemoteHubLoopbackCallbackListenerFactory(),
            DefaultRequestTimeout,
            DefaultAuthorizationTimeout,
            () => DateTimeOffset.UtcNow,
            ownsHttpClient: true)
    {
    }

    internal RemoteHubOidcAccessTokenProvider(
        Func<RemoteHubOidcConfiguration?> getConfiguration,
        IRemoteHubTokenStorage tokenStorage,
        IRemoteHubOidcHttpClient httpClient,
        IRemoteHubOidcBrowser browser,
        IRemoteHubLoopbackCallbackListenerFactory callbackListenerFactory,
        TimeSpan? requestTimeout = null,
        TimeSpan? authorizationTimeout = null,
        Func<DateTimeOffset>? utcNow = null,
        bool ownsHttpClient = false)
    {
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
        _tokenStorage = tokenStorage ?? throw new ArgumentNullException(nameof(tokenStorage));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _callbackListenerFactory = callbackListenerFactory ?? throw new ArgumentNullException(nameof(callbackListenerFactory));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _authorizationTimeout = authorizationTimeout ?? DefaultAuthorizationTimeout;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _ownedHttpClient = ownsHttpClient ? httpClient as IDisposable : null;

        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (_authorizationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout));
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = GetConfigurationOrThrow();
            var tokenSet = await LoadBoundTokenSetAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (tokenSet is null)
            {
                throw new RemoteHubAuthenticationRequiredException();
            }

            if (tokenSet.ExpiresAt > _utcNow().Add(RefreshSkew))
            {
                return tokenSet.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(tokenSet.RefreshToken))
            {
                throw new RemoteHubAuthenticationRequiredException();
            }

            try
            {
                var discovery = await GetDiscoveryAsync(configuration, cancellationToken).ConfigureAwait(false);
                var refreshed = await ExchangeTokenAsync(
                    discovery.TokenEndpoint,
                    configuration,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = tokenSet.RefreshToken,
                        ["client_id"] = configuration.ClientId
                    },
                    tokenSet.RefreshToken,
                    isRefreshGrant: true,
                    cancellationToken).ConfigureAwait(false);
                await SaveTokenSetAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return refreshed.AccessToken;
            }
            catch (RemoteHubRefreshTokenRejectedException)
            {
                await ClearTokenSetQuietlyAsync(cancellationToken).ConfigureAwait(false);
                throw new RemoteHubAuthenticationRequiredException();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = GetConfigurationOrThrow();
            var discovery = await GetDiscoveryAsync(configuration, cancellationToken).ConfigureAwait(false);

            using var listener = _callbackListenerFactory.Create();
            Uri redirectUri;
            try
            {
                redirectUri = listener.Start();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RemoteHubOidcException("A secure local sign-in callback could not be started.");
            }

            if (!IsValidLoopbackRedirectUri(redirectUri))
            {
                throw new RemoteHubOidcException("A secure local sign-in callback could not be started.");
            }

            var state = CreateRandomUrlSafeValue(32);
            var verifier = CreateRandomUrlSafeValue(32);
            var challenge = CreatePkceChallenge(verifier);
            var authorizationUri = BuildAuthorizationUri(discovery.AuthorizationEndpoint, configuration, redirectUri, state, challenge);

            using var authorizationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authorizationDeadline.CancelAfter(_authorizationTimeout);
            try
            {
                await _browser.OpenAsync(authorizationUri, authorizationDeadline.Token).ConfigureAwait(false);
                var callback = await listener.WaitForCallbackAsync(authorizationDeadline.Token).ConfigureAwait(false);
                var code = ValidateCallbackAndGetCode(callback, redirectUri, state);
                var tokenSet = await ExchangeTokenAsync(
                    discovery.TokenEndpoint,
                    configuration,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = redirectUri.AbsoluteUri,
                        ["client_id"] = configuration.ClientId,
                        ["code_verifier"] = verifier
                    },
                    priorRefreshToken: null,
                    isRefreshGrant: false,
                    authorizationDeadline.Token).ConfigureAwait(false);
                await SaveTokenSetAsync(tokenSet, authorizationDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RemoteHubOidcException("The sign-in request timed out or was cancelled before completion.");
            }
            catch (RemoteHubOidcException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RemoteHubOidcException("The self-hosted sign-in could not be completed.");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _tokenStorage.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new RemoteHubOidcException("The secure local sign-in session could not be cleared.");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _ownedHttpClient?.Dispose();
    }

    private RemoteHubOidcConfiguration GetConfigurationOrThrow()
    {
        var configuration = _getConfiguration();
        if (configuration is null)
        {
            throw new RemoteHubOidcException("Configure a valid self-hosted HTTPS identity issuer and client ID before signing in.");
        }

        return configuration;
    }

    private async Task<RemoteHubTokenSet?> LoadBoundTokenSetAsync(
        RemoteHubOidcConfiguration configuration,
        CancellationToken cancellationToken)
    {
        RemoteHubTokenSet? tokenSet;
        try
        {
            tokenSet = await _tokenStorage.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RemoteHubOidcException("The secure local sign-in session could not be read.");
        }

        return tokenSet is not null && tokenSet.IsBoundTo(configuration)
            ? tokenSet
            : null;
    }

    private async Task SaveTokenSetAsync(RemoteHubTokenSet tokenSet, CancellationToken cancellationToken)
    {
        try
        {
            await _tokenStorage.SaveAsync(tokenSet, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RemoteHubOidcException("The secure local sign-in session could not be saved.");
        }
    }

    private async Task ClearTokenSetQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tokenStorage.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed cleanup must not expose token storage details in the tray UI.
        }
    }

    private async Task<RemoteHubOidcDiscovery> GetDiscoveryAsync(
        RemoteHubOidcConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (_cachedDiscovery is not null && _cachedDiscovery.IsFor(configuration))
        {
            return _cachedDiscovery;
        }

        var discoveryUri = new Uri(
            configuration.Issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration",
            UriKind.Absolute);
        RemoteHubOidcHttpResponse response;
        try
        {
            response = await InvokeWithRequestTimeoutAsync(
                token => _httpClient.GetAsync(discoveryUri, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteHubOidcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RemoteHubOidcException("The self-hosted identity service could not be reached.");
        }

        if (response.StatusCode is < 200 or >= 300
            || !RemoteHubOidcDiscovery.TryParse(response.Body, configuration, out var discovery))
        {
            throw new RemoteHubOidcException("The self-hosted identity service returned an invalid configuration.");
        }

        _cachedDiscovery = discovery;
        return discovery;
    }

    private async Task<RemoteHubTokenSet> ExchangeTokenAsync(
        Uri tokenEndpoint,
        RemoteHubOidcConfiguration configuration,
        IReadOnlyDictionary<string, string> formValues,
        string? priorRefreshToken,
        bool isRefreshGrant,
        CancellationToken cancellationToken)
    {
        RemoteHubOidcHttpResponse response;
        try
        {
            response = await InvokeWithRequestTimeoutAsync(
                token => _httpClient.PostFormAsync(tokenEndpoint, formValues, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteHubOidcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RemoteHubOidcException("The self-hosted identity service could not complete the sign-in request.");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            if (isRefreshGrant && IsInvalidGrantResponse(response.Body))
            {
                throw new RemoteHubRefreshTokenRejectedException();
            }

            throw new RemoteHubOidcException("The self-hosted identity service rejected the sign-in request.");
        }

        if (!RemoteHubTokenResponseParser.TryParse(
                response.Body,
                configuration,
                priorRefreshToken,
                _utcNow(),
                out var tokenSet))
        {
            throw new RemoteHubOidcException("The self-hosted identity service returned an invalid sign-in response.");
        }

        return tokenSet;
    }

    private async Task<T> InvokeWithRequestTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteHubOidcException("The self-hosted identity service did not respond in time.");
        }
    }

    private static Uri BuildAuthorizationUri(
        Uri authorizationEndpoint,
        RemoteHubOidcConfiguration configuration,
        Uri redirectUri,
        string state,
        string challenge)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = configuration.RequestedScopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (configuration.RequireFreshAuthentication)
        {
            // These force a new PKCE sign-in for ticket issuance and are not
            // used by the lower-privilege fleet inventory client.
            query["prompt"] = "login";
            query["max_age"] = "0";
        }

        var builder = new UriBuilder(authorizationEndpoint)
        {
            Query = string.Join("&", query.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        return builder.Uri;
    }

    private static string ValidateCallbackAndGetCode(Uri callback, Uri redirectUri, string expectedState)
    {
        if (!IsExpectedCallbackUri(callback, redirectUri)
            || !TryParseQuery(callback.Query, out var query)
            || query.ContainsKey("error")
            || !query.TryGetValue("state", out var returnedState)
            || !FixedTimeEquals(expectedState, returnedState)
            || !query.TryGetValue("code", out var code)
            || string.IsNullOrWhiteSpace(code)
            || code.Length > 4096
            || code.Any(char.IsControl))
        {
            throw new RemoteHubOidcException("The sign-in callback could not be verified.");
        }

        return code;
    }

    private static bool IsExpectedCallbackUri(Uri callback, Uri redirectUri)
    {
        return callback.IsAbsoluteUri
            && string.Equals(callback.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callback.Host, "127.0.0.1", StringComparison.Ordinal)
            && callback.Port == redirectUri.Port
            && string.Equals(callback.AbsolutePath, redirectUri.AbsolutePath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(callback.UserInfo)
            && string.IsNullOrEmpty(callback.Fragment);
    }

    private static bool IsValidLoopbackRedirectUri(Uri redirectUri)
    {
        return redirectUri.IsAbsoluteUri
            && string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(redirectUri.Host, "127.0.0.1", StringComparison.Ordinal)
            && redirectUri.Port is > 0 and <= 65535
            && string.Equals(redirectUri.AbsolutePath, "/", StringComparison.Ordinal)
            && string.IsNullOrEmpty(redirectUri.UserInfo)
            && string.IsNullOrEmpty(redirectUri.Query)
            && string.IsNullOrEmpty(redirectUri.Fragment);
    }

    private static bool TryParseQuery(string queryText, out IReadOnlyDictionary<string, string> query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var source = queryText.StartsWith('?') ? queryText[1..] : queryText;
        if (string.IsNullOrEmpty(source))
        {
            query = values;
            return true;
        }

        foreach (var pair in source.Split('&', StringSplitOptions.None))
        {
            var separator = pair.IndexOf('=');
            var rawKey = separator < 0 ? pair : pair[..separator];
            var rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                query = null!;
                return false;
            }

            if (string.IsNullOrWhiteSpace(key)
                || key.Length > 256
                || value.Length > 16 * 1024
                || !values.TryAdd(key, value))
            {
                query = null!;
                return false;
            }
        }

        query = values;
        return true;
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static string CreateRandomUrlSafeValue(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        try
        {
            return Base64UrlEncode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string CreatePkceChallenge(string verifier)
    {
        var verifierBytes = Encoding.ASCII.GetBytes(verifier);
        try
        {
            var hash = SHA256.HashData(verifierBytes);
            try
            {
                return Base64UrlEncode(hash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifierBytes);
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsInvalidGrantResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RemoteHubOidcAccessTokenProvider));
        }
    }
}

internal static class RemoteHubOidcEndpointValidator
{
    public static bool TryGetIssuer(string? value, out Uri issuer)
    {
        return TryGetStrictHttpsUri(value, out issuer);
    }

    public static bool TryGetStrictHttpsEndpoint(string? value, out Uri endpoint)
    {
        return TryGetStrictHttpsUri(value, out endpoint);
    }

    public static bool IsSafeHttpsAuthorizationUri(Uri? uri)
    {
        return uri is not null
            && uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && !RemoteClientPreferences.IsHostedTailscaleHost(uri.Host);
    }

    private static bool TryGetStrictHttpsUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !candidate.IsAbsoluteUri
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || RemoteClientPreferences.IsHostedTailscaleHost(candidate.Host))
        {
            return false;
        }

        uri = candidate;
        return true;
    }
}

internal sealed class RemoteHubOidcDiscovery
{
    private RemoteHubOidcDiscovery(Uri issuer, Uri authorizationEndpoint, Uri tokenEndpoint)
    {
        Issuer = issuer;
        AuthorizationEndpoint = authorizationEndpoint;
        TokenEndpoint = tokenEndpoint;
    }

    public Uri Issuer { get; }

    public Uri AuthorizationEndpoint { get; }

    public Uri TokenEndpoint { get; }

    public bool IsFor(RemoteHubOidcConfiguration configuration)
    {
        return Uri.Compare(
                   Issuer,
                   configuration.Issuer,
                   UriComponents.HttpRequestUrl,
                   UriFormat.UriEscaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    public static bool TryParse(
        string? json,
        RemoteHubOidcConfiguration configuration,
        out RemoteHubOidcDiscovery discovery)
    {
        discovery = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "issuer", out var issuerValue)
                || !RemoteHubOidcEndpointValidator.TryGetIssuer(issuerValue, out var issuer)
                || Uri.Compare(
                    issuer,
                    configuration.Issuer,
                    UriComponents.HttpRequestUrl,
                    UriFormat.UriEscaped,
                    StringComparison.OrdinalIgnoreCase) != 0
                || !TryGetString(root, "authorization_endpoint", out var authorizationValue)
                || !RemoteHubOidcEndpointValidator.TryGetStrictHttpsEndpoint(authorizationValue, out var authorizationEndpoint)
                || !TryGetString(root, "token_endpoint", out var tokenValue)
                || !RemoteHubOidcEndpointValidator.TryGetStrictHttpsEndpoint(tokenValue, out var tokenEndpoint))
            {
                return false;
            }

            discovery = new RemoteHubOidcDiscovery(issuer, authorizationEndpoint, tokenEndpoint);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString());
    }
}

internal static class RemoteHubTokenResponseParser
{
    public static bool TryParse(
        string? json,
        RemoteHubOidcConfiguration configuration,
        string? priorRefreshToken,
        DateTimeOffset now,
        out RemoteHubTokenSet tokenSet)
    {
        tokenSet = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetTokenString(root, "access_token", out var accessToken)
                || !TryGetBearerTokenType(root)
                || !TryGetExpiresIn(root, out var expiresIn)
                || !TryGetOptionalTokenString(root, "refresh_token", out var returnedRefreshToken)
                || (!configuration.AllowRefreshTokens && returnedRefreshToken is not null))
            {
                return false;
            }

            tokenSet = new RemoteHubTokenSet(
                configuration.Issuer.AbsoluteUri,
                configuration.ClientId,
                accessToken!,
                returnedRefreshToken ?? priorRefreshToken,
                now.AddSeconds(expiresIn));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetTokenString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && IsSafeTokenValue(value = property.GetString());
    }

    private static bool TryGetOptionalTokenString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
            && IsSafeTokenValue(value = property.GetString());
    }

    private static bool TryGetBearerTokenType(JsonElement root)
    {
        return root.TryGetProperty("token_type", out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetExpiresIn(JsonElement root, out double seconds)
    {
        seconds = 0;
        if (!root.TryGetProperty("expires_in", out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out seconds))
        {
            return IsReasonableExpiration(seconds);
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out seconds))
        {
            return IsReasonableExpiration(seconds);
        }

        return false;
    }

    private static bool IsReasonableExpiration(double seconds)
    {
        return !double.IsNaN(seconds)
            && !double.IsInfinity(seconds)
            && seconds > 0
            && seconds <= TimeSpan.FromDays(31).TotalSeconds;
    }

    private static bool IsSafeTokenValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 16 * 1024
            && !value.Any(char.IsControl);
    }
}

internal sealed class DpapiCurrentUserRemoteHubTokenStorage : IRemoteHubTokenStorage
{
    private const string FileName = "remotehub-oidc.tokens";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StayActive.RemoteHub.Oidc.TokenStore.v1");
    private readonly string _directoryPath;
    private readonly string _filePath;

    public DpapiCurrentUserRemoteHubTokenStorage()
        : this(GetDefaultDirectoryPath())
    {
    }

    internal DpapiCurrentUserRemoteHubTokenStorage(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("A token-storage directory is required.", nameof(directoryPath));
        }

        _directoryPath = Path.GetFullPath(directoryPath);
        _filePath = Path.Combine(_directoryPath, FileName);
    }

    public async Task<RemoteHubTokenSet?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        byte[]? protectedBytes = null;
        byte[]? plaintextBytes = null;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            if (protectedBytes.Length == 0 || protectedBytes.Length > 128 * 1024)
            {
                return null;
            }

            plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var document = JsonSerializer.Deserialize<PersistedTokenDocument>(plaintextBytes);
            if (document is null)
            {
                return null;
            }

            return new RemoteHubTokenSet(
                document.Issuer ?? string.Empty,
                document.ClientId ?? string.Empty,
                document.AccessToken ?? string.Empty,
                document.RefreshToken,
                document.ExpiresAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (plaintextBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public async Task SaveAsync(RemoteHubTokenSet tokenSet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenSet);

        byte[]? plaintextBytes = null;
        byte[]? protectedBytes = null;
        try
        {
            plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(new PersistedTokenDocument
            {
                Issuer = tokenSet.Issuer,
                ClientId = tokenSet.ClientId,
                AccessToken = tokenSet.AccessToken,
                RefreshToken = tokenSet.RefreshToken,
                ExpiresAt = tokenSet.ExpiresAt
            });
            protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                Entropy,
                DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(_directoryPath);
            var temporaryPath = Path.Combine(_directoryPath, "." + FileName + "." + Path.GetRandomFileName());
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                // Both paths are on the same volume. File.Replace is atomic on the
                // supported Windows filesystem and keeps the prior ciphertext intact
                // until the complete replacement is committed.
                if (File.Exists(_filePath))
                {
                    try
                    {
                        File.Replace(temporaryPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (FileNotFoundException)
                    {
                        // A concurrent cleanup removed the previous file between the
                        // existence check and Replace; a same-directory move remains
                        // an atomic create in that case.
                        File.Move(temporaryPath, _filePath, overwrite: false);
                    }
                }
                else
                {
                    File.Move(temporaryPath, _filePath, overwrite: false);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            if (plaintextBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The caller will either retry later or report a generic storage error.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not surface a filesystem path from a credential-store cleanup.
        }
    }

    private static string GetDefaultDirectoryPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application-data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, "StayActive");
    }

    private sealed class PersistedTokenDocument
    {
        public string? Issuer { get; init; }

        public string? ClientId { get; init; }

        public string? AccessToken { get; init; }

        public string? RefreshToken { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }
    }
}

internal sealed class SystemRemoteHubOidcHttpClient : IRemoteHubOidcHttpClient, IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public SystemRemoteHubOidcHttpClient()
        : this(
            new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            }),
            ownsHttpClient: true)
    {
    }

    internal SystemRemoteHubOidcHttpClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<RemoteHubOidcHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureStrictHttpsEndpoint(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        return new RemoteHubOidcHttpResponse((int)response.StatusCode, await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RemoteHubOidcHttpResponse> PostFormAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        EnsureStrictHttpsEndpoint(uri);
        ArgumentNullException.ThrowIfNull(formValues);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formValues)
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        return new RemoteHubOidcHttpResponse((int)response.StatusCode, await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void EnsureStrictHttpsEndpoint(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!RemoteHubOidcEndpointValidator.TryGetStrictHttpsEndpoint(uri.AbsoluteUri, out _))
        {
            throw new RemoteHubOidcException("The identity service endpoint is not a valid HTTPS endpoint.");
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (result.Length + count > MaximumResponseBytes)
                {
                    throw new RemoteHubOidcException("The identity service response was too large.");
                }

                await result.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(result.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

internal sealed class SystemRemoteHubOidcBrowser : IRemoteHubOidcBrowser
{
    public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RemoteHubOidcEndpointValidator.IsSafeHttpsAuthorizationUri(authorizationUri))
        {
            throw new RemoteHubOidcException("The identity service authorization endpoint is not a valid HTTPS endpoint.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationUri.AbsoluteUri,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }
}

internal sealed class SystemRemoteHubLoopbackCallbackListenerFactory : IRemoteHubLoopbackCallbackListenerFactory
{
    public IRemoteHubLoopbackCallbackListener Create() => new SystemRemoteHubLoopbackCallbackListener();
}

internal sealed class SystemRemoteHubLoopbackCallbackListener : IRemoteHubLoopbackCallbackListener
{
    private static readonly byte[] CompletionPage = Encoding.UTF8.GetBytes(
        "<!doctype html><html><body><p>Sign-in received. You can return to StayActive.</p></body></html>");

    private HttpListener? _listener;

    public Uri Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The loopback callback listener has already started.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = GetAvailableLoopbackPort();
            // RFC 8252 loopback redirect registrations allow the client to use
            // an ephemeral port, but providers such as Keycloak still require
            // the registered path to match. Keep the path fixed at the root so
            // the native public client can register the narrow
            // http://127.0.0.1 callback rather than an over-broad path wildcard.
            // State and S256 PKCE bind the returned authorization response.
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                return new Uri(prefix, UriKind.Absolute);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new RemoteHubOidcException("A secure local sign-in callback could not be started.");
    }

    public async Task<Uri> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        var listener = _listener ?? throw new InvalidOperationException("The loopback callback listener has not started.");
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try
            {
                ((HttpListener)state!).Close();
            }
            catch (ObjectDisposedException)
            {
                // The listener was already cleaned up.
            }
        }, listener);

        try
        {
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            try
            {
                var callback = context.Request.Url
                    ?? throw new RemoteHubOidcException("The sign-in callback could not be verified.");
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = CompletionPage.Length;
                await context.Response.OutputStream.WriteAsync(CompletionPage.AsMemory(), cancellationToken).ConfigureAwait(false);
                return callback;
            }
            finally
            {
                context.Response.Close();
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose()
    {
        _listener?.Close();
        _listener = null;
    }

    private static int GetAvailableLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string CreateCallbackNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed class RemoteHubRefreshTokenRejectedException : RemoteHubOidcException
{
    public RemoteHubRefreshTokenRejectedException()
        : base("The saved RemoteHub sign-in session is no longer valid.")
    {
    }
}
