using StayActive.Remotes;

namespace StayActive.RemoteUiPreview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var startDirect = args.Any(argument => string.Equals(argument, "--off", StringComparison.OrdinalIgnoreCase));
        var degraded = args.Any(argument => string.Equals(argument, "--degraded", StringComparison.OrdinalIgnoreCase));
        using var sourceFleetClient = new PreviewRemoteFleetClient(startDirect, degraded);
        var displayOverrides = new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.Ordinal);
        using var fleetClient = new LocalDisplayRemoteFleetClient(sourceFleetClient, () => displayOverrides);
        var preferences = new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotes.example.test",
            "https://remotes.example.test/admin",
            "https://mesh.example.test",
            "HQ-Laptop",
            "Chicago office");
        using var form = new RemotesDashboardForm(
            fleetClient,
            new MeshCentralRemoteActionService(() => preferences, new PreviewUriLauncher()),
            new PreviewExitNodeController(sourceFleetClient),
            new RemoteAdminConsoleLauncher(() => preferences, new PreviewUriLauncher()),
            new PreviewRemoteHubTokenProvider(),
            () => preferences,
            _ => { },
            saveDeviceDisplayOverride: (deviceId, displayOverride) =>
            {
                if (displayOverride.IsEmpty)
                {
                    displayOverrides.Remove(deviceId);
                }
                else
                {
                    displayOverrides[deviceId] = displayOverride;
                }
            });
        if (args.Any(argument => string.Equals(argument, "--minimum", StringComparison.OrdinalIgnoreCase)))
        {
            form.Size = form.MinimumSize;
        }
        Application.Run(form);
    }
}

internal sealed class PreviewRemoteFleetClient : IRemoteFleetClient
{
    private RemoteFleetSnapshot _snapshot;

    public PreviewRemoteFleetClient(bool startDirect, bool degraded)
    {
        _snapshot = new RemoteFleetSnapshot(
        degraded ? RemoteFleetConnectionState.Degraded : RemoteFleetConnectionState.Connected,
        "Private fleet",
        degraded ? "RemoteHub is unavailable. VPN presence is still current." : "Ready",
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
                degraded
                    ? RemoteCapability.ExitNode
                    : RemoteCapability.ExitNode
                        | RemoteCapability.ScreenView
                        | RemoteCapability.SendFile
                        | RemoteCapability.RequestFile
                        | RemoteCapability.LocalAuthenticatorApproval,
                "100.64.0.10",
                degraded ? null : "node//austin-pc"),
            new RemoteDevice(
                "london-laptop",
                "London-Laptop",
                "Bob",
                "London",
                false,
                true,
                DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                RemoteCapability.ScreenView | RemoteCapability.SendFile,
                "100.64.0.20",
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
        startDirect ? null : "austin-pc",
        DateTimeOffset.UtcNow);
    }

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

    public void SetActiveExit(string? deviceId)
    {
        _snapshot = _snapshot with
        {
            ActiveExitNodeId = deviceId,
            RefreshedAt = DateTimeOffset.UtcNow
        };
    }
}

internal sealed class PreviewUriLauncher : IExternalUriLauncher
{
    public void Open(Uri uri)
    {
    }
}

internal sealed class PreviewExitNodeController(PreviewRemoteFleetClient fleetClient) : IRemoteExitNodeController
{
    public RemoteActionAvailability GetAvailability(RemoteDevice device)
    {
        if (!device.IsVerified || !device.IsOnline)
        {
            return new RemoteActionAvailability(false, "The selected computer must be verified and online.");
        }

        if (!device.Capabilities.HasFlag(RemoteCapability.ExitNode))
        {
            return new RemoteActionAvailability(false, "This computer is not an approved exit node.");
        }

        return RemoteActionAvailability.Available;
    }

    public RemoteActionAvailability GetClearAvailability() => RemoteActionAvailability.Available;

    public Task<RemoteActionResult> UseExitNodeAsync(
        RemoteDevice device,
        bool allowLocalNetworkAccess,
        CancellationToken cancellationToken)
    {
        fleetClient.SetActiveExit(device.Id);
        return Task.FromResult(RemoteActionResult.Success("Preview only."));
    }

    public Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
    {
        fleetClient.SetActiveExit(null);
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
