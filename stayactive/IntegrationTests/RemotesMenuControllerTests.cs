using System.Runtime.ExceptionServices;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

[Collection("WinForms")]
public sealed class RemotesMenuControllerTests
{
    [Fact]
    public void DirectVpnState_UsesPlainLanguageAndOneClickConnectAction()
    {
        RunSta(() =>
        {
            var fleet = new FakeFleetClient(CreateSnapshot(activeExitNodeId: null));
            var exitController = new FakeExitNodeController();
            using var controller = CreateController(fleet, exitController);

            Assert.Equal("Private network: connected • 1 online", controller.MenuItem.DropDownItems[0].Text);
            Assert.Equal("Internet VPN: OFF", controller.MenuItem.DropDownItems[1].Text);

            var deviceItem = Assert.Single(
                controller.MenuItem.DropDownItems
                    .OfType<ToolStripMenuItem>(),
                item => item.Text?.StartsWith("Travel-Laptop", StringComparison.Ordinal) == true);
            Assert.Contains("Alice", deviceItem.Text, StringComparison.Ordinal);
            Assert.Contains("Home office", deviceItem.Text, StringComparison.Ordinal);
            var vpnItem = Assert.Single(
                deviceItem.DropDownItems
                    .OfType<ToolStripMenuItem>(),
                item => item.Text == "Use this computer for Internet VPN");
            Assert.False(vpnItem.Checked);

            vpnItem.PerformClick();
            Application.DoEvents();

            Assert.Equal(1, exitController.UseCalls);
            Assert.Equal("node:travel", exitController.LastUsedDevice?.Id);
            Assert.False(exitController.LastAllowLocalNetworkAccess);
        });
    }

    [Fact]
    public void ActiveVpnState_IsObviousAndOneClickTurnOffIsChecked()
    {
        RunSta(() =>
        {
            var fleet = new FakeFleetClient(CreateSnapshot(activeExitNodeId: "node:travel"));
            var exitController = new FakeExitNodeController();
            using var controller = CreateController(fleet, exitController);

            Assert.Equal("Internet VPN: ON through Travel-Laptop", controller.MenuItem.DropDownItems[1].Text);
            var turnOff = Assert.Single(
                controller.MenuItem.DropDownItems
                    .OfType<ToolStripMenuItem>(),
                item => item.Text == "VPN ON - turn off");
            Assert.True(turnOff.Checked);

            turnOff.PerformClick();
            Application.DoEvents();

            Assert.Equal(1, exitController.ClearCalls);
            Assert.Equal(0, exitController.UseCalls);
        });
    }

    private static RemotesMenuController CreateController(
        IRemoteFleetClient fleetClient,
        IRemoteExitNodeController exitNodeController)
    {
        return new RemotesMenuController(
            fleetClient,
            new FakeActionService(),
            exitNodeController,
            new FakeAdminConsoleLauncher(),
            new FakeTokenProvider(),
            new WindowsFormsSynchronizationContext(),
            () => { },
            () => { },
            _ => { });
    }

    private static RemoteFleetSnapshot CreateSnapshot(string? activeExitNodeId) => new(
        RemoteFleetConnectionState.Degraded,
        "Private Headscale",
        "RemoteHub metadata is unavailable.",
        new[]
        {
            new RemoteDevice(
                "node:travel",
                "Travel-Laptop",
                "Alice",
                "Home office",
                true,
                true,
                DateTimeOffset.UtcNow,
                RemoteCapability.ExitNode,
                "100.64.0.20")
        },
        activeExitNodeId,
        DateTimeOffset.UtcNow);

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = ExceptionDispatchInfo.Capture(caught);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The WinForms test thread did not finish.");
        exception?.Throw();
    }

    private sealed class FakeFleetClient(RemoteFleetSnapshot snapshot) : IRemoteFleetClient
    {
        public RemoteFleetSnapshot GetCachedSnapshot() => snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeActionService : IRemoteActionService
    {
        public RemoteActionAvailability GetAvailability(RemoteDevice device, RemoteWebAction action) =>
            new(false, "Remote access is not configured in this test.");

        public RemoteActionResult Open(RemoteDevice device, RemoteWebAction action) =>
            RemoteActionResult.Failure("Not configured.");
    }

    private sealed class FakeExitNodeController : IRemoteExitNodeController
    {
        public int UseCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public RemoteDevice? LastUsedDevice { get; private set; }

        public bool LastAllowLocalNetworkAccess { get; private set; }

        public RemoteActionAvailability GetAvailability(RemoteDevice device) => RemoteActionAvailability.Available;

        public RemoteActionAvailability GetClearAvailability() => RemoteActionAvailability.Available;

        public Task<RemoteActionResult> UseExitNodeAsync(
            RemoteDevice device,
            bool allowLocalNetworkAccess,
            CancellationToken cancellationToken)
        {
            UseCalls++;
            LastUsedDevice = device;
            LastAllowLocalNetworkAccess = allowLocalNetworkAccess;
            return Task.FromResult(RemoteActionResult.Success("Enabled."));
        }

        public Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            return Task.FromResult(RemoteActionResult.Success("Disabled."));
        }
    }

    private sealed class FakeAdminConsoleLauncher : IRemoteAdminConsoleLauncher
    {
        public RemoteActionResult Open() => RemoteActionResult.Success("Opened.");
    }

    private sealed class FakeTokenProvider : IRemoteHubAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

        public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
