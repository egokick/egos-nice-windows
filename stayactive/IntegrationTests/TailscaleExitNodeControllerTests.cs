using System.Diagnostics;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class TailscaleExitNodeControllerTests
{
    [Fact]
    public async Task UseExitNodeAsync_UsesOnlyLiteralTailscaleSetArguments()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var readOnlyRunner = new FakeReadOnlyRunner();
        var controller = CreateController(runner, readOnlyRunner);

        var result = await controller.UseExitNodeAsync(
            CreateExitNode("100.64.0.10"),
            allowLocalNetworkAccess: false,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\Program Files\Tailscale\tailscale.exe", call.FileName);
        Assert.False(call.UseShellExecute);
        Assert.Equal(string.Empty, call.Arguments);
        Assert.Equal(
            new[] { "set", "--exit-node=100.64.0.10", "--exit-node-allow-lan-access=false" },
            call.ArgumentList);

        var trustCall = Assert.Single(readOnlyRunner.Calls);
        Assert.Equal(@"C:\Program Files\Tailscale\tailscale.exe", trustCall.FileName);
        Assert.False(trustCall.UseShellExecute);
        Assert.Equal(string.Empty, trustCall.Arguments);
        Assert.Equal(new[] { "debug", "prefs" }, trustCall.ArgumentList);
    }

    [Fact]
    public async Task ClearExitNodeAsync_UsesOnlyTheClearExitNodeArgument()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var readOnlyRunner = new FakeReadOnlyRunner();
        var controller = CreateController(runner, readOnlyRunner);

        var result = await controller.ClearExitNodeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "set", "--exit-node=" }, call.ArgumentList);
        Assert.Empty(readOnlyRunner.Calls);
    }

    [Fact]
    public async Task ClearExitNodeAsync_WhenControlPlaneIsInvalid_RemainsAvailableWithoutTrustCheck()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var readOnlyRunner = new FakeReadOnlyRunner
        {
            ExceptionToThrow = new InvalidOperationException("must not run")
        };
        var controller = new TailscaleExitNodeController(
            () => Preferences("https://login.tailscale.com"),
            new FakeLocator(@"C:\Program Files\Tailscale\tailscale.exe"),
            runner,
            readOnlyRunner,
            TimeSpan.FromSeconds(1));

        Assert.True(controller.GetClearAvailability().IsAvailable);
        var result = await controller.ClearExitNodeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "set", "--exit-node=" }, Assert.Single(runner.Calls).ArgumentList);
        Assert.Empty(readOnlyRunner.Calls);
    }

    [Fact]
    public async Task UseExitNodeAsync_RejectsAnInvalidAddressBeforeStartingAProcess()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var controller = CreateController(runner);
        var device = CreateExitNode("100.64.0.10 --advertise-exit-node");

        var result = await controller.UseExitNodeAsync(device, false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
        Assert.Contains("valid Headscale address", result.Message);
    }

    [Fact]
    public async Task UseExitNodeAsync_WhenTheControlPlaneIsHostedTailscale_DoesNotStartAProcess()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var controller = new TailscaleExitNodeController(
            () => Preferences("https://login.tailscale.com"),
            new FakeLocator(@"C:\Program Files\Tailscale\tailscale.exe"),
            runner,
            new FakeReadOnlyRunner(),
            TimeSpan.FromSeconds(1));

        var result = await controller.UseExitNodeAsync(CreateExitNode("100.64.0.10"), false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
        Assert.Contains("self-hosted Headscale", result.Message);
    }

    [Fact]
    public async Task UseExitNodeAsync_WhenReportedControlPlaneDiffers_FailsClosedBeforeRouteChange()
    {
        const string untrustedUrl = "https://untrusted.example.test";
        const string diagnostic = "sensitive local diagnostic";
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var readOnlyRunner = new FakeReadOnlyRunner
        {
            Result = new TailscaleStatusProcessResult(
                0,
                "{ \"ControlURL\": \"" + untrustedUrl + "\" }",
                diagnostic)
        };
        var controller = CreateController(runner, readOnlyRunner);

        var result = await controller.UseExitNodeAsync(
            CreateExitNode("100.64.0.10"),
            false,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain(untrustedUrl, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(diagnostic, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "debug", "prefs" }, Assert.Single(readOnlyRunner.Calls).ArgumentList);
    }

    [Fact]
    public async Task UseExitNodeAsync_WhenPreferencesCannotBeRead_FailsClosedBeforeRouteChange()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(0) };
        var readOnlyRunner = new FakeReadOnlyRunner
        {
            ExceptionToThrow = new InvalidOperationException("sensitive local diagnostic")
        };
        var controller = CreateController(runner, readOnlyRunner);

        var result = await controller.UseExitNodeAsync(
            CreateExitNode("100.64.0.10"),
            false,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain("sensitive", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UseExitNodeAsync_WhenTailscaleRejectsIt_DoesNotExposeCommandDiagnostics()
    {
        var runner = new FakeExitNodeRunner { Result = new TailscaleExitNodeProcessResult(1) };
        var controller = CreateController(runner);

        var result = await controller.UseExitNodeAsync(CreateExitNode("100.64.0.10"), false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Tailscale rejected the requested exit-node change. Check your Headscale policy and route approval.", result.Message);
    }

    private static TailscaleExitNodeController CreateController(
        FakeExitNodeRunner runner,
        FakeReadOnlyRunner? readOnlyRunner = null)
    {
        return new TailscaleExitNodeController(
            () => Preferences("https://headscale.example.test"),
            new FakeLocator(@"C:\Program Files\Tailscale\tailscale.exe"),
            runner,
            readOnlyRunner ?? new FakeReadOnlyRunner(),
            TimeSpan.FromSeconds(1));
    }

    private static RemoteClientPreferences Preferences(string controlPlaneUrl)
    {
        return new RemoteClientPreferences(
            controlPlaneUrl,
            string.Empty,
            string.Empty,
            string.Empty,
            "Local PC",
            "Test lab");
    }

    private static RemoteDevice CreateExitNode(string tailnetIp)
    {
        return new RemoteDevice(
            "remote-id",
            "Austin-PC",
            "Alice",
            "Austin office",
            true,
            true,
            DateTimeOffset.UtcNow,
            RemoteCapability.ExitNode,
            tailnetIp);
    }

    private sealed class FakeLocator(string? executable) : ITailscaleExecutableLocator
    {
        public string? FindInstalledExecutable() => executable;
    }

    private sealed class FakeExitNodeRunner : ITailscaleExitNodeProcessRunner
    {
        public List<TailscaleExitNodeCall> Calls { get; } = new();

        public TailscaleExitNodeProcessResult Result { get; set; } = new(0);

        public Task<TailscaleExitNodeProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TailscaleExitNodeCall(
                startInfo.FileName,
                startInfo.UseShellExecute,
                startInfo.Arguments,
                startInfo.ArgumentList.ToArray()));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeReadOnlyRunner : ITailscaleStatusProcessRunner
    {
        public List<TailscaleReadOnlyCall> Calls { get; } = new();

        public TailscaleStatusProcessResult Result { get; set; } = new(
            0,
            "{ \"ControlURL\": \"https://headscale.example.test\" }",
            string.Empty);

        public Exception? ExceptionToThrow { get; set; }

        public Task<TailscaleStatusProcessResult> RunAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            Calls.Add(new TailscaleReadOnlyCall(
                startInfo.FileName,
                startInfo.UseShellExecute,
                startInfo.Arguments,
                startInfo.ArgumentList.ToArray()));

            return ExceptionToThrow is null
                ? Task.FromResult(Result)
                : Task.FromException<TailscaleStatusProcessResult>(ExceptionToThrow);
        }
    }

    private sealed record TailscaleExitNodeCall(
        string FileName,
        bool UseShellExecute,
        string Arguments,
        IReadOnlyList<string> ArgumentList);

    private sealed record TailscaleReadOnlyCall(
        string FileName,
        bool UseShellExecute,
        string Arguments,
        IReadOnlyList<string> ArgumentList);
}
