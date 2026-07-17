using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Globalization;

namespace StayActive.Remotes;

internal enum RemoteHubInventoryState
{
    NotConfigured,
    AuthenticationRequired,
    Available,
    Unavailable
}

internal sealed record RemoteHubDeviceMetadata(
    string HeadscaleNodeId,
    string? OwnerDisplayName,
    string? CoarseLocation,
    string? MeshCentralNodeId,
    bool IsVerified,
    RemoteCapability AllowedCapabilities,
    long Version,
    DateTimeOffset UpdatedAtUtc);

internal sealed record RemoteHubInventorySnapshot(
    RemoteHubInventoryState State,
    string StatusMessage,
    IReadOnlyDictionary<string, RemoteHubDeviceMetadata> Devices,
    DateTimeOffset RefreshedAt)
{
    public static RemoteHubInventorySnapshot NotConfigured { get; } = new(
        RemoteHubInventoryState.NotConfigured,
        "RemoteHub is not configured.",
        new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal),
        DateTimeOffset.MinValue);
}

internal interface IRemoteHubInventoryClient : IDisposable
{
    RemoteHubInventorySnapshot GetCachedSnapshot();

    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the metadata-only RemoteHub fleet inventory. The service receives
/// an OIDC access token from the local provider but no Headscale, MeshCentral,
/// device-enrollment, or impersonation secret is ever sent to this client.
/// </summary>
internal sealed class RemoteHubFleetClient : IRemoteHubInventoryClient
{
    private const int MaxInventoryBytes = 1_048_576;

    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly IRemoteHubAccessTokenProvider _accessTokenProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _snapshotLock = new();
    private RemoteHubInventorySnapshot _snapshot = RemoteHubInventorySnapshot.NotConfigured;

    public RemoteHubFleetClient(
        Func<RemoteClientPreferences> getPreferences,
        IRemoteHubAccessTokenProvider accessTokenProvider)
        : this(getPreferences, accessTokenProvider, CreateDefaultHttpClient(), ownsHttpClient: true)
    {
    }

    internal RemoteHubFleetClient(
        Func<RemoteClientPreferences> getPreferences,
        IRemoteHubAccessTokenProvider accessTokenProvider,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _getPreferences = getPreferences ?? throw new ArgumentNullException(nameof(getPreferences));
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public RemoteHubInventorySnapshot GetCachedSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetFleetEndpoint(_getPreferences().RemoteHubUrl, out var fleetEndpoint))
        {
            SetSnapshot(RemoteHubInventorySnapshot.NotConfigured);
            return;
        }

        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            SetSnapshot(new RemoteHubInventorySnapshot(
                RemoteHubInventoryState.AuthenticationRequired,
                "Sign in to your self-hosted RemoteHub to load device metadata.",
                EmptyDeviceMap(),
                DateTimeOffset.UtcNow));
            return;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            SetSnapshot(new RemoteHubInventorySnapshot(
                RemoteHubInventoryState.AuthenticationRequired,
                "Sign in to your self-hosted RemoteHub to load device metadata.",
                EmptyDeviceMap(),
                DateTimeOffset.UtcNow));
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, fleetEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SetSnapshot(new RemoteHubInventorySnapshot(
                    RemoteHubInventoryState.AuthenticationRequired,
                    "RemoteHub requires a current sign-in with fleet-read permission.",
                    EmptyDeviceMap(),
                    DateTimeOffset.UtcNow));
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                SetUnavailable();
                return;
            }

