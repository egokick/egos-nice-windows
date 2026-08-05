using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Setup;

public sealed record InstallProgress(int Percent, string Message);

public sealed class ExistingTailscaleSessionException : InvalidOperationException
{
    public ExistingTailscaleSessionException(string message) : base(message) { }
}

public sealed class InstallCoordinator
{
    private readonly InvitePayload _invite;
    private readonly string _bundleDirectory;
    private readonly IProgress<InstallProgress> _progress;
    private readonly HttpClient _http;
    private readonly bool _allowTailscaleReauthentication;
    private readonly InteractiveUserProfile _userProfile;

    public InstallCoordinator(InvitePayload invite, string bundleDirectory, IProgress<InstallProgress> progress, bool allowTailscaleReauthentication = false)
    {
        _invite = invite;
        _bundleDirectory = bundleDirectory;
        _progress = progress;
        _allowTailscaleReauthentication = allowTailscaleReauthentication;
        _userProfile = InteractiveUserProfile.Resolve();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Taildesk-Setup/1.0");
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        EnsureInviteIsValid();
        var canResumeExistingSession = false;
        if (File.Exists(AppPaths.AgentConfigFile))
        {
            var installedState = await new JsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).LoadAsync(cancellationToken);
            if (installedState.CompletedInviteId == _invite.InviteId)
            {
                _progress.Report(new InstallProgress(100, "This invitation is already installed and enrolled."));
                return;
            }
            canResumeExistingSession = installedState.PendingInviteId == _invite.InviteId;
        }
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Taildesk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            _progress.Report(new InstallProgress(4, "Checking the invitation and local payload…"));
            var agentPayload = Path.Combine(_bundleDirectory, "Payload", "Agent");
            var agentExecutable = Path.Combine(agentPayload, "Taildesk.Agent.exe");
            if (!File.Exists(agentExecutable))
            {
                throw new FileNotFoundException("The invitation bundle is incomplete (Payload\\Agent is missing).");
            }
            await InvitationSigning.VerifyAuthenticodeAsync(agentExecutable, cancellationToken);

            await EnsureTailscaleAsync(tempDirectory, cancellationToken);
            _progress.Report(new InstallProgress(28, "Joining the private Opticon network…"));
            var tailscale = FindTailscale();
            var existing = await TryReadTailscaleStatusAsync(tailscale, cancellationToken);
            LocalTailscaleSnapshot snapshot;
            if (existing is { Online: true }
                && ExistingSessionHasExpectedRole(existing)
                && (canResumeExistingSession || ExistingSessionHasExpectedDeviceName(existing)))
            {
                _progress.Report(new InstallProgress(31, "This machine already has the expected Opticon network role."));
                snapshot = existing;
            }
            else
            {
                if (existing is { Online: true } && !string.IsNullOrWhiteSpace(existing.Ip))
                {
                    if (!_allowTailscaleReauthentication)
                    {
                        throw new ExistingTailscaleSessionException("This machine is already connected to Tailscale. To consume this single-use invitation and enforce its exact tailnet and role, Opticon must reauthenticate it with the new invitation.");
                    }
                }

                var up = await ProcessRunner.RunAsync(tailscale,
                    TailscaleCommandLine.BuildEnrollmentArguments(
                        _invite.HeadscaleLoginUrl, _invite.TailscaleAuthKey, SafeHostName(_invite.DeviceName)),
                    TimeSpan.FromMinutes(2), cancellationToken);
                EnsureSuccess(up, "Tailscale could not join the tailnet");
                snapshot = await WaitForExpectedTailscaleSessionAsync(tailscale, cancellationToken);
            }
            if (!ExistingSessionHasExpectedRole(snapshot))
            {
                throw new InvalidOperationException("Tailscale joined, but the resulting tailnet or device tags do not exactly match this invitation.");
            }
            if (string.IsNullOrWhiteSpace(snapshot.Ip))
            {
                throw new InvalidOperationException("Tailscale joined, but did not assign an address.");
            }

