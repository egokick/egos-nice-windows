using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace StayActive.Remotes;

internal sealed record TailscaleExitNodeProcessResult(int ExitCode);

/// <summary>
/// Narrow process boundary for one local route-selection command. Callers receive
/// a fully constructed start-info object; this code never accepts a command line.
/// </summary>
internal interface ITailscaleExitNodeProcessRunner
{
    Task<TailscaleExitNodeProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}

internal interface IRemoteExitNodeController
{
    RemoteActionAvailability GetAvailability(RemoteDevice device);

    RemoteActionAvailability GetClearAvailability();

    Task<RemoteActionResult> UseExitNodeAsync(
        RemoteDevice device,
        bool allowLocalNetworkAccess,
        CancellationToken cancellationToken);

    Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Selects or clears a Headscale-approved exit node through the locally installed
/// Tailscale client. A new route is selected only after the read-only local
/// preferences prove that the client is currently bound to the configured
/// self-hosted controller. Clearing an existing route remains available as a
/// recovery action. This component intentionally supports no other Tailscale
/// operation.
/// </summary>
internal sealed class TailscaleExitNodeController : IRemoteExitNodeController
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly ITailscaleExecutableLocator _executableLocator;
    private readonly ITailscaleExitNodeProcessRunner _runner;
    private readonly ITailscaleStatusProcessRunner _readOnlyRunner;
    private readonly TimeSpan _commandTimeout;

    public TailscaleExitNodeController(Func<RemoteClientPreferences> getPreferences)
        : this(
            getPreferences,
            new SystemTailscaleExecutableLocator(),
            new SystemTailscaleExitNodeProcessRunner(),
            new SystemTailscaleStatusProcessRunner(),
            DefaultCommandTimeout)
    {
    }

    internal TailscaleExitNodeController(
        Func<RemoteClientPreferences> getPreferences,
        ITailscaleExecutableLocator executableLocator,
        ITailscaleExitNodeProcessRunner runner,
        ITailscaleStatusProcessRunner readOnlyRunner,
        TimeSpan? commandTimeout = null)
    {
        _getPreferences = getPreferences ?? throw new ArgumentNullException(nameof(getPreferences));
        _executableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _readOnlyRunner = readOnlyRunner ?? throw new ArgumentNullException(nameof(readOnlyRunner));
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        if (_commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }
    }

    public RemoteActionAvailability GetAvailability(RemoteDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        return GetAvailability(device, _getPreferences());
    }

    private RemoteActionAvailability GetAvailability(
        RemoteDevice device,
        RemoteClientPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!TailscaleControlPlaneBinding.TryCreateSelfHostedControlPlane(
                preferences.ControlPlaneUrl,
                out _))
        {
            return new RemoteActionAvailability(false, "Configure an HTTPS self-hosted Headscale URL first.");
        }

        if (!device.IsVerified || !device.IsOnline)
        {
            return new RemoteActionAvailability(false, "The selected computer must be verified and online.");
        }

        if (!device.Capabilities.HasFlag(RemoteCapability.ExitNode))
        {
            return new RemoteActionAvailability(false, "This computer is not an approved exit node.");
        }

        if (!IPAddress.TryParse(device.TailnetIp, out _))
        {
            return new RemoteActionAvailability(false, "This computer has no valid Headscale address for exit routing.");
        }

        if (!TryGetExecutable(out _))
        {
            return new RemoteActionAvailability(false, "The local Tailscale client was not found.");
        }

