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
        ? "Location not shared"
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

        var connectionText = snapshot.ConnectionState switch
        {
            RemoteFleetConnectionState.NotConfigured => "Self-hosted remotes: not configured",
            RemoteFleetConnectionState.Connecting => $"{snapshot.ControlPlaneDisplayName}: connecting…",
            RemoteFleetConnectionState.Connected => $"{snapshot.ControlPlaneDisplayName}: connected · {snapshot.Devices.Count(device => device.IsOnline)} online",
            RemoteFleetConnectionState.Degraded => $"{snapshot.ControlPlaneDisplayName}: degraded · {snapshot.StatusMessage}",
            _ => $"{snapshot.ControlPlaneDisplayName}: disconnected · {snapshot.StatusMessage}"
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
            ? $"Internet route: {activeExitNode.DeviceName}"
            : snapshot.HasUnmanagedActiveExitNode
                ? "Internet route: an unmanaged exit node"
                : "Internet route: Direct";

        return new RemoteMenuModel(
            connectionText,
            internetRouteText,
            snapshot.ConnectionState is RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded,
            devices);
    }

    private static bool IsActionable(RemoteFleetSnapshot snapshot, RemoteDevice device)
    {
        return snapshot.ConnectionState == RemoteFleetConnectionState.Connected
            && device.IsOnline
            && device.IsVerified;
    }

    private static string BuildDisplayText(RemoteDevice device)
    {
        var status = device.IsOnline ? "Online" : "Offline";
        var verification = device.IsVerified ? string.Empty : " · Unverified";
        return $"{device.DeviceName} · {device.OwnerDisplayName} · {device.LocationDisplay} · {status}{verification}";
    }
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
    public bool IsConfigured => IsHttpsEndpoint(RemoteHubUrl);

    public bool HasControlPlane => IsSelfHostedControlPlane(ControlPlaneUrl);

    public bool HasMeshCentral => IsHttpsEndpoint(MeshCentralUrl);

    public bool HasEnrollmentBroker => IsHttpsEndpoint(RemoteEnrollmentUrl);

    private static bool IsHttpsEndpoint(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    internal static bool IsSelfHostedControlPlane(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.Equals(uri.Host, "tailscale.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase);
    }
}