            if (_invite.AdvertiseExitNode)
            {
                _progress.Report(new InstallProgress(38, "Enabling this machine as an exit node…"));
                var advertise = await ProcessRunner.RunAsync(tailscale, ["set", "--advertise-exit-node"], TimeSpan.FromSeconds(30), cancellationToken);
                EnsureSuccess(advertise, "Tailscale could not advertise the exit node");
            }

            var rustDesk = await EnsureRustDeskAsync(tempDirectory, cancellationToken);
            await ConfigureRustDeskAsync(rustDesk, cancellationToken);
            if (!await WaitForListeningPortAsync(21118, TimeSpan.FromSeconds(90), cancellationToken))
            {
                _progress.Report(new InstallProgress(66, "Repairing the RustDesk private listener?"));
                await ConfigureRustDeskAsync(rustDesk, cancellationToken);
                if (!await WaitForListeningPortAsync(21118, TimeSpan.FromSeconds(90), cancellationToken))
                    throw new InvalidOperationException("RustDesk did not open its private direct-access listener on TCP 21118 after an automatic repair.");
            }
            await InstallAgentAsync(agentPayload, snapshot.Ip, cancellationToken);
            await ConfigureFirewallAsync(snapshot.Ip, rustDesk, cancellationToken);

            await InstallControllerPayloadAsync(_invite.Role == DeviceRole.ControllerAndManaged, cancellationToken);

