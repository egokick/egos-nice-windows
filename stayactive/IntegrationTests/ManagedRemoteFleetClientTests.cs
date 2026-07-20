using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class ManagedRemoteFleetClientTests
{
    [Fact]
    public async Task RefreshAsync_MergesOnlyVerifiedRemoteHubMetadataIntoHeadscalePeers()
    {
        var headscale = new FakeFleetClient(HeadscaleSnapshot());
        var hub = new FakeRemoteHubClient(new RemoteHubInventorySnapshot(
            RemoteHubInventoryState.Available,
            "Current",
            new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal)
            {
                ["node:office"] = new(
                    "node:office",
                    "Alice at HQ",
                    "Austin office",
                    "node//mesh-office",
                    true,
                    RemoteCapability.ExitNode | RemoteCapability.ScreenView | RemoteCapability.SendFile | RemoteCapability.RequestFile,
                    3,
                    DateTimeOffset.UtcNow)
            },
            DateTimeOffset.UtcNow));
        using var client = new ManagedRemoteFleetClient(headscale, hub);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(RemoteFleetConnectionState.Connected, snapshot.ConnectionState);
        Assert.True(device.IsVerified);
        Assert.Equal("Alice at HQ", device.OwnerDisplayName);
        Assert.Equal("Austin office", device.Location);
        Assert.Equal("node//mesh-office", device.MeshCentralNodeId);
        Assert.Equal(
            RemoteCapability.ExitNode | RemoteCapability.ScreenView | RemoteCapability.SendFile | RemoteCapability.RequestFile,
            device.Capabilities);
    }

    [Fact]
    public async Task RefreshAsync_LeavesAnUnmappedPeerNonActionable()
    {
        var headscale = new FakeFleetClient(HeadscaleSnapshot());
        var hub = new FakeRemoteHubClient(new RemoteHubInventorySnapshot(
            RemoteHubInventoryState.Available,
            "Current",
            new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow));
        using var client = new ManagedRemoteFleetClient(headscale, hub);

        await client.RefreshAsync(CancellationToken.None);

        var device = Assert.Single(client.GetCachedSnapshot().Devices);
        Assert.False(device.IsVerified);
        Assert.Equal(RemoteCapability.None, device.Capabilities);
        Assert.Null(device.MeshCentralNodeId);
    }

    [Fact]
    public async Task RefreshAsync_WhenRemoteHubIsUnavailable_PreservesOnlyVpnTrustAndExitControl()
    {
        var headscale = new FakeFleetClient(HeadscaleSnapshot(activeExitNodeId: "node:office"));
        var hub = new FakeRemoteHubClient(new RemoteHubInventorySnapshot(
            RemoteHubInventoryState.Unavailable,
            "RemoteHub unavailable",
            new Dictionary<string, RemoteHubDeviceMetadata>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow));
        using var client = new ManagedRemoteFleetClient(headscale, hub);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(RemoteFleetConnectionState.Degraded, snapshot.ConnectionState);
        Assert.Equal("node:office", snapshot.ActiveExitNodeId);
        Assert.True(device.IsVerified);
        Assert.Equal(RemoteCapability.ExitNode, device.Capabilities);
        Assert.Null(device.Location);
        Assert.Null(device.MeshCentralNodeId);
        Assert.Contains("screen and file controls are disabled", snapshot.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_WhenHeadscaleIsDisconnected_DoesNotQueryRemoteHub()
    {
        var headscale = new FakeFleetClient(new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Disconnected,
            "Headscale example",
            "Disconnected",
            Array.Empty<RemoteDevice>(),
            null,
            DateTimeOffset.UtcNow));
        var hub = new FakeRemoteHubClient(RemoteHubInventorySnapshot.NotConfigured);
        using var client = new ManagedRemoteFleetClient(headscale, hub);

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, hub.RefreshCount);
        Assert.Equal(RemoteFleetConnectionState.Disconnected, client.GetCachedSnapshot().ConnectionState);
    }

    private static RemoteFleetSnapshot HeadscaleSnapshot(string? activeExitNodeId = null)
    {
        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "Headscale example",
            "Connected",
            new[]
            {
                new RemoteDevice(
                    "node:office",
                    "Office-PC",
                    "Headscale user",
                    null,
                    true,
                    true,
                    DateTimeOffset.UtcNow,
                    RemoteCapability.ExitNode,
                    "100.64.0.10")
            },
            activeExitNodeId,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeFleetClient(RemoteFleetSnapshot snapshot) : IRemoteFleetClient
    {
        public RemoteFleetSnapshot GetCachedSnapshot() => snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeRemoteHubClient(RemoteHubInventorySnapshot snapshot) : IRemoteHubInventoryClient
    {
        public int RefreshCount { get; private set; }

        public RemoteHubInventorySnapshot GetCachedSnapshot() => snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
