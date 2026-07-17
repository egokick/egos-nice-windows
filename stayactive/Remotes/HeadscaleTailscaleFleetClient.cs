using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace StayActive.Remotes;

/// <summary>
/// Result of the one, read-only local Tailscale command this integration permits.
/// No command output is persisted by this component.
/// </summary>
internal sealed record TailscaleStatusProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Deliberately narrow process boundary for the local Tailscale CLI. Implementations
/// receive a fully constructed <see cref="ProcessStartInfo"/> rather than arbitrary
/// command strings, so the caller can use <see cref="ProcessStartInfo.ArgumentList"/>.
/// </summary>
internal interface ITailscaleStatusProcessRunner
{
    Task<TailscaleStatusProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}

internal interface ITailscaleExecutableLocator
{
    string? FindInstalledExecutable();
}

/// <summary>
/// Locates only the normal machine-wide Tailscale installation. It intentionally
/// does not look on PATH or accept a user-controlled executable path.
/// </summary>
internal sealed class SystemTailscaleExecutableLocator : ITailscaleExecutableLocator
{
    public string? FindInstalledExecutable()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Select(directory => Path.Combine(directory, "Tailscale", "tailscale.exe"))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed class SystemTailscaleStatusProcessRunner : ITailscaleStatusProcessRunner
{
    public async Task<TailscaleStatusProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("The Tailscale status command must not use a shell.");
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
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new TailscaleStatusProcessResult(process.ExitCode, standardOutput, standardError);
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
            // Cancellation is best effort. The caller still receives cancellation.
        }
    }
}

/// <summary>
/// Reads the local client's status for a user-configured, self-hosted Headscale
/// endpoint. It invokes exactly <c>tailscale status --json</c>, filters the result
/// to peers explicitly tagged <c>tag:stayactive</c>, and does not perform a network
/// request, enrollment, route change, or remote action.
/// </summary>
internal sealed class HeadscaleTailscaleFleetClient : IRemoteFleetClient
{
    internal const string RequiredFleetTag = "tag:stayactive";

    private static readonly TimeSpan DefaultStatusTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly ITailscaleStatusProcessRunner _runner;
    private readonly ITailscaleExecutableLocator _executableLocator;
    private readonly TimeSpan _statusTimeout;
    private readonly object _snapshotLock = new();

    private RemoteFleetSnapshot _snapshot = RemoteFleetSnapshot.NotConfigured;

    public HeadscaleTailscaleFleetClient(Func<RemoteClientPreferences> getPreferences)
        : this(
            getPreferences,
            new SystemTailscaleStatusProcessRunner(),
            new SystemTailscaleExecutableLocator(),
            DefaultStatusTimeout)
    {
    }