        return RemoteActionAvailability.Available;
    }

    public RemoteActionAvailability GetClearAvailability()
    {
        // Clearing is always a safe recovery operation, even when the configured
        // controller is missing, invalid, or no longer matches local preferences.
        return TryGetExecutable(out _)
            ? RemoteActionAvailability.Available
            : new RemoteActionAvailability(false, "The local Tailscale client was not found.");
    }

    public async Task<RemoteActionResult> UseExitNodeAsync(
        RemoteDevice device,
        bool allowLocalNetworkAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var preferences = _getPreferences();
        var availability = GetAvailability(device, preferences);
        if (!availability.IsAvailable)
        {
            return RemoteActionResult.Failure(availability.Reason);
        }

        if (!TailscaleControlPlaneBinding.TryCreateSelfHostedControlPlane(
                preferences.ControlPlaneUrl,
                out var configuredControlPlane))
        {
            return RemoteActionResult.Failure("Configure an HTTPS self-hosted Headscale URL first.");
        }

        if (!TryGetExecutable(out var executable))
        {
            return RemoteActionResult.Failure("The local Tailscale client was not found.");
        }

        if (!await IsBoundToConfiguredControlPlaneAsync(
                executable,
                configuredControlPlane,
                cancellationToken).ConfigureAwait(false))
        {
            return RemoteActionResult.Failure(
                "The local Tailscale client could not be verified against the configured self-hosted Headscale control plane.");
        }

        return await RunAsync(
            CreateUseExitNodeStartInfo(executable, device.TailnetIp!, allowLocalNetworkAccess),
            $"Tailscale accepted the request to route internet traffic through {device.DeviceName}.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
    {
        var availability = GetClearAvailability();
        if (!availability.IsAvailable)
        {
            return RemoteActionResult.Failure(availability.Reason);
        }

        if (!TryGetExecutable(out var executable))
        {
            return RemoteActionResult.Failure("The local Tailscale client was not found.");
        }

        return await RunAsync(
            CreateClearExitNodeStartInfo(executable),
            "Tailscale accepted the request to restore direct internet routing.",
            cancellationToken).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateUseExitNodeStartInfo(
        string executable,
        string exitNodeAddress,
        bool allowLocalNetworkAccess)
    {
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("A fully qualified Tailscale executable path is required.", nameof(executable));
        }

        if (!IPAddress.TryParse(exitNodeAddress, out _))
        {
            throw new ArgumentException("A valid IP address is required for an exit node.", nameof(exitNodeAddress));
        }

        var startInfo = CreateBaseStartInfo(executable);
        startInfo.ArgumentList.Add("set");
        startInfo.ArgumentList.Add("--exit-node=" + exitNodeAddress);
        startInfo.ArgumentList.Add("--exit-node-allow-lan-access=" + (allowLocalNetworkAccess ? "true" : "false"));
        return startInfo;
    }

    internal static ProcessStartInfo CreateClearExitNodeStartInfo(string executable)
    {
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("A fully qualified Tailscale executable path is required.", nameof(executable));
        }

        var startInfo = CreateBaseStartInfo(executable);
        startInfo.ArgumentList.Add("set");
        startInfo.ArgumentList.Add("--exit-node=");
        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executable)
    {
        return new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private async Task<bool> IsBoundToConfiguredControlPlaneAsync(
        string executable,
        Uri configuredControlPlane,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_commandTimeout);

        try
        {
            var result = await _readOnlyRunner.RunAsync(
                TailscaleControlPlaneBinding.CreateDebugPreferencesStartInfo(executable),
                timeoutCancellation.Token).ConfigureAwait(false);

            return result.ExitCode == 0
                && TailscaleControlPlaneBinding.MatchesConfiguredControlPlane(
                    result.StandardOutput,
                    configuredControlPlane);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail closed and never surface local CLI output or exception details.
            return false;
        }
    }

    private async Task<RemoteActionResult> RunAsync(
        ProcessStartInfo startInfo,
        string successMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_commandTimeout);

        try
        {
            var result = await _runner.RunAsync(startInfo, timeoutCancellation.Token).ConfigureAwait(false);
            return result.ExitCode == 0
                ? RemoteActionResult.Success(successMessage)
                : RemoteActionResult.Failure("Tailscale rejected the requested exit-node change. Check your Headscale policy and route approval.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteActionResult.Failure("The exit-node change timed out.");
        }
        catch (FileNotFoundException)
        {
            return RemoteActionResult.Failure("The local Tailscale client was not found.");
        }
        catch (DirectoryNotFoundException)
        {
            return RemoteActionResult.Failure("The local Tailscale client was not found.");
        }
        catch (Win32Exception)
        {
            return RemoteActionResult.Failure("The local Tailscale client could not be started.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteActionResult.Failure("The exit-node change could not be completed.");
        }
    }

    private bool TryGetExecutable(out string executable)
    {
        executable = _executableLocator.FindInstalledExecutable() ?? string.Empty;
        // The system locator itself returns only an existing Program Files binary.
        // Keeping this boundary free of a second filesystem lookup also makes the
        // strict locator contract independently testable.
        return Path.IsPathFullyQualified(executable);
    }
}

internal sealed class SystemTailscaleExitNodeProcessRunner : ITailscaleExitNodeProcessRunner
{
    public async Task<TailscaleExitNodeProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("The Tailscale route command must not use a shell.");
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The local Tailscale client could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new FileNotFoundException("The local Tailscale client was not found.", startInfo.FileName, exception);
        }

        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        // Drain diagnostics so a failing child cannot block on a full pipe, but do
        // not retain or expose them because they may contain local diagnostics.
        var discardStandardOutput = DrainAsync(process.StandardOutput, cancellationToken);
        var discardStandardError = DrainAsync(process.StandardError, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(discardStandardOutput, discardStandardError).ConfigureAwait(false);
        return new TailscaleExitNodeProcessResult(process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cancellation is best effort.
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
            {
                // Intentionally discard local CLI diagnostics.
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }
}