            if (response.Content.Headers.ContentLength is > MaxInventoryBytes)
            {
                SetUnavailable();
                return;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await ReadInventoryDocumentAsync(contentStream, cancellationToken).ConfigureAwait(false);
            if (!TryParseInventory(document.RootElement, out var devices))
            {
                SetUnavailable();
                return;
            }

            SetSnapshot(new RemoteHubInventorySnapshot(
                RemoteHubInventoryState.Available,
                "RemoteHub metadata is current.",
                devices,
                DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Do not expose server response bodies or transport exception details:
            // they can reveal server internals or authentication state in a tray.
            SetUnavailable();
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static bool TryGetFleetEndpoint(string configuredUrl, out Uri fleetEndpoint)
    {
        fleetEndpoint = null!;
        if (!RemoteClientPreferences.IsSelfHostedEndpoint(configuredUrl)
            || !Uri.TryCreate(configuredUrl, UriKind.Absolute, out var remoteHub))
        {
            return false;
        }

        var builder = new UriBuilder(remoteHub)
        {
            Path = remoteHub.AbsolutePath.TrimEnd('/') + "/api/v1/fleet",
            Query = string.Empty,
            Fragment = string.Empty
        };
        fleetEndpoint = builder.Uri;
        return true;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            // Never forward an OIDC bearer token to a redirect target. A valid
            // RemoteHub fleet endpoint returns JSON directly over its configured
            // self-hosted HTTPS origin.
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static async Task<JsonDocument> ReadInventoryDocumentAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var readBuffer = new byte[16_384];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxInventoryBytes)
            {
                throw new InvalidDataException("The RemoteHub inventory response is too large.");
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    private static bool TryParseInventory(
        JsonElement root,
        out IReadOnlyDictionary<string, RemoteHubDeviceMetadata> devices)
    {
        devices = EmptyDeviceMap();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("devices", out var deviceArray)
            || deviceArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal);
        foreach (var item in deviceArray.EnumerateArray())
        {
            if (!TryParseDevice(item, out var device) || !parsed.TryAdd(device.HeadscaleNodeId, device))
            {
                return false;
            }
        }

        devices = parsed;
        return true;
    }

    private static bool TryParseDevice(JsonElement item, out RemoteHubDeviceMetadata device)
    {
        device = null!;
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetSafeString(item, "headscaleNodeId", required: true, maxLength: 256, out var headscaleNodeId)
            || !TryGetOptInString(item, "ownerDisplayNameOptIn", "ownerDisplayName", 256, out var ownerDisplayName)
            || !TryGetOptInString(item, "coarseLocationOptIn", "coarseLocation", 256, out var coarseLocation)
            || !TryGetSafeString(item, "meshCentralNodeId", required: false, maxLength: 512, out var meshCentralNodeId)
            || !item.TryGetProperty("verified", out var verified)
            || verified.ValueKind is not JsonValueKind.True and not JsonValueKind.False
            || !item.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt64(out var versionNumber)
            || versionNumber < 0
            || !item.TryGetProperty("updatedAtUtc", out var updatedAt)
            || updatedAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                updatedAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var updatedAtUtc)
            || !TryParseCapabilities(item, out var capabilities))
        {
            return false;
        }

        device = new RemoteHubDeviceMetadata(
            headscaleNodeId!,
            ownerDisplayName,
            coarseLocation,
            meshCentralNodeId,
            verified.GetBoolean(),
            capabilities,
            versionNumber,
            updatedAtUtc.ToUniversalTime());
        return true;
    }

    private static bool TryGetOptInString(
        JsonElement item,
        string optInPropertyName,
        string valuePropertyName,
        int maxLength,
        out string? value)
    {
        value = null;
        if (!item.TryGetProperty(optInPropertyName, out var optIn)
            || optIn.ValueKind is not JsonValueKind.True and not JsonValueKind.False
            || !item.TryGetProperty(valuePropertyName, out var metadataValue))
        {
            return false;
        }

        if (!optIn.GetBoolean())
        {
            return metadataValue.ValueKind == JsonValueKind.Null;
        }

        if (metadataValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = metadataValue.GetString();
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && !value.Any(char.IsControl);
    }

    private static bool TryParseCapabilities(JsonElement item, out RemoteCapability capabilities)
    {
        capabilities = RemoteCapability.None;
        if (!item.TryGetProperty("allowedCapabilities", out var capabilityArray)
            || capabilityArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var capability in capabilityArray.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String
                || !Enum.TryParse<RemoteCapability>(capability.GetString(), ignoreCase: false, out var parsed)
                || parsed is RemoteCapability.None or RemoteCapability.LocalAuthenticatorApproval)
            {
                return false;
            }

            capabilities |= parsed;
        }

        return true;
    }

    private static bool TryGetSafeString(
        JsonElement item,
        string propertyName,
        bool required,
        int maxLength,
        out string? value)
    {
        value = null;
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return !required;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && !value.Any(char.IsControl);
    }

    private static IReadOnlyDictionary<string, RemoteHubDeviceMetadata> EmptyDeviceMap()
    {
        return new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal);
    }

    private void SetUnavailable()
    {
        SetSnapshot(new RemoteHubInventorySnapshot(
            RemoteHubInventoryState.Unavailable,
            "RemoteHub metadata is unavailable; remote screen and file actions are disabled.",
            EmptyDeviceMap(),
            DateTimeOffset.UtcNow));
    }

    private void SetSnapshot(RemoteHubInventorySnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }
    }
}
