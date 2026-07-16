using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class MeshCentralRemoteActionServiceTests
{
    [Fact]
    public void Open_ViewScreen_UsesOnlyTheConfiguredHttpsMeshCentralDeviceView()
    {
        var launcher = new CapturingLauncher();
        var service = CreateService("https://mesh.example.test", launcher);

        var result = service.Open(
            CreateDevice(RemoteCapability.ScreenView, "node//workstation?one"),
            RemoteWebAction.ViewScreen);

        Assert.True(result.Succeeded);
        var uri = Assert.IsType<Uri>(launcher.OpenedUri);
        Assert.Equal("mesh.example.test", uri.Host);
        Assert.Contains("node=node%2F%2Fworkstation%3Fone", uri.Query);
        Assert.Contains("viewmode=11", uri.Query);
    }

    [Fact]
    public void Open_FileRequest_UsesTheMeshCentralFilesView()
    {
        var launcher = new CapturingLauncher();
        var service = CreateService("https://mesh.example.test/remote", launcher);

        var result = service.Open(
            CreateDevice(RemoteCapability.RequestFile, "node//workstation"),
            RemoteWebAction.RequestFile);

        Assert.True(result.Succeeded);
        var uri = Assert.IsType<Uri>(launcher.OpenedUri);
        Assert.Equal("/remote/", uri.AbsolutePath);
        Assert.Contains("viewmode=13", uri.Query);
    }

    [Fact]
    public void Open_RejectsHttpAndDoesNotStartAnExternalProcess()
    {
        var launcher = new CapturingLauncher();
        var service = CreateService("http://mesh.example.test", launcher);

        var result = service.Open(
            CreateDevice(RemoteCapability.ScreenView, "node//workstation"),
            RemoteWebAction.ViewScreen);

        Assert.False(result.Succeeded);
        Assert.Null(launcher.OpenedUri);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void GetAvailability_RequiresTheMatchingConsentCapableDevice()
    {
        var launcher = new CapturingLauncher();
        var service = CreateService("https://mesh.example.test", launcher);
        var device = CreateDevice(RemoteCapability.SendFile, "node//workstation") with
        {
            IsVerified = false
        };

        var availability = service.GetAvailability(device, RemoteWebAction.SendFile);

        Assert.False(availability.IsAvailable);
        Assert.Contains("Verify", availability.Reason);
        Assert.Null(launcher.OpenedUri);
    }

    private static MeshCentralRemoteActionService CreateService(string meshCentralUrl, IExternalUriLauncher launcher)
    {
        return new MeshCentralRemoteActionService(
            () => new RemoteClientPreferences(
                "https://headscale.example.test",
                "https://remotes.example.test",
                "https://remotes.example.test/admin",
                meshCentralUrl,
                "This computer",
                "Test lab"),
            launcher);
    }

    private static RemoteDevice CreateDevice(RemoteCapability capability, string meshCentralNodeId)
    {
        return new RemoteDevice(
            "device-id",
            "Office-PC",
            "Alice",
            "Austin office",
            true,
            true,
            DateTimeOffset.UtcNow,
            capability,
            null,
            meshCentralNodeId);
    }

    private sealed class CapturingLauncher : IExternalUriLauncher
    {
        public Uri? OpenedUri { get; private set; }

        public void Open(Uri uri)
        {
            OpenedUri = uri;
        }
    }
}