    internal HeadscaleTailscaleFleetClient(
        Func<RemoteClientPreferences> getPreferences,
        ITailscaleStatusProcessRunner runner,
        ITailscaleExecutableLocator executableLocator,
        TimeSpan? statusTimeout = null)
    {
        _getPreferences = getPreferences ?? throw new ArgumentNullException(nameof(getPreferences));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
        _statusTimeout = statusTimeout ?? DefaultStatusTimeout;

        if (_statusTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(statusTimeout));
        }
    }

    public RemoteFleetSnapshot GetCachedSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preferences = _getPreferences();
        if (!TryGetControlPlane(preferences, out var controlPlane))
        {
            SetSnapshot(CreateNotConfiguredSnapshot());
            return;
        }

        var controlPlaneDisplayName = GetControlPlaneDisplayName(controlPlane);
        var executable = _executableLocator.FindInstalledExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client was not found. Install it and enroll it with this Headscale control plane."));
            return;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_statusTimeout);

        TailscaleStatusProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                CreateStatusStartInfo(executable),
                timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "Timed out while reading the local Tailscale status."));
            return;
        }
        catch (FileNotFoundException)
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client was not found. Install it and enroll it with this Headscale control plane."));
            return;
        }
        catch (DirectoryNotFoundException)
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client was not found. Install it and enroll it with this Headscale control plane."));
            return;
        }
        catch (Win32Exception)
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client could not be started."));
            return;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Do not surface command output or exception details in the UI. They can
            // include hostnames and diagnostic material that does not belong in a tray.
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale status could not be read."));
            return;
        }

        if (result.ExitCode != 0)
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client is not connected to the configured Headscale control plane."));
            return;
        }

        if (!HeadscaleTailscaleStatusParser.TryParse(
                result.StandardOutput,
                controlPlaneDisplayName,
                out var snapshot))
        {
            SetSnapshot(CreateDisconnectedSnapshot(
                controlPlaneDisplayName,
                "The local Tailscale client returned an unreadable status response."));
            return;
        }

        SetSnapshot(snapshot);
    }

    public void Dispose()
    {
    }

    private static ProcessStartInfo CreateStatusStartInfo(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Do not use ProcessStartInfo.Arguments: every argument remains a literal
        // token and no shell is involved.
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");
        return startInfo;
    }

    private static bool TryGetControlPlane(RemoteClientPreferences? preferences, out Uri controlPlane)
    {
        controlPlane = null!;
        if (preferences is null
            || !RemoteClientPreferences.IsSelfHostedControlPlane(preferences.ControlPlaneUrl)
            || !Uri.TryCreate(preferences.ControlPlaneUrl, UriKind.Absolute, out var parsedControlPlane))
        {
            return false;
        }

        controlPlane = parsedControlPlane;
        return true;
    }

    private static string GetControlPlaneDisplayName(Uri controlPlane)
    {
        return "Headscale " + controlPlane.Authority;
    }

    private static RemoteFleetSnapshot CreateNotConfiguredSnapshot()
    {
        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.NotConfigured,
            "Self-hosted remotes",
            "A self-hosted HTTPS Headscale control-plane URL is required.",
            Array.Empty<RemoteDevice>(),
            null,
            DateTimeOffset.UtcNow);
    }

    private static RemoteFleetSnapshot CreateDisconnectedSnapshot(string controlPlaneDisplayName, string statusMessage)
    {
        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Disconnected,
            controlPlaneDisplayName,
            statusMessage,
            Array.Empty<RemoteDevice>(),
            null,
            DateTimeOffset.UtcNow);
    }

    private void SetSnapshot(RemoteFleetSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }
    }
}

internal static class HeadscaleTailscaleStatusParser
{
    public static bool TryParse(
        string? statusJson,
        string controlPlaneDisplayName,
        out RemoteFleetSnapshot snapshot)
    {
        snapshot = null!;
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var backendState = GetString(root, "BackendState");
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = new RemoteFleetSnapshot(
                    RemoteFleetConnectionState.Disconnected,
                    controlPlaneDisplayName,
                    "The local Tailscale client is not connected to the configured Headscale control plane.",
                    Array.Empty<RemoteDevice>(),
                    null,
                    DateTimeOffset.UtcNow);
                return true;
            }

            var users = GetUsers(root);
            var peers = GetTaggedPeers(root, users);
            var activeExitNodeId = GetActiveExitNodeId(root, peers);
            var hasUnmanagedActiveExitNode = activeExitNodeId is null && HasActiveExitNode(root);
            var statusMessage = hasUnmanagedActiveExitNode
                ? $"Connected. Only peers explicitly tagged {HeadscaleTailscaleFleetClient.RequiredFleetTag} are shown; the active exit node is outside that fleet."
                : $"Connected. Only peers explicitly tagged {HeadscaleTailscaleFleetClient.RequiredFleetTag} are shown.";

            snapshot = new RemoteFleetSnapshot(
                RemoteFleetConnectionState.Connected,
                controlPlaneDisplayName,
                statusMessage,
                peers.Select(peer => peer.Device).ToArray(),
                activeExitNodeId,
                DateTimeOffset.UtcNow,
                hasUnmanagedActiveExitNode);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> GetUsers(JsonElement root)
    {
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryGetProperty(root, "User", out var userMap) || userMap.ValueKind != JsonValueKind.Object)
        {
            return users;
        }

