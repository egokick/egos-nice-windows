namespace StayActive.Remotes;

/// <summary>
/// Combines locally observed Headscale peer state with the signed-in operator's
/// RemoteHub metadata. Headscale remains the source of network presence and
/// exit-node availability; RemoteHub supplies only opt-in identity/location,
/// MeshCentral mapping, and centrally approved capabilities.
/// </summary>
internal sealed class ManagedRemoteFleetClient : IRemoteFleetClient
{
    private readonly IRemoteFleetClient _headscaleClient;
    private readonly IRemoteHubInventoryClient _remoteHubClient;
    private readonly object _snapshotLock = new();
    private RemoteFleetSnapshot _snapshot = RemoteFleetSnapshot.NotConfigured;
    private bool _disposed;

    public ManagedRemoteFleetClient(
        IRemoteFleetClient headscaleClient,
        IRemoteHubInventoryClient remoteHubClient)
    {
        _headscaleClient = headscaleClient ?? throw new ArgumentNullException(nameof(headscaleClient));
        _remoteHubClient = remoteHubClient ?? throw new ArgumentNullException(nameof(remoteHubClient));
    }

    public RemoteFleetSnapshot GetCachedSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _headscaleClient.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var headscale = _headscaleClient.GetCachedSnapshot();
        if (headscale.ConnectionState is not RemoteFleetConnectionState.Connected and not RemoteFleetConnectionState.Degraded)
        {
            SetSnapshot(headscale);
            return;
        }

        await _remoteHubClient.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var remoteHub = _remoteHubClient.GetCachedSnapshot();
        SetSnapshot(remoteHub.State == RemoteHubInventoryState.Available
            ? MergeVerifiedInventory(headscale, remoteHub)
            : MergeWithoutInventory(headscale, remoteHub));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _headscaleClient.Dispose();
        _remoteHubClient.Dispose();
    }

    private static RemoteFleetSnapshot MergeVerifiedInventory(
        RemoteFleetSnapshot headscale,
        RemoteHubInventorySnapshot remoteHub)
    {
        var devices = headscale.Devices
            .Select(device => remoteHub.Devices.TryGetValue(device.Id, out var metadata)
                ? MergeDevice(device, metadata)
                : UnverifiedDevice(device))
            .ToArray();

        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            headscale.ControlPlaneDisplayName,
            "Headscale network and RemoteHub device metadata are connected.",
            devices,
            headscale.ActiveExitNodeId,
            DateTimeOffset.UtcNow,
            headscale.HasUnmanagedActiveExitNode);
    }

    private static RemoteFleetSnapshot MergeWithoutInventory(
        RemoteFleetSnapshot headscale,
        RemoteHubInventorySnapshot remoteHub)
    {
        // A disconnected RemoteHub must never leave an old mapping or central
        // authorization actionable. An active route can still be cleared from
        // the root menu, but no new screen, file, or exit-node session starts.
        var devices = headscale.Devices
            .Select(device => device with
            {
                Location = null,
                MeshCentralNodeId = null,
                IsVerified = false,
                Capabilities = RemoteCapability.None
            })
            .ToArray();

        var state = headscale.ConnectionState == RemoteFleetConnectionState.Connected
            ? RemoteFleetConnectionState.Degraded
            : headscale.ConnectionState;
        return new RemoteFleetSnapshot(
            state,
            headscale.ControlPlaneDisplayName,
            remoteHub.StatusMessage,
            devices,
            headscale.ActiveExitNodeId,
            DateTimeOffset.UtcNow,
            headscale.HasUnmanagedActiveExitNode);
    }

    private static RemoteDevice MergeDevice(RemoteDevice headscaleDevice, RemoteHubDeviceMetadata metadata)
    {
        var isVerified = headscaleDevice.IsVerified && metadata.IsVerified;
        var capabilities = isVerified
            ? IntersectCapabilities(headscaleDevice.Capabilities, metadata.AllowedCapabilities)
            : RemoteCapability.None;

        return headscaleDevice with
        {
            OwnerDisplayName = string.IsNullOrWhiteSpace(metadata.OwnerDisplayName)
                ? headscaleDevice.OwnerDisplayName
                : metadata.OwnerDisplayName,
            Location = metadata.CoarseLocation,
            IsVerified = isVerified,
            Capabilities = capabilities,
            MeshCentralNodeId = metadata.MeshCentralNodeId
        };
    }

    private static RemoteDevice UnverifiedDevice(RemoteDevice headscaleDevice)
    {
        return headscaleDevice with
        {
            Location = null,
            IsVerified = false,
            Capabilities = RemoteCapability.None,
            MeshCentralNodeId = null
        };
    }

    private static RemoteCapability IntersectCapabilities(
        RemoteCapability headscaleCapabilities,
        RemoteCapability remoteHubCapabilities)
    {
        var capabilities = RemoteCapability.None;
        if (headscaleCapabilities.HasFlag(RemoteCapability.ExitNode)
            && remoteHubCapabilities.HasFlag(RemoteCapability.ExitNode))
        {
            capabilities |= RemoteCapability.ExitNode;
        }

        capabilities |= remoteHubCapabilities &
            (RemoteCapability.ScreenView | RemoteCapability.SendFile | RemoteCapability.RequestFile);
        return capabilities;
    }

    private void SetSnapshot(RemoteFleetSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ManagedRemoteFleetClient));
        }
    }
}
