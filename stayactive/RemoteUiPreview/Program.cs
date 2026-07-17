using StayActive.Remotes;

namespace StayActive.RemoteUiPreview;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var fleetClient = new PreviewRemoteFleetClient();
        var preferences = new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotes.example.test",
            "https://remotes.example.test/admin",
            "https://mesh.example.test",
            "HQ-Laptop",
            "Chicago office");
        Application.Run(new RemotesDashboardForm(
            fleetClient,
            new MeshCentralRemoteActionService(() => preferences, new PreviewUriLauncher()),
            new PreviewExitNodeController(),
            new RemoteAdminConsoleLauncher(() => preferences, new PreviewUriLauncher()),
            new PreviewRemoteHubTokenProvider(),
            () => preferences,
            _ => { }));
    }
}

internal sealed class PreviewRemoteFleetClient : IRemoteFleetClient
{
    private readonly RemoteFleetSnapshot _snapshot = new(
        RemoteFleetConnectionState.Connected,
        "Private fleet",
        "Ready",
        new[]
        {
            new RemoteDevice(
                "austin-pc",
                "Austin-PC",
                "Alice",
                "Austin office",
                true,
                true,
                DateTimeOffset.UtcNow,
                RemoteCapability.ExitNode
                    | RemoteCapability.ScreenView
                    | RemoteCapability.SendFile
                    | RemoteCapability.RequestFile
                    | RemoteCapability.LocalAuthenticatorApproval,
                null,
                "node//austin-pc"),
            new RemoteDevice(
                "london-laptop",
                "London-Laptop",
                "Bob",
                "London",
                false,
                true,
                DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                RemoteCapability.ScreenView | RemoteCapability.SendFile,
                null,
                "node//london-laptop"),
            new RemoteDevice(
                "unverified-host",
                "New-Desktop",
                "Pending owner",
                null,
                true,
                false,
                DateTimeOffset.UtcNow,
                RemoteCapability.None)
        },
        "austin-pc",
        DateTimeOffset.UtcNow);

    public RemoteFleetSnapshot GetCachedSnapshot()
    {
        return _snapshot;
    }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

internal sealed class PreviewUriLauncher : IExternalUriLauncher
{
    public void Open(Uri uri)
    {
    }
}

internal sealed class PreviewExitNodeController : IRemoteExitNodeController
{
    public RemoteActionAvailability GetAvailability(RemoteDevice device) => RemoteActionAvailability.Available;

    public RemoteActionAvailability GetClearAvailability() => RemoteActionAvailability.Available;

    public Task<RemoteActionResult> UseExitNodeAsync(
        RemoteDevice device,
        bool allowLocalNetworkAccess,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(RemoteActionResult.Success("Preview only."));
    }

    public Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(RemoteActionResult.Success("Preview only."));
    }
}

internal sealed class PreviewRemoteHubTokenProvider : IRemoteHubAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult("preview-token");
    }

    public Task SignInAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