        foreach (var user in userMap.EnumerateObject())
        {
            if (user.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var displayName = FirstNonEmpty(
                GetString(user.Value, "DisplayName"),
                GetString(user.Value, "LoginName"),
                user.Name);
            users[user.Name] = displayName;
        }

        return users;
    }

    private static IReadOnlyList<ParsedPeer> GetTaggedPeers(
        JsonElement root,
        IReadOnlyDictionary<string, string> users)
    {
        if (!TryGetProperty(root, "Peer", out var peerMap) || peerMap.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ParsedPeer>();
        }

        var peers = new List<ParsedPeer>();
        foreach (var peerEntry in peerMap.EnumerateObject())
        {
            var peer = peerEntry.Value;
            if (peer.ValueKind != JsonValueKind.Object || !HasRequiredFleetTag(peer))
            {
                continue;
            }

            var id = FirstNonEmpty(GetString(peer, "ID"), peerEntry.Name);
            var userId = GetStringOrNumber(peer, "UserID");
            var owner = !string.IsNullOrWhiteSpace(userId) && users.TryGetValue(userId, out var userDisplayName)
                ? userDisplayName
                : "Headscale user";
            var name = FirstNonEmpty(
                GetString(peer, "HostName"),
                TrimDnsSuffix(GetString(peer, "DNSName")),
                id);
            var tailscaleIps = GetStringArray(peer, "TailscaleIPs");

            var remoteDevice = new RemoteDevice(
                id,
                name,
                owner,
                // Never infer or expose physical location from an IP address. That
                // must come from the separately consented RemoteHub inventory.
                Location: null,
                IsOnline: GetBoolean(peer, "Online"),
                // "Verified" here means Headscale supplied a peer with the explicit
                // opt-in fleet tag. It is not remote-action authorization.
                IsVerified: true,
                LastSeenAt: GetDateTimeOffset(peer, "LastSeen"),
                Capabilities: IsExitNodeCandidate(peer) ? RemoteCapability.ExitNode : RemoteCapability.None,
                TailnetIp: tailscaleIps.FirstOrDefault());

            peers.Add(new ParsedPeer(remoteDevice, tailscaleIps));
        }

        return peers;
    }

    private static string? GetActiveExitNodeId(JsonElement root, IReadOnlyList<ParsedPeer> peers)
    {
        if (!TryGetProperty(root, "ExitNodeStatus", out var exitNode)
            || exitNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(exitNode, "ID");
        if (!string.IsNullOrWhiteSpace(id)
            && peers.Any(peer => string.Equals(peer.Device.Id, id, StringComparison.Ordinal)))
        {
            return id;
        }

        var exitNodeIps = GetStringArray(exitNode, "TailscaleIPs");
        return peers.FirstOrDefault(peer => peer.TailscaleIps.Intersect(exitNodeIps, StringComparer.Ordinal).Any())?.Device.Id;
    }

    private static bool HasActiveExitNode(JsonElement root)
    {
        return TryGetProperty(root, "ExitNodeStatus", out var exitNode)
            && exitNode.ValueKind == JsonValueKind.Object;
    }

    private static bool HasRequiredFleetTag(JsonElement peer)
    {
        return GetStringArray(peer, "Tags")
            .Any(tag => string.Equals(
                tag,
                HeadscaleTailscaleFleetClient.RequiredFleetTag,
                StringComparison.Ordinal));
    }

    private static bool IsExitNodeCandidate(JsonElement peer)
    {
        return GetBoolean(peer, "ExitNodeOption") || GetBoolean(peer, "ExitNode");
    }

    private static string[] GetStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dateTime)
            ? dateTime
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetStringOrNumber(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? "Unknown device";
    }

    private static string? TrimDnsSuffix(string? dnsName)
    {
        return string.IsNullOrWhiteSpace(dnsName)
            ? null
            : dnsName.TrimEnd('.');
    }

    private sealed record ParsedPeer(RemoteDevice Device, IReadOnlyList<string> TailscaleIps);
}