            _progress.Report(new InstallProgress(94, "Starting the Opticon agent…"));
            var start = await ProcessRunner.RunAsync("schtasks.exe", ["/Run", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(20), cancellationToken);
            if (!await WaitForListeningPortAsync(45831, TimeSpan.FromSeconds(30), cancellationToken))
                throw new InvalidOperationException("The Opticon agent task started but did not open its private API listener on TCP 45831.");
            EnsureSuccess(start, "The Opticon background agent task could not be started");
            _progress.Report(new InstallProgress(96, "Waiting for the command center to confirm enrollment…"));
            await WaitForEnrollmentAsync(cancellationToken);
            _progress.Report(new InstallProgress(100, "Connected. This machine is ready."));
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); } catch { }
        }
    }

    private async Task EnsureTailscaleAsync(string tempDirectory, CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        var artifact = DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed) && await InstalledTailscaleMatchesAsync(installed, artifact.Version, cancellationToken))
        {
            _progress.Report(new InstallProgress(12, $"Pinned Tailscale {artifact.Version} is already installed."));
            return;
        }

        _progress.Report(new InstallProgress(10, $"Downloading pinned Tailscale {artifact.Version}…"));
        var installer = Path.Combine(tempDirectory, artifact.FileName);
        await DownloadVerifiedAsync(artifact, installer, cancellationToken);
        _progress.Report(new InstallProgress(18, "Installing Tailscale…"));
        var result = await ProcessRunner.RunAsync("msiexec.exe", ["/i", installer, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(result, "Tailscale installation failed");
        if (!File.Exists(installed) || !await InstalledTailscaleMatchesAsync(installed, artifact.Version, cancellationToken))
            throw new InvalidDataException($"Tailscale installed, but its version is not the pinned {artifact.Version}.");
    }
    private async Task<string> EnsureRustDeskAsync(string tempDirectory, CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        var artifact = DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed) && FileVersionInfo.GetVersionInfo(installed).ProductVersion?.StartsWith(artifact.Version, StringComparison.Ordinal) == true)
        {
            _progress.Report(new InstallProgress(47, $"Pinned RustDesk {artifact.Version} is already installed."));
            return installed;
        }

        var installer = Path.Combine(tempDirectory, artifact.FileName);
        _progress.Report(new InstallProgress(49, $"Downloading pinned RustDesk {artifact.Version}…"));
        await DownloadVerifiedAsync(artifact, installer, cancellationToken);
        _progress.Report(new InstallProgress(56, "Installing RustDesk…"));
        var install = await ProcessRunner.RunAsync("msiexec.exe", ["/i", installer, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(install, "RustDesk installation failed");

        for (var attempt = 0; attempt < 20 && !File.Exists(installed); attempt++)
            await Task.Delay(500, cancellationToken);
        if (!File.Exists(installed) || FileVersionInfo.GetVersionInfo(installed).ProductVersion?.StartsWith(artifact.Version, StringComparison.Ordinal) != true)
            throw new InvalidDataException($"RustDesk installed, but its version is not the pinned {artifact.Version}.");
        return installed;
    }
    private async Task ConfigureRustDeskAsync(string rustDesk, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(61, "Securing RustDesk for direct Tailscale access…"));
        var service = await ProcessRunner.RunAsync("sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!service.Succeeded)
        {
            var installService = await ProcessRunner.RunAsync(rustDesk, ["--install-service"], TimeSpan.FromSeconds(20), cancellationToken);
            EnsureSuccess(installService, "RustDesk service installation failed");
        }
        var automatic = await ProcessRunner.RunAsync("sc.exe", ["config", "RustDesk", "start=", "auto"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(automatic, "RustDesk could not be configured for automatic startup");
        var recovery = await ProcessRunner.RunAsync("sc.exe",
            ["failure", "RustDesk", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
            TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(recovery, "RustDesk service recovery could not be configured");
        var failureFlag = await ProcessRunner.RunAsync("sc.exe", ["failureflag", "RustDesk", "1"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(failureFlag, "RustDesk non-crash failure recovery could not be configured");


        var password = await ProcessRunner.RunAsync(rustDesk, ["--password", _invite.RustDeskPassword], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(password, "RustDesk password provisioning failed");

        _ = await ProcessRunner.RunAsync("sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        _ = await ProcessRunner.RunAsync("taskkill.exe", ["/F", "/IM", "rustdesk.exe"], TimeSpan.FromSeconds(20), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await Task.Delay(750, cancellationToken);
        HardenRustDeskConfigFiles();
        var restart = await ProcessRunner.RunAsync("sc.exe", ["start", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(restart, "The private RustDesk service could not be restarted");

        foreach (var shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "RustDesk Tray.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "RustDesk.lnk")
                 })
        {
            if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        var rustDeskPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "RustDesk");
        if (Directory.Exists(rustDeskPrograms)) Directory.Delete(rustDeskPrograms, true);
    }

    private async Task InstallAgentAsync(string source, string tailscaleIp, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(70, "Installing the Opticon background agent…"));
        var destination = Path.Combine(AppPaths.InstallDirectory, "Agent");
        _ = await ProcessRunner.RunAsync("schtasks.exe", ["/End", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(15), cancellationToken);
        await Task.Delay(750, cancellationToken);
        CopyDirectory(source, destination);

        var roots = BuildSharedRoots(_invite.AllowedRoots);
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("None of the folders selected in this invitation exists in the signed-in user's profile.");
        }
        var config = new AgentConfig
        {
            DeviceName = _invite.DeviceName,
            Role = _invite.Role,
            BindAddress = tailscaleIp,
            AgentTokenHash = SecurityHelpers.HashToken(_invite.AgentToken),
            MediaSigningKeyProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
            CoordinatorUrl = _invite.CoordinatorUrl,
            PendingInviteId = _invite.InviteId,
            PendingInviteSecretProtected = SecretProtector.Protect(_invite.InviteSecret, SecretScope.LocalMachine),
            AdvertiseExitNode = _invite.AdvertiseExitNode,
            SharedRoots = roots,
            ControllerShortcutPaths =
            [
                Path.Combine(_userProfile.Desktop, "Opticon.lnk"),
                Path.Combine(_userProfile.Startup, "Opticon.lnk"),
                Path.Combine(_userProfile.Programs, "Opticon.lnk")
            ]
        };
        await new JsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).SaveAsync(config, cancellationToken);

        var protectDirectory = await ProcessRunner.RunAsync("icacls.exe",
            [AppPaths.AgentDataDirectory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(protectDirectory, "Could not secure the agent configuration directory");

        var agentExe = Path.Combine(destination, "Taildesk.Agent.exe");
        var taskCommand = $"\"{agentExe}\"";
        var task = await ProcessRunner.RunAsync("schtasks.exe",
            ["/Create", "/TN", "Taildesk Agent", "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(task, "Could not create the Opticon background-agent startup task");

        const string taskSettings = "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0); Set-ScheduledTask -TaskName 'Taildesk Agent' -Settings $s | Out-Null";
        var settings = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", taskSettings],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(settings, "Could not apply Opticon background-agent recovery settings");
    }

    private async Task ConfigureFirewallAsync(string tailscaleIp, string rustDesk, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(80, "Restricting inbound access to the Tailscale interface…"));
        var agent = Path.Combine(AppPaths.InstallDirectory, "Agent", "Taildesk.Agent.exe");
        foreach (var rule in new[] { "Taildesk Agent (Tailscale only)", "RustDesk Direct (Tailscale only)", "RustDesk External IPv4 Block", "RustDesk External IPv6 Block" })
        {
            _ = await ProcessRunner.RunAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={rule}"], TimeSpan.FromSeconds(20), cancellationToken);
        }
        _ = await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "delete", "rule", "name=all", "dir=in", $"program={rustDesk}"],
            TimeSpan.FromSeconds(20), cancellationToken);

        var agentRule = await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=Taildesk Agent (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=45831", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={agent}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(agentRule, "Could not create the Opticon agent firewall rule");

        var rustDeskRule = await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk Direct (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=21118", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskRule, "Could not create the RustDesk firewall rule");

        var rustDeskExternalV4Block = await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk External IPv4 Block", "dir=out", "action=block", "remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskExternalV4Block, "Could not block RustDesk from non-Tailscale IPv4 destinations");

        var rustDeskExternalV6Block = await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk External IPv6 Block", "dir=out", "action=block", "remoteip=::/1,8000::/1", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskExternalV6Block, "Could not block RustDesk from external IPv6 destinations");
    }

    private async Task InstallControllerPayloadAsync(bool installController, CancellationToken cancellationToken)
    {
        if (!installController)
        {
            _progress.Report(new InstallProgress(87, "Managed-only role confirmed; controller tools are not installed."));
            return;
        }
        _progress.Report(new InstallProgress(87, "Installing controller tools for this machine…"));
        var source = Path.Combine(_bundleDirectory, "Payload", "Admin");
        var controllerExecutable = Path.Combine(source, "Opticon.exe");
        if (!File.Exists(controllerExecutable))
        {
            throw new FileNotFoundException("This controller invite is missing Payload\\Admin.");
        }
        await InvitationSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);

        var destination = Path.Combine(AppPaths.InstallDirectory, "Admin");
        CopyDirectory(source, destination);
        var bootstrap = new AdminBootstrap
        {
            CoordinatorUrl = _invite.CoordinatorUrl,
            ControllerTokenProtected = SecretProtector.Protect(_invite.ControllerToken, SecretScope.LocalMachine),
            DeviceName = _invite.DeviceName,
            IsMachineProtected = true
        };
        var bootstrapPath = Path.Combine(_userProfile.LocalAppData, "Taildesk", "Admin", "bootstrap.json");
        await new JsonFileStore<AdminBootstrap>(bootstrapPath).SaveAsync(bootstrap, cancellationToken);
        if (installController)
        {
            CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Desktop);
            CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Startup);
            CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Programs);
        }
    }

    private async Task<LocalTailscaleSnapshot> ReadTailscaleStatusAsync(string tailscale, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(result, "Tailscale status was unavailable");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var self = root.GetProperty("Self");
        var ips = self.GetProperty("TailscaleIPs").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        return new LocalTailscaleSnapshot
        {
            DeviceId = self.TryGetProperty("ID", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            DnsName = self.TryGetProperty("DNSName", out var dns) ? (dns.GetString() ?? string.Empty).TrimEnd('.') : string.Empty,
            Ip = ips.FirstOrDefault(ip => ip.Contains('.')) ?? ips.FirstOrDefault() ?? string.Empty,
            Online = true,
            Tailnet = ReadTailnet(root),
            Tags = self.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray()
                : []
        };
    }

    private async Task<LocalTailscaleSnapshot> WaitForExpectedTailscaleSessionAsync(string tailscale, CancellationToken cancellationToken)
    {
        LocalTailscaleSnapshot? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                last = await ReadTailscaleStatusAsync(tailscale, cancellationToken);
                if (!string.IsNullOrWhiteSpace(last.Ip) && ExistingSessionHasExpectedRole(last)) return last;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The Windows service can take a few seconds to publish the new
                // self identity and tags after `tailscale up` returns.
            }
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        }
        return last ?? await ReadTailscaleStatusAsync(tailscale, cancellationToken);
    }

    private async Task<LocalTailscaleSnapshot?> TryReadTailscaleStatusAsync(string tailscale, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var state) ? state.GetString() ?? string.Empty : string.Empty;
            if (!backendState.Equals("Running", StringComparison.OrdinalIgnoreCase)) return null;
            var self = root.GetProperty("Self");
            var ips = self.GetProperty("TailscaleIPs").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
            return new LocalTailscaleSnapshot
            {
                DeviceId = self.TryGetProperty("ID", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                DnsName = self.TryGetProperty("DNSName", out var dns) ? (dns.GetString() ?? string.Empty).TrimEnd('.')
                    : self.TryGetProperty("HostName", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                Ip = ips.FirstOrDefault(ip => ip.Contains('.')) ?? string.Empty,
                Online = true,
                Tailnet = ReadTailnet(root),
                Tags = self.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray()
                    : []
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForEnrollmentAsync(CancellationToken cancellationToken)
    {
        var store = new JsonFileStore<AgentConfig>(AppPaths.AgentConfigFile);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var state = await store.LoadAsync(cancellationToken);
            if (!state.PendingInviteId.HasValue) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("The agent is installed and will keep retrying, but the command center did not confirm enrollment within two minutes. Make sure the Opticon command center is running and the private-network policy is active.");
    }

    private bool ExistingSessionHasExpectedRole(LocalTailscaleSnapshot snapshot)
    {
        var expected = _invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed";
        var opposite = _invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-managed" : "tag:taildesk-controller";
        var hasExitTag = snapshot.Tags.Contains("tag:taildesk-exit", StringComparer.OrdinalIgnoreCase);
        return snapshot.Tags.Contains(expected, StringComparer.OrdinalIgnoreCase)
               && !snapshot.Tags.Contains(opposite, StringComparer.OrdinalIgnoreCase)
               && hasExitTag == _invite.AdvertiseExitNode
               && snapshot.Tailnet.Equals(_invite.ExpectedTailnet, StringComparison.OrdinalIgnoreCase);
    }

    private bool ExistingSessionHasExpectedDeviceName(LocalTailscaleSnapshot snapshot)
    {
        var dnsLabel = snapshot.DnsName.Split('.', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return dnsLabel.Equals(SafeHostName(_invite.DeviceName), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTailnet(JsonElement root)
    {
        if (root.TryGetProperty("CurrentTailnet", out var current) && current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? string.Empty;
        }
        return root.TryGetProperty("MagicDNSSuffix", out var suffix) && suffix.ValueKind == JsonValueKind.String
            ? suffix.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task DownloadVerifiedAsync(DependencyArtifact artifact, string destination, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var url in new[] { artifact.PrimaryUrl, artifact.FallbackUrl })
        {
            try
            {
                if (File.Exists(destination)) File.Delete(destination);
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long declared && declared != artifact.Size)
                    throw new InvalidDataException($"Content length {declared} did not match pinned size {artifact.Size}.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                    await source.CopyToAsync(output, 1024 * 1024, cancellationToken);
                var info = new FileInfo(destination);
                if (info.Length != artifact.Size) throw new InvalidDataException($"Downloaded size {info.Length} did not match pinned size {artifact.Size}.");
                await using var verify = File.OpenRead(destination);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
                if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 did not match the pinned artifact.");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{new Uri(url).Host}: {exception.Message}");
            }
        }
        try { if (File.Exists(destination)) File.Delete(destination); } catch { }
        throw new InvalidDataException($"Neither verified source supplied {artifact.Product} {artifact.Version}: {string.Join(" | ", errors)}");
    }

    private static async Task<bool> WaitForListeningPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        do
        {
            try
            {
                if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port)) return true;
            }
            catch { }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static async Task<bool> InstalledTailscaleMatchesAsync(string executable, string version, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(executable, ["version"], TimeSpan.FromSeconds(20), cancellationToken);
        return result.Succeeded && result.StandardOutput.TrimStart().StartsWith(version, StringComparison.Ordinal);
    }
    private void HardenRustDeskConfigFiles()
    {
        var roamingRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_userProfile.ProfilePath, "AppData", "Roaming"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles", "LocalService", "AppData", "Roaming"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles", "NetworkService", "AppData", "Roaming"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config", "systemprofile", "AppData", "Roaming")
        };
        foreach (var root in roamingRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var configDirectory = Path.Combine(root, "RustDesk", "config");
            Directory.CreateDirectory(configDirectory);
            var files = Directory.EnumerateFiles(configDirectory, "RustDesk*.toml").ToList();
            var primary = Path.Combine(configDirectory, "RustDesk2.toml");
            if (!files.Contains(primary, StringComparer.OrdinalIgnoreCase)) files.Add(primary);
            foreach (var file in files)
            {
                var hardened = RustDeskConfiguration.HardenManagedHost(File.Exists(file) ? File.ReadAllText(file) : string.Empty);
                File.WriteAllText(file, hardened, new System.Text.UTF8Encoding(false));
                if (!RustDeskConfiguration.IsManagedHostHardened(File.ReadAllText(file)))
                    throw new InvalidDataException($"RustDesk configuration verification failed for {file}.");
            }
        }
    }
    private Dictionary<string, string> BuildSharedRoots(IEnumerable<string> requested)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = _userProfile.Desktop,
            ["Documents"] = _userProfile.Documents,
            ["Downloads"] = _userProfile.Downloads,
            ["Pictures"] = _userProfile.Pictures,
            ["Videos"] = _userProfile.Videos
        };
        return requested.Where(known.ContainsKey)
            .Select(name => new KeyValuePair<string, string>(name, known[name]))
            .Where(pair => Directory.Exists(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
        }
    }

    private static string FindTailscale()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Tailscale was installed but tailscale.exe was not found.");
    }

    private static string SafeHostName(string value)
    {
        var safe = new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? Environment.MachineName.ToLowerInvariant() : safe[..Math.Min(safe.Length, 63)];
    }

    private void EnsureInviteIsValid()
    {
        var supportedRoots = new HashSet<string>(["Desktop", "Documents", "Downloads", "Pictures", "Videos"], StringComparer.OrdinalIgnoreCase);
        if (!InvitationPolicy.IsSupportedPayloadSchema(_invite.SchemaVersion) || _invite.InviteId == Guid.Empty || string.IsNullOrWhiteSpace(_invite.TailscaleAuthKey)
            || !Uri.TryCreate(_invite.HeadscaleLoginUrl, UriKind.Absolute, out var loginUri) || loginUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(_invite.ExpectedTailnet)
            || string.IsNullOrWhiteSpace(_invite.AgentToken) || string.IsNullOrWhiteSpace(_invite.InviteSecret)
            || _invite.AllowedRoots is null || _invite.AllowedRoots.Length == 0
            || _invite.AllowedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _invite.AllowedRoots.Length
            || _invite.AllowedRoots.Any(root => !supportedRoots.Contains(root)))
        {
            throw new InvalidDataException("This is not a valid Opticon invitation.");
        }
        if (_invite.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("This Opticon invitation has expired. Create a new one on the command center.");
        }
    }

    private void EnsureSuccess(ProcessResult result, string message)
    {
        if (!result.Succeeded && result.ExitCode != 3010)
        {
            var detail = $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}";
            foreach (var secret in new[] { _invite.TailscaleAuthKey, _invite.AgentToken, _invite.InviteSecret, _invite.ControllerToken, _invite.RustDeskPassword })
            {
                if (!string.IsNullOrEmpty(secret)) detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
            throw new InvalidOperationException($"{message}: {detail}".Trim());
        }
    }

    private static void CreateShortcut(string target, string name, string directory)
    {
        Directory.CreateDirectory(directory);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(directory, name + ".lnk"));
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.Description = "Opticon command center";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private sealed class LocalTailscaleSnapshot
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DnsName { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public bool Online { get; init; }
        public string Tailnet { get; init; } = string.Empty;
        public string[] Tags { get; init; } = [];
    }
}
