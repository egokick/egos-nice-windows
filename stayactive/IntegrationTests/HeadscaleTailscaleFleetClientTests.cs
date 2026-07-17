using System.Diagnostics;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class HeadscaleTailscaleFleetClientTests
{
    [Fact]
    public async Task RefreshAsync_UsesLiteralStatusArguments_AndOnlyMapsExplicitlyTaggedPeers()
    {
        var runner = new FakeTailscaleStatusProcessRunner
        {
            Result = SuccessResult(
                """
                {
                  "BackendState": "Running",
                  "User": {
                    "100": { "DisplayName": "Alice Example", "LoginName": "alice@example.test" }
                  },
                  "Peer": {
                    "nodekey:included": {
                      "ID": "node:included",
                      "HostName": "Austin-PC",
                      "UserID": 100,
                      "Tags": ["tag:stayactive"],
                      "Online": true,
                      "LastSeen": "2026-07-16T12:00:00Z",
                      "ExitNodeOption": true,
                      "TailscaleIPs": ["100.64.0.10"]
                    },
                    "nodekey:wrong-tag": {
                      "ID": "node:wrong-tag",
                      "HostName": "Unmanaged-PC",
                      "UserID": 100,
                      "Tags": ["tag:other"],
                      "Online": true
                    },
                    "nodekey:wrong-case": {
                      "ID": "node:wrong-case",
                      "HostName": "Wrong-Case-PC",
                      "UserID": 100,
                      "Tags": ["tag:StayActive"],
                      "Online": true
                    }
                  },
                  "ExitNodeStatus": {
                    "ID": "node:included",
                    "TailscaleIPs": ["100.64.0.10"]
                  }
                }
                """)
        };
        var client = CreateClient(runner);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(RemoteFleetConnectionState.Connected, snapshot.ConnectionState);
        Assert.Equal("Headscale mesh.example.test", snapshot.ControlPlaneDisplayName);
        Assert.Equal("node:included", snapshot.ActiveExitNodeId);
        Assert.Equal("Austin-PC", device.DeviceName);
        Assert.Equal("Alice Example", device.OwnerDisplayName);
        Assert.Null(device.Location);
        Assert.True(device.IsOnline);
        Assert.True(device.IsVerified);
        Assert.Equal("100.64.0.10", device.TailnetIp);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:00:00Z"), device.LastSeenAt);
        Assert.Equal(RemoteCapability.ExitNode, device.Capabilities);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\Program Files\Tailscale\tailscale.exe", call.FileName);
        Assert.False(call.UseShellExecute);
        Assert.True(call.RedirectStandardOutput);
        Assert.True(call.RedirectStandardError);
        Assert.Equal(string.Empty, call.Arguments);
        Assert.Equal(new[] { "status", "--json" }, call.ArgumentList);
    }

    [Fact]
    public async Task RefreshAsync_WhenControlPlaneIsNotSelfHostedHttps_DoesNotStartAProcess()
    {
        var runner = new FakeTailscaleStatusProcessRunner();
        var client = new HeadscaleTailscaleFleetClient(
            () => Preferences(controlPlaneUrl: "http://mesh.example.test"),
            runner,
            new FakeTailscaleExecutableLocator(@"C:\Program Files\Tailscale\tailscale.exe"));

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(RemoteFleetConnectionState.NotConfigured, snapshot.ConnectionState);
        Assert.Equal("A self-hosted HTTPS Headscale control-plane URL is required.", snapshot.StatusMessage);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("https://login.tailscale.com")]
    [InlineData("https://login.tailscale.com.")]
    public async Task RefreshAsync_WhenControlPlaneIsTheHostedTailscaleDomain_DoesNotStartAProcess(string controlPlaneUrl)
    {
        var runner = new FakeTailscaleStatusProcessRunner();
        var client = new HeadscaleTailscaleFleetClient(
            () => Preferences(controlPlaneUrl: controlPlaneUrl),
            runner,
            new FakeTailscaleExecutableLocator(@"C:\Program Files\Tailscale\tailscale.exe"));

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(RemoteFleetConnectionState.NotConfigured, client.GetCachedSnapshot().ConnectionState);
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task RefreshAsync_WhenTailscaleIsNotInstalled_ReportsDisconnectedWithoutStartingAProcess()
    {
        var runner = new FakeTailscaleStatusProcessRunner();
        var client = new HeadscaleTailscaleFleetClient(
            () => Preferences(),
            runner,
            new FakeTailscaleExecutableLocator(null));

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(RemoteFleetConnectionState.Disconnected, snapshot.ConnectionState);
        Assert.Contains("was not found", snapshot.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RefreshAsync_WhenStatusJsonIsMalformed_ReportsDisconnected()
    {
        var runner = new FakeTailscaleStatusProcessRunner
        {
            Result = SuccessResult("{ this is not valid JSON")
        };
        var client = CreateClient(runner);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(RemoteFleetConnectionState.Disconnected, snapshot.ConnectionState);
        Assert.Equal("The local Tailscale client returned an unreadable status response.", snapshot.StatusMessage);
    }

    [Fact]
    public async Task RefreshAsync_WhenRunnerCannotFindTheBinary_ReportsDisconnected()
    {
        var runner = new FakeTailscaleStatusProcessRunner
        {
            ExceptionToThrow = new FileNotFoundException("not installed")
        };
        var client = CreateClient(runner);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(RemoteFleetConnectionState.Disconnected, snapshot.ConnectionState);
        Assert.Contains("was not found", snapshot.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_WhenAnUntaggedExitNodeIsActive_DoesNotExposeItAsAManagedDevice()
    {
        var runner = new FakeTailscaleStatusProcessRunner
        {
            Result = SuccessResult(
                """
                {
                  "BackendState": "Running",
                  "Peer": {
                    "nodekey:managed": {
                      "ID": "node:managed",
                      "HostName": "Managed-PC",
                      "Tags": ["tag:stayactive"],
                      "Online": true
                    },
                    "nodekey:unmanaged": {
                      "ID": "node:unmanaged",
                      "HostName": "Unmanaged-Exit",
                      "Tags": ["tag:other"],
                      "Online": true,
                      "ExitNodeOption": true
                    }
                  },
                  "ExitNodeStatus": { "ID": "node:unmanaged" }
                }
                """)
        };
        var client = CreateClient(runner);

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(new[] { "Managed-PC" }, snapshot.Devices.Select(device => device.DeviceName));
        Assert.Null(snapshot.ActiveExitNodeId);
        Assert.Contains("outside that fleet", snapshot.StatusMessage, StringComparison.Ordinal);
    }

    private static HeadscaleTailscaleFleetClient CreateClient(FakeTailscaleStatusProcessRunner runner)
    {
        return new HeadscaleTailscaleFleetClient(
            () => Preferences(),
            runner,
            new FakeTailscaleExecutableLocator(@"C:\Program Files\Tailscale\tailscale.exe"),
            TimeSpan.FromSeconds(1));
    }

    private static RemoteClientPreferences Preferences(string controlPlaneUrl = "https://mesh.example.test")
    {
        return new RemoteClientPreferences(
            controlPlaneUrl,
            RemoteHubUrl: string.Empty,
            AdminConsoleUrl: string.Empty,
            MeshCentralUrl: string.Empty,
            DeviceDisplayName: "Local PC",
            Location: "Austin office");
    }

    private static TailscaleStatusProcessResult SuccessResult(string standardOutput)
    {
        return new TailscaleStatusProcessResult(0, standardOutput, string.Empty);
    }

    private sealed class FakeTailscaleStatusProcessRunner : ITailscaleStatusProcessRunner
    {
        public List<TailscaleStatusProcessCall> Calls { get; } = new();

        public TailscaleStatusProcessResult Result { get; set; } = SuccessResult("{ \"BackendState\": \"Running\" }");

        public Exception? ExceptionToThrow { get; set; }

        public Task<TailscaleStatusProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TailscaleStatusProcessCall(
                startInfo.FileName,
                startInfo.UseShellExecute,
                startInfo.RedirectStandardOutput,
                startInfo.RedirectStandardError,
                startInfo.Arguments,
                startInfo.ArgumentList.ToArray()));

            if (ExceptionToThrow is not null)
            {
                return Task.FromException<TailscaleStatusProcessResult>(ExceptionToThrow);
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeTailscaleExecutableLocator(string? executable) : ITailscaleExecutableLocator
    {
        public string? FindInstalledExecutable() => executable;
    }

    private sealed record TailscaleStatusProcessCall(
        string FileName,
        bool UseShellExecute,
        bool RedirectStandardOutput,
        bool RedirectStandardError,
        string Arguments,
        IReadOnlyList<string> ArgumentList);
}
