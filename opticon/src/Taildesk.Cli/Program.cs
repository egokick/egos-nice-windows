using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taildesk.Admin;
using Taildesk.Shared;

namespace Taildesk.Cli;

internal static class Program
{
    private const string ControllerOwnershipMarkerName = ".opticon-controller-owned";
    private const string ControllerOwnershipMarkerValue = "Opticon command-center controller payload v1";
    private const string ControllerReadyMarkerName = ".opticon-controller-ready";
    private const string ControllerReadyMarkerValue = "Opticon command-center controller payload ready v1";
    private const string ControllerInstallLockFileName = ".controller-install.lock";
    private const int Success = 0;
    private const int OperationalFailure = 1;
    private const int UsageFailure = 2;
    private const int Cancelled = 130;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var cancellation = new CancellationTokenSource();
        var interactiveSshAttached = 0;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (Volatile.Read(ref interactiveSshAttached) == 0)
            {
                cancellation.Cancel();
            }
        };

        var json = RequestsJson(args);
        try
        {
            await using var installationLease = await AcquireControllerLifetimeLeaseAsync(cancellation.Token);
            return await new CliApplication(
                    attached => Volatile.Write(ref interactiveSshAttached, attached ? 1 : 0))
                .RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            WriteError(json, "cancelled", "The Opticon command was cancelled.");
            return Cancelled;
        }
        catch (CliException exception)
        {
            WriteError(json, exception.Code, exception.Message);
            return exception.ExitCode;
        }
        catch (Exception exception)
        {
            WriteError(json, "operation_failed", SanitizeDiagnostic(exception.Message));
            return OperationalFailure;
        }
    }

    private static async Task<FileStream?> AcquireControllerLifetimeLeaseAsync(
        CancellationToken cancellationToken)
    {
        var runningExecutable = Environment.ProcessPath is { Length: > 0 } value
            ? Path.GetFullPath(value)
            : null;
        if (runningExecutable is null) return null;

        var controllerRoot = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, "Admin"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var liveCli = Path.Combine(controllerRoot, "Cli", "opticon.exe");
        var previousRoot = controllerRoot + ".previous";
        var previousCli = Path.Combine(previousRoot, "Cli", "opticon.exe");
        var isLive = runningExecutable.Equals(liveCli, StringComparison.OrdinalIgnoreCase);
        var isPrevious = runningExecutable.Equals(previousCli, StringComparison.OrdinalIgnoreCase);
        if (!isLive && !isPrevious) return null;
        var payloadRoot = isLive ? controllerRoot : previousRoot;

        var lockPath = Path.Combine(AppPaths.InstallDirectory, ControllerInstallLockFileName);
        if (!File.Exists(lockPath))
            throw new FileNotFoundException(
                "The installed Opticon CLI lock is missing. Run command-center repair.",
                lockPath);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lease = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.None);
                if (!await HasExactControllerMarkersAsync(payloadRoot, cancellationToken))
                {
                    await lease.DisposeAsync();
                    throw new InvalidDataException(
                        "The installed Opticon CLI payload changed while waiting for command-center repair.");
                }
                return lease;
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        "The Opticon CLI waited two minutes for another controller installation to finish.",
                        exception);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException(
                    "The Opticon installation lock cannot be read. Run command-center repair.",
                    exception);
            }
        }
    }

    private static async Task<bool> HasExactControllerMarkersAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var ownershipMarker = Path.Combine(directory, ControllerOwnershipMarkerName);
            if (!File.Exists(ownershipMarker)
                || !string.Equals(
                    await File.ReadAllTextAsync(ownershipMarker, cancellationToken),
                    ControllerOwnershipMarkerValue,
                    StringComparison.Ordinal))
                return false;
            var executingVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (executingVersion is null) return false;
            var readyMarker = Path.Combine(directory, ControllerReadyMarkerName);
            return File.Exists(readyMarker)
                   && string.Equals(
                       await File.ReadAllTextAsync(readyMarker, cancellationToken),
                       $"{ControllerReadyMarkerValue}|{executingVersion}",
                       StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool RequestsJson(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return false;
        var command = args[0].ToLowerInvariant();
        return command is "devices" or "status" or "update"
               && args.Skip(1).Any(value => value.Equals("--json", StringComparison.Ordinal));
    }

    private static void WriteError(bool json, string code, string message)
    {
        message = SanitizeDiagnostic(message);
        if (!json)
        {
            Console.Error.WriteLine($"opticon: {message}");
            return;
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(
            new ErrorEnvelope(1, false, new ErrorBody(code, message)),
            JsonDefaults.Options));
    }

    internal static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "The operation failed without a diagnostic message.";
        var sanitized = new string(value
            .Where(IsSafeOutputCharacter)
            .ToArray()).Trim();
        return sanitized.Length <= 1000 ? sanitized : sanitized[..1000];
    }

    internal static bool IsSafeOutputCharacter(char character) =>
        !char.IsControl(character)
        && !char.IsSurrogate(character)
        && char.GetUnicodeCategory(character) != UnicodeCategory.Format;

    private sealed record ErrorEnvelope(int SchemaVersion, bool Ok, ErrorBody Error);
    private sealed record ErrorBody(string Code, string Message);
}

