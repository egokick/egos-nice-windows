using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class LocalRemoteDisplayMetadataTests
{
    [Fact]
    public void DisplayOverride_NormalizesLabelsAndUsesBlankAsFallback()
    {
        var displayOverride = new RemoteDeviceDisplayOverride
        {
            OwnerOrUserLabel = "  Alice\r\nOwner  ",
            Location = new string('x', RemoteDeviceDisplayOverride.MaxLabelLength + 10)
        };

        Assert.Equal("Alice  Owner", displayOverride.OwnerOrUserLabel);
        Assert.Equal(RemoteDeviceDisplayOverride.MaxLabelLength, displayOverride.Location!.Length);
        Assert.DoesNotContain(displayOverride.OwnerOrUserLabel, char.IsControl);
        Assert.DoesNotContain(displayOverride.Location, char.IsControl);

        var blank = new RemoteDeviceDisplayOverride(" \t\r\n ", null);
        Assert.Null(blank.OwnerOrUserLabel);
        Assert.Null(blank.Location);
        Assert.True(blank.IsEmpty);
    }

    [Fact]
    public void GetCachedSnapshot_OverlaysOnlyOwnerAndLocationForAnExactDeviceId()
    {
        var original = CreateDevice("node:office");
        var snapshot = CreateSnapshot(original);
        using var inner = new FakeFleetClient(snapshot);
        using var client = new LocalDisplayRemoteFleetClient(
            inner,
            () => new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.Ordinal)
            {
                [original.Id] = new("Local owner", "Local location")
            });

        var decoratedSnapshot = client.GetCachedSnapshot();
        var decorated = Assert.Single(decoratedSnapshot.Devices);

        Assert.Equal("Local owner", decorated.OwnerDisplayName);
        Assert.Equal("Local location", decorated.Location);
        Assert.Equal(original.Id, decorated.Id);
        Assert.Equal(original.DeviceName, decorated.DeviceName);
        Assert.Equal(original.IsOnline, decorated.IsOnline);
        Assert.Equal(original.IsVerified, decorated.IsVerified);
        Assert.Equal(original.LastSeenAt, decorated.LastSeenAt);
        Assert.Equal(original.Capabilities, decorated.Capabilities);
        Assert.Equal(original.TailnetIp, decorated.TailnetIp);
        Assert.Equal(original.MeshCentralNodeId, decorated.MeshCentralNodeId);
        Assert.Equal(snapshot.ConnectionState, decoratedSnapshot.ConnectionState);
        Assert.Equal(snapshot.ActiveExitNodeId, decoratedSnapshot.ActiveExitNodeId);

        // Decoration must not mutate the authoritative inner snapshot.
        Assert.Same(original, Assert.Single(inner.GetCachedSnapshot().Devices));
    }

    [Fact]
    public void GetCachedSnapshot_BlankFieldsFallBackToAuthoritativeMetadata()
    {
        var original = CreateDevice("node:office");
        using var client = new LocalDisplayRemoteFleetClient(
            new FakeFleetClient(CreateSnapshot(original)),
            () => new Dictionary<string, RemoteDeviceDisplayOverride>
            {
                [original.Id] = new("Local owner", "  ")
            });

        var decorated = Assert.Single(client.GetCachedSnapshot().Devices);

        Assert.Equal("Local owner", decorated.OwnerDisplayName);
        Assert.Equal(original.Location, decorated.Location);
    }

    [Fact]
    public void GetCachedSnapshot_DoesNotUseTheCallersCaseInsensitiveComparer()
    {
        var original = CreateDevice("node:office");
        var caseInsensitiveOverrides = new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["NODE:OFFICE"] = new("Wrong owner", "Wrong location")
        };
        using var client = new LocalDisplayRemoteFleetClient(
            new FakeFleetClient(CreateSnapshot(original)),
            () => caseInsensitiveOverrides);

        var decorated = Assert.Single(client.GetCachedSnapshot().Devices);

        Assert.Equal(original.OwnerDisplayName, decorated.OwnerDisplayName);
        Assert.Equal(original.Location, decorated.Location);
    }

    [Fact]
    public async Task RefreshAndDispose_DelegateToTheAuthoritativeClient()
    {
        var cancellationSource = new CancellationTokenSource();
        var inner = new FakeFleetClient(CreateSnapshot(CreateDevice("node:office")));
        var client = new LocalDisplayRemoteFleetClient(
            inner,
            () => new Dictionary<string, RemoteDeviceDisplayOverride>());

        await client.RefreshAsync(cancellationSource.Token);
        client.Dispose();

        Assert.Equal(1, inner.RefreshCount);
        Assert.Equal(cancellationSource.Token, inner.LastCancellationToken);
        Assert.Equal(1, inner.DisposeCount);
    }

    private static RemoteDevice CreateDevice(string id) => new(
        id,
        "Office-PC",
        "Authoritative owner",
        "Authoritative location",
        true,
        true,
        new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        RemoteCapability.ExitNode | RemoteCapability.ScreenView,
        "100.64.0.10",
        "node//mesh-office");

    private static RemoteFleetSnapshot CreateSnapshot(RemoteDevice device) => new(
        RemoteFleetConnectionState.Connected,
        "Self-hosted Headscale",
        "Connected",
        new[] { device },
        device.Id,
        new DateTimeOffset(2026, 7, 20, 12, 1, 0, TimeSpan.Zero));

    private sealed class FakeFleetClient(RemoteFleetSnapshot snapshot) : IRemoteFleetClient
    {
        public int RefreshCount { get; private set; }

        public int DisposeCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public RemoteFleetSnapshot GetCachedSnapshot() => snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }
}
