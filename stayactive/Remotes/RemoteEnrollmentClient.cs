using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StayActive.Remotes;

/// <summary>
/// The two server-defined enrollment profiles.  The desktop client cannot
/// choose tags, lifetime, reuse, or any other Headscale policy input.
/// </summary>
internal enum RemoteEnrollmentKind
{
    Device,
    ExitNode
}

internal enum RemoteEnrollmentTicketState
{
    Issued,
    Used,
    Expired,
    Revoked
}

/// <summary>
/// Safe enrollment-ticket information returned after issuance, lookup, or
/// revocation.  It deliberately has no authentication key or join command.
/// </summary>
internal sealed class RemoteEnrollmentTicket
{
    public RemoteEnrollmentTicket(
        string id,
        RemoteEnrollmentKind kind,
        DateTimeOffset expiresAtUtc,
        RemoteEnrollmentTicketState state,
        Uri loginServer,
        IReadOnlyList<string> advertiseTags)
    {
        Id = id;
        Kind = kind;
        ExpiresAtUtc = expiresAtUtc;
        State = state;
        LoginServer = loginServer;
        AdvertiseTags = advertiseTags;
    }

    public string Id { get; }

    public RemoteEnrollmentKind Kind { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public RemoteEnrollmentTicketState State { get; }

    public Uri LoginServer { get; }

    public IReadOnlyList<string> AdvertiseTags { get; }

    public override string ToString() => "[Remote enrollment ticket]";
}

/// <summary>
/// Exists only in memory while the Add a device dialog is open.  The command
/// is intentionally not part of <see cref="RemoteEnrollmentTicket"/>, because
/// status and audit paths must never be able to expose the one-time key again.
/// </summary>
internal sealed class RemoteEnrollmentIssuedTicket
{
    public RemoteEnrollmentIssuedTicket(RemoteEnrollmentTicket ticket, string joinCommand)
    {
        Ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        JoinCommand = joinCommand ?? throw new ArgumentNullException(nameof(joinCommand));
    }

    public RemoteEnrollmentTicket Ticket { get; }

    public string JoinCommand { get; }

    public override string ToString() => "[Remote enrollment command redacted]";
}

internal sealed class RemoteEnrollmentException : Exception
{
    public RemoteEnrollmentException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Retrieves an access token only after an interactive, fresh enrollment-admin
/// sign-in.  Implementations must not use the persisted fleet-read session.
/// </summary>
internal interface IRemoteEnrollmentAccessTokenProvider
{
    Task<string> GetFreshAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates a fresh, S256-PKCE enrollment session for each broker operation.
/// The token is held only long enough to authorize one HTTPS request.  It is
/// never written to DPAPI, settings, or a refresh-token store.
/// </summary>
internal sealed class FreshRemoteEnrollmentAccessTokenProvider : IRemoteEnrollmentAccessTokenProvider
{
    private readonly Func<RemoteHubOidcConfiguration?> _getConfiguration;

    public FreshRemoteEnrollmentAccessTokenProvider(Func<RemoteHubOidcConfiguration?> getConfiguration)
    {
        _getConfiguration = getConfiguration ?? throw new ArgumentNullException(nameof(getConfiguration));
    }

    public async Task<string> GetFreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = _getConfiguration();
        if (configuration is null)
        {
            throw new RemoteEnrollmentException(
                "Configure the self-hosted identity issuer and enrollment public client before adding a device.");
        }

        var storage = new EphemeralRemoteHubTokenStorage();
        using var provider = new RemoteHubOidcAccessTokenProvider(
            () => configuration,
            storage,
            new SystemRemoteHubOidcHttpClient(),
            new SystemRemoteHubOidcBrowser(),
            new SystemRemoteHubLoopbackCallbackListenerFactory(),
            ownsHttpClient: true);
        try
        {
            await provider.SignInAsync(cancellationToken).ConfigureAwait(false);
            return await provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteHubOidcException exception)
        {
            throw new RemoteEnrollmentException(exception.Message);
        }
        finally
        {
            try
            {
                // This clears the in-memory token set even if the broker call
                // later fails.  Enrollment configuration rejects refresh tokens.
                await provider.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A best-effort memory cleanup must not mask the safe OIDC error.
            }
        }
    }

    private sealed class EphemeralRemoteHubTokenStorage : IRemoteHubTokenStorage
    {
        private RemoteHubTokenSet? _value;

        public Task<RemoteHubTokenSet?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_value);
        }

        public Task SaveAsync(RemoteHubTokenSet tokenSet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value = tokenSet ?? throw new ArgumentNullException(nameof(tokenSet));
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value = null;
            return Task.CompletedTask;
        }
    }
}

internal interface IRemoteEnrollmentClient : IDisposable
{
    Task<RemoteEnrollmentIssuedTicket> IssueAsync(RemoteEnrollmentKind kind, CancellationToken cancellationToken);