internal sealed class CliApplication
{
    private readonly AgentClient _agents = new();
    private readonly OpticonReleaseClient _releases = new();
    private readonly Action<bool> _setInteractiveSshAttached;

    public CliApplication(Action<bool>? setInteractiveSshAttached = null)
    {
        _setInteractiveSshAttached = setInteractiveSshAttached ?? (static _ => { });
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "devices" => await RunDevicesAsync(args[1..], cancellationToken),
            "status" => await RunStatusAsync(args[1..], cancellationToken),
            "ssh" => await RunSshAsync(args[1..], cancellationToken),
            "update" => await RunUpdateAsync(args[1..], cancellationToken),
            "version" or "--version" or "-v" => RunVersion(args[1..]),
            _ => throw CliException.Usage($"Unknown command '{Clean(args[0])}'. Run 'opticon help' for usage.")
        };
    }

    private async Task<int> RunDevicesAsync(string[] args, CancellationToken cancellationToken)
    {
        var json = ParseJsonOnly(args, "devices");
        var state = await LoadStateAsync(requireSetup: true, cancellationToken);
        var devices = state.Config.Devices
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id)
            .Select(ToDeviceSummary)
            .ToArray();

        if (json)
        {
            WriteJson(new DevicesEnvelope(1, true, "devices", devices));
            return 0;
        }

        if (devices.Length == 0)
        {
            Console.Out.WriteLine("No devices are registered in this command center.");
            return 0;
        }

        Console.Out.WriteLine("ID\tNAME\tHOST\tTAILSCALE IP\tSTATE\tVERSION\tROLE");
        foreach (var device in devices)
        {
            Console.Out.WriteLine(string.Join('\t',
                device.Id,
                Clean(device.Name),
                Clean(device.HostName),
                Clean(device.TailscaleIp),
                device.State,
                Clean(device.AgentVersion),
                device.Role));
        }
        return 0;
    }

    private async Task<int> RunStatusAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseSelectorWithJson(args, "status");
        var state = await LoadStateAsync(requireSetup: true, cancellationToken);
        RequirePrimary(state.Config, "Live device status");
        var device = SelectDevice(state.Config.Devices, parsed.Selector);
        var token = GetAgentToken(device);
        var status = await GetVerifiedStatusAsync(device, token, cancellationToken);
        var output = ToStatus(device, status);

        if (parsed.Json)
        {
            WriteJson(new StatusEnvelope(1, true, "status", output));
            return 0;
        }

        Console.Out.WriteLine($"Name: {Clean(output.Name)}");
        Console.Out.WriteLine($"ID: {output.Id}");
        Console.Out.WriteLine($"Host: {Clean(output.HostName)}");
        Console.Out.WriteLine($"Tailscale IP: {Clean(output.TailscaleIp)}");
        Console.Out.WriteLine($"State: online");
        Console.Out.WriteLine($"Agent version: {Clean(output.AgentVersion)}");
        Console.Out.WriteLine($"Update protocol: {output.UpdateProtocolVersion}");
        Console.Out.WriteLine($"OS: {Clean(output.OperatingSystem)} ({Clean(output.Architecture)})");
        Console.Out.WriteLine($"RustDesk recovery: {(output.RustDeskReady ? "ready" : "not ready")}");
        Console.Out.WriteLine($"Administrative SSH: {(output.SshReady ? $"ready on {output.SshPort}" : "idle")}");
        Console.Out.WriteLine($"Disk free: {FormatBytes(output.FreeDiskBytes)} of {FormatBytes(output.TotalDiskBytes)}");
        Console.Out.WriteLine($"Target time: {output.ServerTime:O}");
        if (output.Update is not null)
            Console.Out.WriteLine($"Update: {output.Update.Phase} ({Clean(output.Update.Message)})");
        return 0;
    }

    private async Task<int> RunSshAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseSsh(args);
        var state = await LoadStateAsync(requireSetup: true, cancellationToken);
        RequirePrimary(state.Config, "Administrative SSH");
        var device = SelectDevice(state.Config.Devices, options.Selector);
        var token = GetAgentToken(device);
        await GetVerifiedStatusAsync(device, token, cancellationToken);

        string? remoteCommand = options.Command;
        string? powerShellEncoded = null;
        if (options.PowerShell is not null)
        {
            var script = options.PowerShell == "-"
                ? await Console.In.ReadToEndAsync(cancellationToken)
                : options.PowerShell;
            if (string.IsNullOrWhiteSpace(script))
                throw CliException.Usage("--powershell requires a non-empty script or '-' for standard input.");
            powerShellEncoded = EncodePowerShell(script);
        }

        var interactive = remoteCommand is null && powerShellEncoded is null;
        Console.Error.WriteLine(
            $"Requesting a {options.Minutes}-minute, host-key-pinned administrative SSH lease for {Clean(device.Name)}...");
        var requestedLifetime = TimeSpan.FromMinutes(options.Minutes);
        await using var handle = await SshSessionLauncher.LaunchAsync(
            new SshSessionLaunchOptions
            {
                ExpectedHost = device.TailscaleIp,
                RequestedLifetime = requestedLifetime,
                RemoteCommand = remoteCommand,
                PowerShellEncodedCommand = powerShellEncoded,
                AllocateTerminal = interactive
            },
            async (publicKey, lifetime, innerCancellation) =>
            {
                var requestedAt = DateTimeOffset.UtcNow;
                var grant = await _agents.OpenSshAsync(device, token, new SshAccessRequest
                {
                    PublicKey = publicKey,
                    RequestedLifetimeSeconds = checked((int)lifetime.TotalSeconds),
                    ExpiresAt = requestedAt.Add(lifetime)
                }, innerCancellation);
                return grant;
            },
            (sessionId, innerCancellation) =>
                _agents.RevokeSshAsync(device, token, sessionId, innerCancellation),
            cancellationToken);

        if (interactive)
        {
            _setInteractiveSshAttached(true);
        }

        try
        {
            var exitCode = await handle.WaitForExitAsync(cancellationToken);
            if (handle.RemoteRevocationError is not null)
            {
                Console.Error.WriteLine(
                    "opticon: warning: immediate SSH lease revocation could not be confirmed; " +
                    "the target's independent expiry remains in force.");
            }
            if (handle.LocalCleanupError is not null)
            {
                Console.Error.WriteLine(
                    "opticon: warning: the ephemeral local SSH key directory could not be removed; " +
                    "Opticon will retry stale cleanup on the next SSH launch.");
            }
            return exitCode;
        }
        finally
        {
            if (interactive)
            {
                _setInteractiveSshAttached(false);
            }
        }
    }

    private async Task<int> RunUpdateAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseUpdate(args);
        if (!options.Confirmed)
            throw CliException.Usage("Remote update requires --yes after reviewing the recovery-channel requirements.");

        var state = await LoadStateAsync(requireSetup: true, cancellationToken);
        RequirePrimary(state.Config, "Remote update");
        var device = SelectDevice(state.Config.Devices, options.Selector);
        var token = GetAgentToken(device);
        await GetVerifiedStatusAsync(device, token, cancellationToken);
        var release = await _releases.FindUpdateAsync(state.Config, device, cancellationToken);

        if (release is null)
        {
            var noUpdate = new UpdateEnvelope(
                1, true, "update", "upToDate", ToDeviceIdentity(device),
                device.AgentVersion, null, null);
            if (options.Json) WriteJson(noUpdate);
            else Console.Out.WriteLine($"{Clean(device.Name)} is already on the newest available Opticon release ({Clean(device.AgentVersion)}).");
            return 0;
        }

        if (release.RequiresMaintenanceBootstrap)
        {
            throw new CliException(
                "maintenance_bootstrap_required",
                "This legacy Agent requires the signed one-time maintenance bootstrap. " +
                "Use the Opticon UI with a verified RustDesk recovery session; the CLI will not automate this higher-risk transition.",
                1);
        }

        var progress = new InlineProgress<string>(message =>
            Console.Error.WriteLine(Program.SanitizeDiagnostic(message)));
        var coordinator = new RemoteDeviceUpdateCoordinator(_agents);
        var result = await coordinator.UpdateAsync(device, token, release, progress, cancellationToken);

        var outcome = result.Phase switch
        {
            UpdatePhase.Committed => "committed",
            UpdatePhase.RolledBack => "rolledBack",
            UpdatePhase.Failed => "failed",
            _ => "incomplete"
        };
        var output = new UpdateEnvelope(
            1,
            result.Phase == UpdatePhase.Committed,
            "update",
            outcome,
            ToDeviceIdentity(device),
            result.CurrentVersion,
            result.TargetVersion,
            ToUpdate(result));

        if (options.Json)
        {
            WriteJson(output);
        }
        else
        {
            Console.Out.WriteLine(result.Phase switch
            {
                UpdatePhase.Committed =>
                    $"Opticon Agent {Clean(result.TargetVersion)} committed on {Clean(device.Name)}.",
                UpdatePhase.RolledBack =>
                    $"{Clean(device.Name)} safely rolled back to Opticon {Clean(result.CurrentVersion)}.",
                _ =>
                    $"Update on {Clean(device.Name)} ended in {result.Phase}: {Clean(result.Message)}"
            });
        }
        return result.Phase == UpdatePhase.Committed ? 0 : 1;
    }

    private static int RunVersion(string[] args)
    {
        if (args.Length != 0)
            throw CliException.Usage("version does not accept arguments.");
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString(3) ?? "unknown";
        Console.Out.WriteLine($"opticon {version}");
        return 0;
    }

    private async Task<DeviceStatusDto> GetVerifiedStatusAsync(
        DeviceRecord device,
        string token,
        CancellationToken cancellationToken)
    {
        DeviceStatusDto status;
        try
        {
            status = await _agents.GetStatusAsync(device, token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CliException(
                "agent_unreachable",
                $"The authenticated Opticon Agent on {Clean(device.Name)} could not be reached: " +
                Program.SanitizeDiagnostic(exception.Message),
                1);
        }

        if (!AgentClient.IsTailscaleIp(status.TailscaleIp)
            || !string.Equals(status.TailscaleIp, device.TailscaleIp, StringComparison.Ordinal))
            throw new CliException(
                "identity_mismatch",
                "The authenticated Agent reported a different or invalid Tailscale address; refusing the operation.",
                1);
        if (string.IsNullOrWhiteSpace(device.TailnetDeviceId)
            || string.IsNullOrWhiteSpace(status.TailnetDeviceId)
            || !string.Equals(device.TailnetDeviceId, status.TailnetDeviceId, StringComparison.Ordinal))
            throw new CliException(
                "identity_mismatch",
                "The stored and authenticated Agent Tailnet identities must both be present and match exactly; refusing the operation.",
                1);

        ApplyLiveStatus(device, status);
        return status;
    }

    private static void ApplyLiveStatus(DeviceRecord device, DeviceStatusDto status)
    {
        device.State = DeviceConnectionState.Online;
        device.LastSeen = DateTimeOffset.UtcNow;
        device.HostName = status.HostName;
        device.OperatingSystem = status.OperatingSystem;
        device.Architecture = status.Architecture;
        device.AgentVersion = status.AgentVersion;
        device.UpdateProtocolVersion = status.UpdateProtocolVersion;
        device.AdvertisesExitNode = status.AdvertisesExitNode;
        device.RustDeskReady = status.RustDeskReady;
        device.SshReady = status.SshReady;
        device.SshPort = status.SshPort;
        device.UpdateStatus = status.UpdateStatus;
    }

    private static async Task<AdminState> LoadStateAsync(
        bool requireSetup,
        CancellationToken cancellationToken)
    {
        var state = new AdminState();
        await state.InitializeAsync(cancellationToken);
        if (requireSetup && !state.Config.SetupComplete)
            throw new CliException(
                "not_configured",
                "Opticon command-center setup is incomplete. Complete setup in the Opticon UI first.",
                1);
        return state;
    }

    private static void RequirePrimary(AdminConfig config, string operation)
    {
        if (config.Mode != AdminMode.Primary)
            throw new CliException(
                "primary_required",
                $"{operation} requires the primary Opticon command center.",
                1);
    }

    private static string GetAgentToken(DeviceRecord device)
    {
        if (string.IsNullOrWhiteSpace(device.AgentTokenProtected))
            throw new CliException(
                "credentials_unavailable",
                "The selected device has no local Agent credential.",
                1);
        try
        {
            var token = SecretProtector.Unprotect(device.AgentTokenProtected);
            if (string.IsNullOrWhiteSpace(token)) throw new CryptographicException();
            return token;
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or FormatException
            or System.ComponentModel.Win32Exception)
        {
            throw new CliException(
                "credentials_unavailable",
                "The Agent credential cannot be opened. Run opticon as the same Windows user that configured the command center.",
                1);
        }
    }

    private static DeviceRecord SelectDevice(IEnumerable<DeviceRecord> source, string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0)
            throw CliException.Usage("A non-empty device selector is required.");

        var devices = source.ToArray();
        DeviceRecord[] matches;
        if (Guid.TryParse(selector, out var id))
        {
            matches = devices.Where(device => device.Id == id).ToArray();
        }
        else
        {
            matches = devices.Where(device =>
                    string.Equals(device.TailscaleIp, selector, StringComparison.Ordinal)
                    || string.Equals(device.TailnetDeviceId, selector, StringComparison.Ordinal)
                    || string.Equals(device.Name, selector, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.HostName, selector, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.DnsName, selector, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(device => device.Id)
                .ToArray();
        }

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new CliException(
                "device_not_found",
                $"No device exactly matches '{Clean(selector)}'. Use 'opticon devices' to list valid selectors.",
                1),
            _ => throw new CliException(
                "ambiguous_device",
                $"'{Clean(selector)}' exactly matches multiple devices. Select one by its device ID: " +
                string.Join(", ", matches.OrderBy(device => device.Id).Select(device => device.Id)),
                1)
        };
    }

    private static string EncodePowerShell(string script)
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        try { return Convert.ToBase64String(bytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ParseJsonOnly(string[] args, string command)
    {
        if (args.Length == 0) return false;
        if (args.Length == 1 && args[0] == "--json") return true;
        throw CliException.Usage($"Usage: opticon {command} [--json]");
    }

    private static SelectorOptions ParseSelectorWithJson(string[] args, string command)
    {
        if (args.Length is < 1 or > 2)
            throw CliException.Usage($"Usage: opticon {command} <device> [--json]");
        if (args[0] == "--json")
            throw CliException.Usage($"Usage: opticon {command} <device> [--json]");
        if (args.Length == 2 && args[1] != "--json")
            throw CliException.Usage($"Unknown option '{Clean(args[1])}' for {command}.");
        return new SelectorOptions(args[0], args.Length == 2);
    }

    private static SshOptions ParseSsh(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            throw CliException.Usage(
                "Usage: opticon ssh <device> [--minutes 5..480] [--command <command> | --powershell <script|->]");
        var selector = args[0];
        var minutes = 60;
        string? command = null;
        string? powerShell = null;
        var sawMinutes = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--minutes":
                    if (sawMinutes) throw CliException.Usage("--minutes may be specified only once.");
                    sawMinutes = true;
                    if (++index >= args.Length
                        || !int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
                        || minutes is < 5 or > 480)
                        throw CliException.Usage("--minutes must be a whole number from 5 through 480.");
                    break;
                case "--command":
                    if (command is not null || powerShell is not null)
                        throw CliException.Usage("Use only one of --command or --powershell.");
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        throw CliException.Usage("--command requires one non-empty command argument.");
                    command = args[index];
                    break;
                case "--powershell":
                    if (command is not null || powerShell is not null)
                        throw CliException.Usage("Use only one of --command or --powershell.");
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        throw CliException.Usage("--powershell requires a script or '-' for standard input.");
                    powerShell = args[index];
                    break;
                default:
                    throw CliException.Usage($"Unknown option '{Clean(args[index])}' for ssh.");
            }
        }
        return new SshOptions(selector, minutes, command, powerShell);
    }

    private static UpdateOptions ParseUpdate(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            throw CliException.Usage("Usage: opticon update <device> --yes [--json]");
        var confirmed = false;
        var json = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--yes" when !confirmed:
                    confirmed = true;
                    break;
                case "--json" when !json:
                    json = true;
                    break;
                case "--yes":
                case "--json":
                    throw CliException.Usage($"{args[index]} may be specified only once.");
                default:
                    throw CliException.Usage($"Unknown option '{Clean(args[index])}' for update.");
            }
        }
        return new UpdateOptions(args[0], confirmed, json);
    }

    private static bool IsHelp(string value) =>
        value is "help" or "--help" or "-h";

    private static void WriteHelp()
    {
        Console.Out.WriteLine(
            """
            Opticon command-center CLI

            Usage:
              opticon devices [--json]
              opticon status <device> [--json]
              opticon ssh <device> [--minutes 5..480]
              opticon ssh <device> [--minutes 5..480] --command <command>
              opticon ssh <device> [--minutes 5..480] --powershell <script|->
              opticon update <device> --yes [--json]
              opticon version

            Device selectors are exact: device ID, Tailnet device ID, Tailscale IPv4
            address, name, host name, or DNS name. Ambiguous names are rejected; use
            the device ID.

            SSH uses an ephemeral key, pinned target host key, bounded lease, and a
            fail-closed elevated-token check. --powershell encodes the script before
            transport; use '-' to read the script from standard input. Remote command
            output is written directly to this console and ssh.exe's exit code is returned.

            Updates use the guarded Agent/Guardian transaction and require verified
            RustDesk recovery. Legacy one-time maintenance bootstrap remains UI-only.

            Run the CLI as the same Windows user that configured the command center.
            JSON output never includes Agent tokens, passwords, or protected values.
            """);
    }

    private static DeviceSummary ToDeviceSummary(DeviceRecord device) => new(
        device.Id,
        Clean(device.Name),
        Clean(device.HostName),
        Clean(device.DnsName),
        Clean(device.TailscaleIp),
        device.State,
        Clean(device.AgentVersion),
        device.Role,
        device.LastSeen);

    private static DeviceIdentity ToDeviceIdentity(DeviceRecord device) => new(
        device.Id,
        Clean(device.Name),
        Clean(device.HostName),
        Clean(device.TailscaleIp));

    private static LiveStatus ToStatus(DeviceRecord device, DeviceStatusDto status) => new(
        device.Id,
        Clean(device.Name),
        Clean(status.HostName),
        Clean(status.TailscaleIp),
        Clean(status.TailnetDeviceId),
        Clean(status.OperatingSystem),
        Clean(status.Architecture),
        Clean(status.AgentVersion),
        status.UpdateProtocolVersion,
        status.RustDeskRunning,
        status.RustDeskReady,
        status.SshReady,
        status.SshPort,
        status.AdvertisesExitNode,
        status.FreeDiskBytes,
        status.TotalDiskBytes,
        status.StartedAt,
        status.ServerTime,
        status.UpdateStatus is null ? null : ToUpdate(status.UpdateStatus));

    private static SafeUpdateStatus ToUpdate(UpdateStatusDto status) => new(
        status.OperationId,
        status.MaintenanceBootstrap,
        status.Phase,
        Clean(status.CurrentVersion),
        Clean(status.TargetVersion),
        Clean(status.Message),
        status.StartedAt,
        status.UpdatedAt,
        status.CommitDeadline,
        status.RollbackAvailable);

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new string(value
            .Where(Program.IsSafeOutputCharacter)
            .ToArray()).Trim();
        return cleaned.Length <= 512 ? cleaned : cleaned[..512];
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));

    private sealed record SelectorOptions(string Selector, bool Json);
    private sealed record SshOptions(string Selector, int Minutes, string? Command, string? PowerShell);
    private sealed record UpdateOptions(string Selector, bool Confirmed, bool Json);

    private sealed record DevicesEnvelope(
        int SchemaVersion,
        bool Ok,
        string Command,
        IReadOnlyList<DeviceSummary> Devices);

    private sealed record StatusEnvelope(
        int SchemaVersion,
        bool Ok,
        string Command,
        LiveStatus Status);

    private sealed record UpdateEnvelope(
        int SchemaVersion,
        bool Ok,
        string Command,
        string Outcome,
        DeviceIdentity Device,
        string CurrentVersion,
        string? TargetVersion,
        SafeUpdateStatus? Status);

    private sealed record DeviceSummary(
        Guid Id,
        string Name,
        string HostName,
        string DnsName,
        string TailscaleIp,
        DeviceConnectionState State,
        string AgentVersion,
        DeviceRole Role,
        DateTimeOffset? LastSeen);

    private sealed record DeviceIdentity(Guid Id, string Name, string HostName, string TailscaleIp);

    private sealed record LiveStatus(
        Guid Id,
        string Name,
        string HostName,
        string TailscaleIp,
        string TailnetDeviceId,
        string OperatingSystem,
        string Architecture,
        string AgentVersion,
        int UpdateProtocolVersion,
        bool RustDeskRunning,
        bool RustDeskReady,
        bool SshReady,
        int SshPort,
        bool AdvertisesExitNode,
        long FreeDiskBytes,
        long TotalDiskBytes,
        DateTimeOffset StartedAt,
        DateTimeOffset ServerTime,
        SafeUpdateStatus? Update);

    private sealed record SafeUpdateStatus(
        Guid OperationId,
        bool MaintenanceBootstrap,
        UpdatePhase Phase,
        string CurrentVersion,
        string TargetVersion,
        string Message,
        DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CommitDeadline,
        bool RollbackAvailable);
}

internal sealed class CliException : Exception
{
    public CliException(string code, string message, int exitCode) : base(message)
    {
        Code = code;
        ExitCode = exitCode;
    }

    public string Code { get; }
    public int ExitCode { get; }

    public static CliException Usage(string message) => new("invalid_arguments", message, 2);
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
