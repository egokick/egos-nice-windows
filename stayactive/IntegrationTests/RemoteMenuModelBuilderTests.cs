using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class RemoteMenuModelBuilderTests
{
    [Fact]
    public void Build_WhenNotConfigured_ShowsSafeNonActionableState()
    {
        var model = RemoteMenuModelBuilder.Build(RemoteFleetSnapshot.NotConfigured);

        Assert.Equal("Private network: setup needed", model.ConnectionText);
        Assert.Equal("Internet VPN: OFF", model.InternetRouteText);
        Assert.False(model.CanRefresh);
        Assert.Empty(model.Devices);
    }

    [Fact]
    public void Build_SortsOnlineDevicesFirst_AndOnlyEnablesVerifiedOnlineDevices()
    {
        var snapshot = new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "HQ control plane",
            "Ready",
            new[]
            {
                CreateDevice("offline", "Zulu", online: false, verified: true),
                CreateDevice("unverified", "Alpha", online: true, verified: false),
                CreateDevice("online", "Bravo", online: true, verified: true)
            },
            null,
            DateTimeOffset.UtcNow);

        var model = RemoteMenuModelBuilder.Build(snapshot);

        Assert.Equal(new[] { "Alpha", "Bravo", "Zulu" }, model.Devices.Select(device => device.Device.DeviceName));
        Assert.False(model.Devices[0].IsActionable);
        Assert.True(model.Devices[1].IsActionable);
        Assert.False(model.Devices[2].IsActionable);
        Assert.True(model.CanRefresh);
        Assert.Equal("Private network: connected • 2 online", model.ConnectionText);
    }

    [Fact]
    public void Build_ShowsTheSelectedExitNode()
    {
        var exitNode = CreateDevice("exit", "Austin-PC", online: true, verified: true);
        var snapshot = new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "HQ control plane",
            "Ready",
            new[] { exitNode },
            exitNode.Id,
            DateTimeOffset.UtcNow);

        var model = RemoteMenuModelBuilder.Build(snapshot);

        Assert.Equal("Internet VPN: ON through Austin-PC", model.InternetRouteText);
        Assert.True(Assert.Single(model.Devices).IsActiveExitNode);
    }

    [Fact]
    public void Build_WhenRemoteMetadataIsDegraded_KeepsVerifiedVpnPeerActionable()
    {
        var exitNode = CreateDevice("exit", "Austin-PC", online: true, verified: true);
        var snapshot = new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Degraded,
            "HQ control plane",
            "Remote metadata unavailable",
            new[] { exitNode },
            null,
            DateTimeOffset.UtcNow);

        var model = RemoteMenuModelBuilder.Build(snapshot);

        Assert.True(model.CanRefresh);
        Assert.Equal("Private network: connected • 1 online", model.ConnectionText);
        Assert.DoesNotContain("degraded", model.ConnectionText, StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(model.Devices).IsActionable);
    }

    [Fact]
    public void Build_WhenAnUnmanagedExitNodeIsActive_DoesNotClaimDirectRouting()
    {
        var snapshot = new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "HQ control plane",
            "An unmanaged exit node is active.",
            Array.Empty<RemoteDevice>(),
            null,
            DateTimeOffset.UtcNow,
            HasUnmanagedActiveExitNode: true);

        var model = RemoteMenuModelBuilder.Build(snapshot);

        Assert.Equal("Internet VPN: ON through another exit", model.InternetRouteText);
    }

    [Fact]
    public void Build_WhenActiveExitIdIsUnknown_DoesNotClaimVpnIsOff()
    {
        var snapshot = new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "HQ control plane",
            "Ready",
            Array.Empty<RemoteDevice>(),
            "node:not-in-visible-fleet",
            DateTimeOffset.UtcNow);

        var model = RemoteMenuModelBuilder.Build(snapshot);

        Assert.Equal("Internet VPN: ON through another exit", model.InternetRouteText);
    }

    private static RemoteDevice CreateDevice(string id, string name, bool online, bool verified)
    {
        return new RemoteDevice(
            id,
            name,
            "Alice",
            "Austin office",
            online,
            verified,
            DateTimeOffset.UtcNow,
            RemoteCapability.ExitNode | RemoteCapability.ScreenView);
    }
}