    Task<RemoteEnrollmentTicket> GetStatusAsync(string ticketId, CancellationToken cancellationToken);

    Task<RemoteEnrollmentTicket> RevokeAsync(string ticketId, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the owner-operated EnrollmentBroker.  The broker, not the tray,
/// holds the Headscale API key and fixes one-use keys, tags, and expiry.
/// </summary>
internal sealed class RemoteEnrollmentClient : IRemoteEnrollmentClient
{
    internal const int FixedLifetimeMinutes = 15;
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly TimeSpan ExpectedTicketLifetime = TimeSpan.FromMinutes(FixedLifetimeMinutes);
    // The broker creates the key immediately before its response. Keep a small
    // allowance for clock skew and response transit, but reject a broker that
    // tries to extend or shorten the fixed 15-minute policy.
    private static readonly TimeSpan TicketLifetimeClockSkew = TimeSpan.FromSeconds(30);

    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly IRemoteEnrollmentAccessTokenProvider _accessTokenProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTimeOffset> _utcNow;

    public RemoteEnrollmentClient(Func<RemoteClientPreferences> getPreferences)
        : this(
            getPreferences,
            new FreshRemoteEnrollmentAccessTokenProvider(() =>
            {
                var preferences = getPreferences();
                return RemoteHubOidcConfiguration.TryCreateEnrollment(
                    preferences.RemoteHubOidcIssuerUrl,
                    preferences.RemoteEnrollmentOidcClientId,
                    out var configuration)
                    ? configuration
                    : null;
            }),
            CreateDefaultHttpClient(),
            ownsHttpClient: true)
    {
    }

    internal RemoteEnrollmentClient(
        Func<RemoteClientPreferences> getPreferences,
        IRemoteEnrollmentAccessTokenProvider accessTokenProvider,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        Func<DateTimeOffset>? utcNow = null)
    {
        _getPreferences = getPreferences ?? throw new ArgumentNullException(nameof(getPreferences));
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<RemoteEnrollmentIssuedTicket> IssueAsync(
        RemoteEnrollmentKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTicketsEndpoint(_getPreferences().RemoteEnrollmentUrl, out var endpoint))
        {
            throw new RemoteEnrollmentException("Configure an HTTPS URL for your self-hosted enrollment broker first.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            kind = ToBrokerKind(kind),
            lifetimeMinutes = FixedLifetimeMinutes
        });
        var response = await SendAsync(HttpMethod.Post, endpoint, payload, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw CreateBrokerException(response.StatusCode, "issue a one-time enrollment command");
        }

        if (!TryParseIssuedTicket(response.Body, kind, _utcNow(), out var issuedTicket))
        {
            throw new RemoteEnrollmentException("The enrollment broker returned an invalid one-time enrollment response.");
        }

        return issuedTicket;
    }

    public async Task<RemoteEnrollmentTicket> GetStatusAsync(string ticketId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTicketEndpoint(_getPreferences().RemoteEnrollmentUrl, ticketId, out var endpoint))
        {
            throw new RemoteEnrollmentException("The enrollment ticket or broker URL is invalid.");
        }

        var response = await SendAsync(HttpMethod.Get, endpoint, body: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw CreateBrokerException(response.StatusCode, "check the enrollment ticket status");
        }

        if (!TryParseSafeTicket(response.Body, _utcNow(), out var ticket)
            || !string.Equals(ticket.Id, ticketId, StringComparison.Ordinal))
        {
            throw new RemoteEnrollmentException("The enrollment broker returned an invalid ticket status.");
        }

        return ticket;
    }

    public async Task<RemoteEnrollmentTicket> RevokeAsync(string ticketId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTicketEndpoint(_getPreferences().RemoteEnrollmentUrl, ticketId, out var endpoint))
        {
            throw new RemoteEnrollmentException("The enrollment ticket or broker URL is invalid.");
        }

        var response = await SendAsync(HttpMethod.Delete, endpoint, body: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Conflict)
        {
            throw CreateBrokerException(response.StatusCode, "revoke the enrollment ticket");
        }

        if (!TryParseSafeTicket(response.Body, _utcNow(), out var ticket)
            || !string.Equals(ticket.Id, ticketId, StringComparison.Ordinal)
            || (response.StatusCode == HttpStatusCode.Conflict && ticket.State != RemoteEnrollmentTicketState.Used))
        {
            throw new RemoteEnrollmentException("The enrollment broker returned an invalid revocation status.");
        }

        return ticket;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static bool TryGetTicketsEndpoint(string configuredUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (!TryGetSafeBrokerBaseUri(configuredUrl, out var brokerBase))
        {
            return false;
        }

        endpoint = new UriBuilder(brokerBase)
        {
            Path = brokerBase.AbsolutePath.TrimEnd('/') + "/api/v1/enrollment-tickets",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        return true;
    }

    internal static bool TryGetTicketEndpoint(string configuredUrl, string ticketId, out Uri endpoint)
    {
        endpoint = null!;
        if (!IsSafeTicketId(ticketId) || !TryGetTicketsEndpoint(configuredUrl, out var ticketsEndpoint))
        {
            return false;
        }

        endpoint = new UriBuilder(ticketsEndpoint)
        {
            Path = ticketsEndpoint.AbsolutePath.TrimEnd('/') + "/" + ticketId,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        return true;
    }

    private async Task<BrokerResponse> SendAsync(
        HttpMethod method,
        Uri endpoint,
        string? body,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetFreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteEnrollmentException)
        {
            throw;
        }
        catch
        {
            throw new RemoteEnrollmentException("A fresh enrollment sign-in could not be completed.");
        }

        if (!IsSafeBearerToken(accessToken))
        {
            throw new RemoteEnrollmentException("The enrollment sign-in did not return a valid access token.");
        }

        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            // Only successful ticket representations can be useful to the UI.
            // Do not retain or parse arbitrary error bodies, which can contain
            // deployment diagnostics and are never shown to the user anyway.
            var hasTicketRepresentation = response.StatusCode is HttpStatusCode.Created
                or HttpStatusCode.OK
                or HttpStatusCode.Conflict;
            if (!hasTicketRepresentation)
            {
                return new BrokerResponse(response.StatusCode, string.Empty);
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw new RemoteEnrollmentException("The enrollment broker returned an oversized response.");
            }

            var responseBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return new BrokerResponse(response.StatusCode, responseBody);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteEnrollmentException)
        {
            throw;
        }
        catch
        {
            // Do not expose a broker response body, server internals, endpoint
            // routing, or a possibly sensitive issuance payload in the tray.
            throw new RemoteEnrollmentException("The self-hosted enrollment broker could not be reached.");
        }
    }

    private static RemoteEnrollmentException CreateBrokerException(HttpStatusCode statusCode, string action)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new RemoteEnrollmentException("The enrollment broker requires a fresh enrollment-administrator sign-in.")
            : new RemoteEnrollmentException($"The self-hosted enrollment broker could not {action}.");
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (result.Length + read > MaximumResponseBytes)
                {
                    throw new RemoteEnrollmentException("The enrollment broker returned an oversized response.");
                }

                await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            var bytes = result.ToArray();
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool TryParseIssuedTicket(
        string json,
        RemoteEnrollmentKind requestedKind,
        DateTimeOffset now,
        out RemoteEnrollmentIssuedTicket issuedTicket)
    {
        issuedTicket = null!;
        if (!TryParseTicketDocument(json, now, requireAuthKey: true, out var ticket, out var authKey)
            || ticket.Kind != requestedKind
            || ticket.State != RemoteEnrollmentTicketState.Issued
            || !HasExpectedTicketLifetime(ticket.ExpiresAtUtc, now)
            || !IsSafeAuthKey(authKey))
        {
            return false;
        }

        issuedTicket = new RemoteEnrollmentIssuedTicket(ticket, BuildJoinCommand(ticket.LoginServer, authKey!));
        return true;
    }

    private static bool HasExpectedTicketLifetime(DateTimeOffset expiresAtUtc, DateTimeOffset now)
    {
        var expectedExpiry = now.Add(ExpectedTicketLifetime);
        return expiresAtUtc >= expectedExpiry.Subtract(TicketLifetimeClockSkew)
            && expiresAtUtc <= expectedExpiry.Add(TicketLifetimeClockSkew);
    }

    private static bool TryParseSafeTicket(string json, DateTimeOffset now, out RemoteEnrollmentTicket ticket)
    {
        return TryParseTicketDocument(json, now, requireAuthKey: false, out ticket, out _);
    }

    private static bool TryParseTicketDocument(
        string? json,
        DateTimeOffset now,
        bool requireAuthKey,
        out RemoteEnrollmentTicket ticket,
        out string? authKey)
    {
        ticket = null!;
        authKey = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ticket", out var ticketElement)
                || ticketElement.ValueKind != JsonValueKind.Object
                || ticketElement.TryGetProperty("authKey", out _)
                || !TryGetSafeString(ticketElement, "id", 128, out var id)
                || !IsSafeTicketId(id!)
                || !TryGetSafeString(ticketElement, "kind", 32, out var kindValue)
                || !TryParseKind(kindValue, out var kind)
                || !TryGetSafeString(ticketElement, "status", 32, out var statusValue)
                || !TryParseState(statusValue, out var state)
                || !TryGetSafeString(ticketElement, "expiresAtUtc", 64, out var expiresAtValue)
                || !DateTimeOffset.TryParse(
                    expiresAtValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresAtUtc)
                || !TryGetSafeString(ticketElement, "loginServer", 2048, out var loginServerValue)
                || !TryGetSelfHostedLoginServer(loginServerValue, out var loginServer)
                || !TryParseFixedTags(ticketElement, kind, out var tags))
            {
                return false;
            }

            var hasAuthKey = root.TryGetProperty("authKey", out var authKeyProperty);
            if (requireAuthKey)
            {
                if (!hasAuthKey
                    || authKeyProperty.ValueKind != JsonValueKind.String
                    || !IsSafeAuthKey(authKey = authKeyProperty.GetString()))
                {
                    return false;
                }
            }
            else if (hasAuthKey)
            {
                // A status/revocation response must never carry a reusable
                // credential. Fail closed rather than accidentally displaying it.
                return false;
            }

            var normalizedExpiry = expiresAtUtc.ToUniversalTime();
            if (normalizedExpiry < now.AddDays(-31) || normalizedExpiry > now.AddDays(31))
            {
                return false;
            }

            ticket = new RemoteEnrollmentTicket(
                id!,
                kind,
                normalizedExpiry,
                state,
                loginServer,
                tags);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseFixedTags(
        JsonElement root,
        RemoteEnrollmentKind kind,
        out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        if (!root.TryGetProperty("advertiseTags", out var tagArray)
            || tagArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var item in tagArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = item.GetString();
            if (value is not "tag:stayactive" and not "tag:stayactive-exit"
                || parsed.Contains(value, StringComparer.Ordinal))
            {
                return false;
            }

            parsed.Add(value);
        }

        var expected = kind == RemoteEnrollmentKind.Device
            ? new[] { "tag:stayactive" }
            : new[] { "tag:stayactive", "tag:stayactive-exit" };
        if (parsed.Count != expected.Length || !parsed.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        tags = parsed;
        return true;
    }

    private static bool TryGetSafeBrokerBaseUri(string value, out Uri brokerBase)
    {
        brokerBase = null!;
        if (!RemoteClientPreferences.IsSelfHostedEndpoint(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        brokerBase = candidate;
        return true;
    }

    private static bool TryGetSelfHostedLoginServer(string? value, out Uri loginServer)
    {
        loginServer = null!;
        if (!RemoteClientPreferences.IsSelfHostedControlPlane(value ?? string.Empty)
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || string.IsNullOrEmpty(candidate.Host)
            || !string.Equals(candidate.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        loginServer = candidate;
        return true;
    }

    private static bool TryGetSafeString(JsonElement root, string propertyName, int maxLength, out string? value)
    {
        value = null;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString())
            && value.Length <= maxLength
            && !value.Any(char.IsControl);
    }

    private static bool TryParseKind(string? value, out RemoteEnrollmentKind kind)
    {
        kind = default;
        if (string.Equals(value, "device", StringComparison.OrdinalIgnoreCase))
        {
            kind = RemoteEnrollmentKind.Device;
            return true;
        }

        if (string.Equals(value, "exitNode", StringComparison.OrdinalIgnoreCase))
        {
            kind = RemoteEnrollmentKind.ExitNode;
            return true;
        }

        return false;
    }

    private static bool TryParseState(string? value, out RemoteEnrollmentTicketState state)
    {
        state = default;
        if (string.Equals(value, "issued", StringComparison.OrdinalIgnoreCase))
        {
            state = RemoteEnrollmentTicketState.Issued;
            return true;
        }

        if (string.Equals(value, "redeemed", StringComparison.OrdinalIgnoreCase))
        {
            state = RemoteEnrollmentTicketState.Used;
            return true;
        }

        if (string.Equals(value, "expired", StringComparison.OrdinalIgnoreCase))
        {
            state = RemoteEnrollmentTicketState.Expired;
            return true;
        }

        if (string.Equals(value, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            state = RemoteEnrollmentTicketState.Revoked;
            return true;
        }

        return false;
    }

    private static bool IsSafeTicketId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsSafeAuthKey(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 4096
            && !value.StartsWith("-", StringComparison.Ordinal)
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsSafeBearerToken(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 16 * 1024
            && !value.Any(char.IsControl);
    }

    private static string ToBrokerKind(RemoteEnrollmentKind kind)
    {
        return kind switch
        {
            RemoteEnrollmentKind.Device => "device",
            RemoteEnrollmentKind.ExitNode => "exitNode",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string BuildJoinCommand(Uri loginServer, string authKey)
    {
        return "tailscale up --login-server " + loginServer.AbsoluteUri.TrimEnd('/') + " --auth-key " + authKey;
    }

    private sealed record BrokerResponse(HttpStatusCode StatusCode, string Body)
    {
        public override string ToString() => "[Enrollment broker response redacted]";
    }
}