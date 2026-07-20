namespace StayActive.Remotes;

[Flags]
internal enum RemoteCapability
{
    None = 0,
    ExitNode = 1 << 0,
    ScreenView = 1 << 1,
    SendFile = 1 << 2,
    RequestFile = 1 << 3,
    LocalAuthenticatorApproval = 1 << 4
}

internal enum RemoteFleetConnectionState
{
    NotConfigured,
    Connecting,
    Connected,
    Degraded,
    Disconnected
}

internal sealed record RemoteDevice(
    string Id,
    string DeviceName,
    string OwnerDisplayName,
    string? Location,
    bool IsOnline,
    bool IsVerified,
    DateTimeOffset? LastSeenAt,
    RemoteCapability Capabilities,
    string? TailnetIp = null,
    string? MeshCentralNodeId = null)
{
    public string LocationDisplay => string.IsNullOrWhiteSpace(Location)
        ? "Location not set"
        : Location;
}

internal sealed record RemoteFleetSnapshot(
    RemoteFleetConnectionState ConnectionState,
    string ControlPlaneDisplayName,
    string StatusMessage,
    IReadOnlyList<RemoteDevice> Devices,
    string? ActiveExitNodeId,
    DateTimeOffset RefreshedAt,
    bool HasUnmanagedActiveExitNode = false)
{
    public static RemoteFleetSnapshot NotConfigured { get; } = new(
        RemoteFleetConnectionState.NotConfigured,
        "Self-hosted remotes",
        "Not configured",
        Array.Empty<RemoteDevice>(),
        null,
        DateTimeOffset.MinValue);
}

internal sealed record RemoteMenuDevice(
    RemoteDevice Device,
    string DisplayText,
    bool IsActionable,
    bool IsActiveExitNode);

internal sealed record RemoteMenuModel(
    string ConnectionText,
    string InternetRouteText,
    bool CanRefresh,
    IReadOnlyList<RemoteMenuDevice> Devices);

internal static class RemoteMenuModelBuilder
{
    public static RemoteMenuModel Build(RemoteFleetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var onlineCount = snapshot.Devices.Count(device => device.IsOnline);
        var connectionText = snapshot.ConnectionState switch
        {
            RemoteFleetConnectionState.NotConfigured => "Private network: setup needed",
            RemoteFleetConnectionState.Connecting => "Private network: connecting…",
            RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded =>
                $"Private network: connected • {onlineCount} online",
            _ => "Private network: disconnected"
        };

        var devices = snapshot.Devices
            .OrderByDescending(device => device.IsOnline)
            .ThenBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(device => new RemoteMenuDevice(
                device,
                BuildDisplayText(device),
                IsActionable(snapshot, device),
                string.Equals(snapshot.ActiveExitNodeId, device.Id, StringComparison.Ordinal)))
            .ToArray();

        var activeExitNode = devices.FirstOrDefault(device => device.IsActiveExitNode)?.Device;
        var internetRouteText = activeExitNode is not null
            ? $"Internet VPN: ON through {activeExitNode.DeviceName}"
            : snapshot.HasUnmanagedActiveExitNode || snapshot.ActiveExitNodeId is not null
                ? "Internet VPN: ON through another exit"
                : "Internet VPN: OFF";

        return new RemoteMenuModel(
            connectionText,
            internetRouteText,
            snapshot.ConnectionState is RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded,
            devices);
    }

    private static bool IsActionable(RemoteFleetSnapshot snapshot, RemoteDevice device)
    {
        return snapshot.ConnectionState is RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded
            && device.IsOnline
            && device.IsVerified;
    }

    private static string BuildDisplayText(RemoteDevice device)
    {
        var status = device.IsOnline ? "Online" : "Offline";
        var verification = device.IsVerified ? string.Empty : " · Unverified";
        var owner = string.IsNullOrWhiteSpace(device.OwnerDisplayName)
            || string.Equals(device.OwnerDisplayName, "Tagged Devices", StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.OwnerDisplayName, "Headscale user", StringComparison.OrdinalIgnoreCase)
                ? "Not set"
                : device.OwnerDisplayName;
        return $"{device.DeviceName} · User: {owner} · Location: {device.LocationDisplay} · {status}{verification}";
    }
}

internal static class StayActiveRemoteDefaults
{
    // The LAN deployment publishes these names through its local DNS/hosts
    // bootstrap and its internal CA. They are defaults, not a lock-in: an
    // owner can move the same self-hosted stack to another HTTPS origin later.
    public const string ControlPlaneUrl = "https://headscale.stayactive.test";
    public const string RemoteHubUrl = "https://remotehub.stayactive.test";
    public const string AdminConsoleUrl = "https://remotehub.stayactive.test/admin";
    public const string MeshCentralUrl = "https://meshcentral.stayactive.test";
    public const string OidcIssuerUrl = "https://id.stayactive.test/realms/stayactive";
    public const string FleetOidcClientId = "stayactive-remotes-tray";
    public const string EnrollmentBrokerUrl = "https://remotehub.stayactive.test";
    public const string EnrollmentOidcClientId = "stayactive-remotes-enrollment";
}

internal sealed record RemoteClientPreferences(
    string ControlPlaneUrl,
    string RemoteHubUrl,
    string AdminConsoleUrl,
    string MeshCentralUrl,
    string DeviceDisplayName,
    string Location,
    string RemoteHubOidcIssuerUrl = "",
    string RemoteHubOidcClientId = "",
    string RemoteEnrollmentUrl = "",
    string RemoteEnrollmentOidcClientId = "")
{
    public bool IsConfigured => IsSelfHostedEndpoint(RemoteHubUrl);

    public bool HasControlPlane => IsSelfHostedControlPlane(ControlPlaneUrl);

    public bool HasMeshCentral => IsSelfHostedEndpoint(MeshCentralUrl);

    public bool HasEnrollmentBroker => IsSelfHostedEndpoint(RemoteEnrollmentUrl);

    internal static bool IsSelfHostedEndpoint(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 2048
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && !IsHostedTailscaleHost(uri.Host);
    }

    internal static bool IsSelfHostedControlPlane(string? value)
    {
        return IsSelfHostedEndpoint(value);
    }

    internal static bool IsHostedTailscaleHost(string? host)
    {
        var normalizedHost = host?.Trim().TrimEnd('.');
        return !string.IsNullOrEmpty(normalizedHost)
            && (string.Equals(normalizedHost, "tailscale.com", StringComparison.OrdinalIgnoreCase)
                || normalizedHost.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase));
    }
}
