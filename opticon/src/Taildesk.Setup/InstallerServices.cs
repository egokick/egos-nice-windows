using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Setup;

public sealed record InstallProgress(int Percent, string Message);

public sealed class ExistingTailscaleSessionException : InvalidOperationException
{
    public ExistingTailscaleSessionException(string message) : base(message) { }
}

public sealed class InstallCoordinator
{
    private const string ControllerOwnershipMarkerName = ".opticon-controller-owned";
    private const string ControllerOwnershipMarkerValue = "Opticon command-center controller payload v1";
    private const string ControllerReadyMarkerName = ".opticon-controller-ready";
    private const string ControllerReadyMarkerValue = "Opticon command-center controller payload ready v1";
    private const string ControllerInstallDirectoryValueName = "InstallDirectory";
    private const string ControllerInstallLockFileName = ".controller-install.lock";

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
            var guardianPayload = Path.Combine(_bundleDirectory, "Payload", "UpdateGuardian");
            var guardianExecutable = Path.Combine(guardianPayload, "Taildesk.UpdateGuardian.exe");
            if (!File.Exists(guardianExecutable))
            {
                throw new FileNotFoundException("The invitation bundle is incomplete (Payload\\UpdateGuardian is missing).");
            }
            await InvitationSigning.VerifyAuthenticodeAsync(guardianExecutable, cancellationToken);
            if (_invite.Role == DeviceRole.ControllerAndManaged)
            {
                var controllerPayload = Path.Combine(_bundleDirectory, "Payload", "Admin");
                var controllerExecutable = Path.Combine(controllerPayload, "Opticon.exe");
                var cliExecutable = Path.Combine(controllerPayload, "Cli", "opticon.exe");
                if (!File.Exists(controllerExecutable) || !File.Exists(cliExecutable))
                    throw new FileNotFoundException("This controller invite is missing its signed UI or CLI payload.");
                await InvitationSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);
                await InvitationSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);
                foreach (var executable in Directory.EnumerateFiles(
                             controllerPayload, "*.exe", SearchOption.AllDirectories))
                    await InvitationSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
                var installedController = Path.Combine(AppPaths.InstallDirectory, "Admin");
                RequireInstalledControllerProcessesClosed(installedController, installedController + ".previous");
            }

            // The signed Agent and stable guardian payloads are trusted before
            // servicing Windows. No Tailscale, RustDesk, enrollment, journal,
            // or installed Opticon state has been changed at this point.
            _progress.Report(new InstallProgress(6, "Checking the Windows OpenSSH recovery component…"));
            await EnsureOpenSshServerCapabilityAsync(cancellationToken);
            if (_invite.Role == DeviceRole.ControllerAndManaged)
                await EnsureOpenSshClientCapabilityAsync(cancellationToken);
            var installedNetworkComponent = await EnsureTailscaleAsync(tempDirectory, cancellationToken);
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

            var rustDeskInstallation = await EnsureRustDeskAsync(tempDirectory, cancellationToken);
            var rustDesk = rustDeskInstallation.Path;
            await ConfigureRustDeskAsync(rustDesk, cancellationToken);
            if (!await WaitForListeningPortAsync(21118, TimeSpan.FromSeconds(90), cancellationToken))
            {
                _progress.Report(new InstallProgress(66, "Repairing the RustDesk private listener?"));
                await ConfigureRustDeskAsync(rustDesk, cancellationToken);
                if (!await WaitForListeningPortAsync(21118, TimeSpan.FromSeconds(90), cancellationToken))
                    throw new InvalidOperationException("RustDesk did not open its private direct-access listener on TCP 21118 after an automatic repair.");
            }
            await InstallAgentAsync(agentPayload, guardianPayload, snapshot.Ip, cancellationToken);
            await ConfigureFirewallAsync(snapshot.Ip, rustDesk, cancellationToken);
            OpticonComponentIntegration.Integrate(_userProfile, installedNetworkComponent, rustDeskInstallation.InstalledByOpticon);

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

    internal static async Task EnsureOpenSshClientCapabilityAsync(CancellationToken cancellationToken)
    {
        var opensshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var ssh = Path.Combine(opensshDirectory, "ssh.exe");
        var sshKeygen = Path.Combine(opensshDirectory, "ssh-keygen.exe");
        if (File.Exists(ssh) && File.Exists(sshKeygen)) return;

        var installed = await InstallOpenSshCapabilityAsync(
            "OpenSSH.Client~~~~0.0.1.0",
            "OpenSSH Client",
            cancellationToken);
        EnsureCapabilityCommandSucceeded(installed, "Windows could not install the OpenSSH Client capability");
        if (!File.Exists(ssh) || !File.Exists(sshKeygen))
            throw new InvalidOperationException(
                "OpenSSH Client installation needs a Windows restart or capability repair before controller setup can continue.");
    }

    internal static async Task EnsureOpenSshServerCapabilityAsync(CancellationToken cancellationToken)    {
        var opensshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var sshd = Path.Combine(opensshDirectory, "sshd.exe");
        var sshKeygen = Path.Combine(opensshDirectory, "ssh-keygen.exe");
        var stateDirectory = Path.Combine(AppPaths.AgentDataDirectory, "SshAccess");
        var journalPath = Path.Combine(stateDirectory, "openssh-setup-journal.json");
        var journalExists = File.Exists(journalPath);

        // Existing OpenSSH belongs to Windows/the operator. Setup does not alter its
        // service, firewall rule, startup type, or configuration.
        if (File.Exists(sshd) && File.Exists(sshKeygen) && !journalExists) return;

        Directory.CreateDirectory(stateDirectory);
        var acl = await ProcessRunner.RunAsync(
            "icacls.exe",
            [stateDirectory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "/grant:r", "*S-1-5-32-544:(OI)(CI)F"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureCapabilityCommandSucceeded(acl, "Setup could not secure the OpenSSH installation journal");

        var phase = journalExists ? await File.ReadAllTextAsync(journalPath, cancellationToken) : string.Empty;
        if (phase.Contains("\"phase\":\"isolated\"", StringComparison.Ordinal)
            && File.Exists(sshd) && File.Exists(sshKeygen))
            return; // The one-time Opticon installation was already contained.

        // The journal precedes DISM so a cancelled/rebooted Setup can safely finish
        // containing only the capability installation that Opticon itself began.
        await WriteSetupJournalAsync(journalPath, "installing", cancellationToken);
        if (!File.Exists(sshd) || !File.Exists(sshKeygen))
        {
            var installed = await InstallOpenSshCapabilityAsync(
                "OpenSSH.Server~~~~0.0.1.0",
                "OpenSSH Server",
                cancellationToken);
            EnsureCapabilityCommandSucceeded(installed, "Windows could not install the OpenSSH Server capability");
        }
        if (!File.Exists(sshd) || !File.Exists(sshKeygen))
            throw new InvalidOperationException(
                "OpenSSH Server installation needs a Windows restart or capability repair before Opticon setup can continue.");

        // Complete the one-time containment without request or UI cancellation. If
        // interrupted, the durable 'installing' journal makes the next Setup retry it.
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var isolated = await ProcessRunner.RunAsync(
            powershell,
            [
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference='Stop'; " +
                "$rule=Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue; " +
                "if($null -ne $rule){$rule | Disable-NetFirewallRule}; " +
                "$service=Get-Service -Name 'sshd' -ErrorAction SilentlyContinue; " +
                "if($null -ne $service){if($service.Status -ne 'Stopped'){Stop-Service -Name 'sshd' -Force}; Set-Service -Name 'sshd' -StartupType Disabled}"
            ],
            TimeSpan.FromSeconds(45),
            CancellationToken.None);
        EnsureCapabilityCommandSucceeded(isolated, "Setup could not contain the OpenSSH service and firewall rule it installed");
        await WriteSetupJournalAsync(journalPath, "isolated", CancellationToken.None);
    }

    private static async Task<ProcessResult> InstallOpenSshCapabilityAsync(
        string capabilityName,
        string displayName,
        CancellationToken cancellationToken)
    {
        const string command = "/Online /Add-Capability /NoRestart";
        var timeout = TimeSpan.FromMinutes(30);
        var dismLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "DISM", "dism.log");
        await SetupDiagnostics.WriteAsync(
            $"Starting Windows capability installation: {displayName}",
            $"Command: dism.exe {command} /CapabilityName:{capabilityName}{Environment.NewLine}" +
            $"Timeout: {timeout.TotalMinutes:0} minutes{Environment.NewLine}" +
            $"DISM log: {dismLog}",
            cancellationToken);

        try
        {
            var result = await ProcessRunner.RunAsync(
                "dism.exe",
                ["/Online", "/Add-Capability", $"/CapabilityName:{capabilityName}", "/NoRestart"],
                timeout,
                cancellationToken);
            await SetupDiagnostics.WriteAsync(
                $"Windows capability installation completed: {displayName}",
                $"Exit code: {result.ExitCode}{Environment.NewLine}" +
                $"Standard output:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
                $"Standard error:{Environment.NewLine}{result.StandardError}",
                CancellationToken.None);
            return result;
        }
        catch (ProcessTimeoutException exception)
        {
            await SetupDiagnostics.WriteAsync(
                $"Windows capability installation timed out: {displayName}",
                $"Timeout: {exception.Timeout.TotalMinutes:0} minutes{Environment.NewLine}" +
                $"Standard output:{Environment.NewLine}{exception.StandardOutput}{Environment.NewLine}" +
                $"Standard error:{Environment.NewLine}{exception.StandardError}",
                CancellationToken.None);
            throw new TimeoutException(
                $"Windows timed out while installing {displayName} after {exception.Timeout.TotalMinutes:0} minutes. " +
                $"Review {SetupDiagnostics.LogPath} and {dismLog}, then ensure Windows Update servicing is available and retry setup.",
                exception);
        }
    }

    private static void EnsureCapabilityCommandSucceeded(ProcessResult result, string message)
    {
        if (result.Succeeded || result.ExitCode == 3010) return;
        var detail = new[] { result.StandardError, result.StandardOutput }
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.Length != 0);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}");
    }

    private static async Task WriteSetupJournalAsync(
        string path,
        string phase,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".new";
        var value = $"{{\"schemaVersion\":1,\"phase\":\"{phase}\",\"updatedAt\":\"{DateTimeOffset.UtcNow:O}\"}}";
        await using (var stream = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, null, true);
        else File.Move(temporary, path);
    }

    private async Task<bool> EnsureTailscaleAsync(string tempDirectory, CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        var artifact = DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed)
            && OpticonComponentIntegration.IsManagedByOpticon("Private Network")
            && await InstalledTailscaleMatchesAsync(installed, artifact.Version, cancellationToken))
        {
            _progress.Report(new InstallProgress(12, $"Pinned Tailscale {artifact.Version} is already managed by Opticon."));
            return true;
        }

        if (File.Exists(installed) || FindInstalledMsiProductCode(["Tailscale"]) is not null)
        {
            await RemoveStandaloneComponentAsync("Tailscale", ["Tailscale"], installed, cancellationToken);
        }

        _progress.Report(new InstallProgress(10, $"Downloading Opticon's private-network component ({artifact.Version})…"));
        var installer = Path.Combine(tempDirectory, artifact.FileName);
        await DownloadVerifiedAsync(artifact, installer, cancellationToken);
        _progress.Report(new InstallProgress(18, "Installing the Opticon private-network component…"));
        var result = await ProcessRunner.RunAsync("msiexec.exe", ["/i", installer, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(result, "Tailscale installation failed");
        if (!File.Exists(installed) || !await InstalledTailscaleMatchesAsync(installed, artifact.Version, cancellationToken))
            throw new InvalidDataException($"Tailscale installed, but its version is not the pinned {artifact.Version}.");
        return true;
    }

    private async Task<ComponentInstallation> EnsureRustDeskAsync(string tempDirectory, CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        var artifact = DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed)
            && OpticonComponentIntegration.IsManagedByOpticon("Remote Access")
            && FileVersionInfo.GetVersionInfo(installed).ProductVersion?.StartsWith(artifact.Version, StringComparison.Ordinal) == true)
        {
            _progress.Report(new InstallProgress(47, $"Pinned RustDesk {artifact.Version} is already managed by Opticon."));
            return new ComponentInstallation(installed, true);
        }

        if (File.Exists(installed) || FindInstalledMsiProductCode(["RustDesk", "RustDesk Remote Desktop"]) is not null)
        {
            await RemoveStandaloneComponentAsync("RustDesk", ["RustDesk", "RustDesk Remote Desktop"], installed, cancellationToken);
        }

        var installer = Path.Combine(tempDirectory, artifact.FileName);
        _progress.Report(new InstallProgress(49, $"Downloading Opticon's remote-access component ({artifact.Version})…"));
        await DownloadVerifiedAsync(artifact, installer, cancellationToken);
        _progress.Report(new InstallProgress(56, "Installing the Opticon remote-access component…"));
        var install = await ProcessRunner.RunAsync("msiexec.exe", ["/i", installer, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(install, "RustDesk installation failed");

        for (var attempt = 0; attempt < 20 && !File.Exists(installed); attempt++)
            await Task.Delay(500, cancellationToken);
        if (!File.Exists(installed) || FileVersionInfo.GetVersionInfo(installed).ProductVersion?.StartsWith(artifact.Version, StringComparison.Ordinal) != true)
            throw new InvalidDataException($"RustDesk installed, but its version is not the pinned {artifact.Version}.");
        return new ComponentInstallation(installed, true);
    }
    private async Task ConfigureRustDeskAsync(string rustDesk, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(61, "Securing RustDesk for direct Tailscale access…"));
        var service = await ProcessRunner.RunAsync("sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!service.Succeeded)
        {
            // RustDesk starts long-lived service/session children which inherit redirected
            // standard handles. Capturing them would wait forever after this command exits.
            var installService = await ProcessRunner.RunAsync(rustDesk, ["--install-service"],
                TimeSpan.FromSeconds(20), cancellationToken, captureOutput: false);
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

    private async Task InstallAgentAsync(
        string source, string guardianSource, string tailscaleIp, CancellationToken cancellationToken)
    {
        await InstallGuardianAsync(guardianSource, cancellationToken);
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
            UpdateHealthTokenProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
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

    private async Task InstallGuardianAsync(string source, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(69, "Installing the fail-safe update guardian..."));
        var sourceExecutable = Path.Combine(source, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(sourceExecutable))
            throw new FileNotFoundException("The signed update guardian payload is missing.", sourceExecutable);
        await InvitationSigning.VerifyAuthenticodeAsync(sourceExecutable, cancellationToken);

        // The guardian is deliberately outside the swappable Agent directory.
        // Never hot-overwrite an installed, signed guardian: releases declare a
        // minimum guardian version and fail closed until explicit stable-guardian
        // maintenance has completed.
        var destination = AppPaths.UpdateGuardianInstallDirectory;
        var installedExecutable = Path.Combine(destination, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(installedExecutable)) CopyDirectory(source, destination);
        await InvitationSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
        await RequireInstalledGuardianWatchdogCompatibilityAsync(source, destination, cancellationToken);

        var taskCommand = $"\"{installedExecutable}\"";
        var task = await ProcessRunner.RunAsync("schtasks.exe",
            ["/Create", "/TN", RemoteAdministrationProtocol.GuardianTaskName, "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(task, "Could not create the fail-safe update-guardian task");

        var watchdogCommand = $"\"{installedExecutable}\" {RemoteAdministrationProtocol.GuardianWatchdogArgument}";
        var watchdog = await ProcessRunner.RunAsync("schtasks.exe",
            ["/Create", "/TN", RemoteAdministrationProtocol.GuardianWatchdogTaskName,
                "/SC", "MINUTE", "/MO", "1", "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", watchdogCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(watchdog, "Could not create the fail-safe update-guardian watchdog task");

        var bootSettings =
            "$boot=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable " +
            "-RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0) " +
            "-MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianTaskName}' -Settings $boot | Out-Null";
        var watchdogSettings =
            "$watchdog=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianWatchdogTaskName}' -Settings $watchdog | Out-Null";
        var guardianTaskSettings = bootSettings + "; " + watchdogSettings;
        var settings = await ProcessRunner.RunAsync(
            "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", guardianTaskSettings],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(settings, "Could not apply fail-safe update-guardian recovery/watchdog settings");
    }

    private static async Task RequireInstalledGuardianWatchdogCompatibilityAsync(
        string sourceDirectory,
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var sourceExecutable = Path.Combine(sourceDirectory, "Taildesk.UpdateGuardian.exe");
        var installedExecutable = Path.Combine(installedDirectory, "Taildesk.UpdateGuardian.exe");
        var sourceVersion = UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(sourceExecutable).ProductVersion ?? string.Empty));
        var installedVersion = UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(installedExecutable).ProductVersion ?? string.Empty));
        var installedFiles = Directory.EnumerateFiles(installedDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(installedDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        if (installedVersion > sourceVersion)
        {
            if (installedFiles.Count == 1
                && installedFiles.ContainsKey("Taildesk.UpdateGuardian.exe"))
                return;
            throw new InvalidOperationException(
                "The newer stable Guardian has companion files this Setup cannot attest. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");
        }
        if (installedVersion < sourceVersion)
            throw new InvalidOperationException(
                "The existing stable Guardian predates this Setup's watchdog contract and was not overwritten. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");

        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != installedFiles.Count
            || sourceFiles.Keys.Except(installedFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException(
                "The existing stable Guardian payload differs from this Setup and was not overwritten. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");

        foreach (var (relative, sourcePath) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installedPath = installedFiles[relative];
            if (new FileInfo(sourcePath).Length != new FileInfo(installedPath).Length)
                throw new InvalidOperationException(
                    $"The existing stable Guardian payload differs at {relative}; attended Guardian maintenance is required.");
            await using var source = File.OpenRead(sourcePath);
            await using var installed = File.OpenRead(installedPath);
            var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
            var installedHash = await SHA256.HashDataAsync(installed, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, installedHash))
                throw new InvalidOperationException(
                    $"The existing stable Guardian payload differs at {relative}; attended Guardian maintenance is required.");
        }
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
        _progress.Report(new InstallProgress(87, "Installing controller tools for this machine..."));
        var source = Path.Combine(_bundleDirectory, "Payload", "Admin");
        var controllerExecutable = Path.Combine(source, "Opticon.exe");
        var cliExecutable = Path.Combine(source, "Cli", "opticon.exe");
        if (!File.Exists(controllerExecutable) || !File.Exists(cliExecutable))
            throw new FileNotFoundException("This controller invite is missing its signed UI or CLI payload.");
        await InvitationSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);
        await InvitationSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);

        var destination = Path.Combine(AppPaths.InstallDirectory, "Admin");
        var backup = destination + ".previous";
        await using var transactionLock = await AcquireControllerInstallLockAsync(cancellationToken);
        RequireInstalledControllerProcessesClosed(destination, backup);
        await RecoverControllerDirectoryTransactionAsync(destination, cancellationToken);

        var bootstrap = new AdminBootstrap
        {
            CoordinatorUrl = _invite.CoordinatorUrl,
            ControllerTokenProtected = SecretProtector.Protect(_invite.ControllerToken, SecretScope.LocalMachine),
            DeviceName = _invite.DeviceName,
            IsMachineProtected = true
        };
        var bootstrapPath = Path.Combine(_userProfile.LocalAppData, "Taildesk", "Admin", "bootstrap.json");
        var configurationSnapshot = CaptureControllerConfiguration(bootstrapPath);
        try
        {
            await InstallControllerDirectoryTransactionalAsync(
                source,
                destination,
                async () =>
                {
                    await new JsonFileStore<AdminBootstrap>(bootstrapPath).SaveAsync(bootstrap, cancellationToken);
                    CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Desktop);
                    CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Startup);
                    CreateShortcut(Path.Combine(destination, "Opticon.exe"), "Opticon", _userProfile.Programs);
                    AddInteractiveUserPathEntry(Path.Combine(destination, "Cli"));
                },
                cancellationToken);
        }
        catch (Exception installError)
        {
            try
            {
                await RestoreControllerConfigurationAsync(configurationSnapshot);
            }
            catch (Exception configurationRollbackError)
            {
                throw new AggregateException(
                    "Controller installation failed and its user configuration could not be fully restored.",
                    installError,
                    configurationRollbackError);
            }
            throw;
        }
    }

    private static async Task<FileStream> AcquireControllerInstallLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.InstallDirectory);
        var path = Path.Combine(AppPaths.InstallDirectory, ControllerInstallLockFileName);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Another Opticon controller installation, UI, or CLI still owns the installation lock.",
                        exception);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The Opticon controller installation lock cannot be opened.", exception);
            }
        }
    }

    private static async Task InstallControllerDirectoryTransactionalAsync(
        string source,
        string destination,
        Func<Task> configureActivatedPayload,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(destination);
        var leaf = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf))
            throw new InvalidOperationException("The controller installation directory is unsafe.");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $"{leaf}.installing-{Guid.NewGuid():N}");
        var backup = destination + ".previous";
        var failed = Path.Combine(parent, $"{leaf}.failed-{Guid.NewGuid():N}");
        RequireSafeInstallSibling(staging, parent, leaf + ".installing-");
        RequireSafeInstallSibling(backup, parent, leaf + ".previous");
        RequireSafeInstallSibling(failed, parent, leaf + ".failed-");

        var previousMoved = false;
        var candidateActivated = false;
        try
        {
            CopyDirectory(source, staging);
            File.Delete(Path.Combine(staging, ControllerReadyMarkerName));
            WriteControllerOwnershipMarker(staging);
            await VerifyControllerDirectoryAsync(staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(destination))
                await RequireOwnedControllerDirectoryAsync(destination, allowLegacyCanonical: true, cancellationToken);
            else if (File.Exists(destination))
                throw new InvalidDataException("The controller installation path is a file.");
            if (Directory.Exists(backup) || File.Exists(backup))
                throw new InvalidOperationException("An unrecovered controller backup is still present; refusing the swap.");

            RequireInstalledControllerProcessesClosed(destination, backup);
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                previousMoved = true;
                RequireInstalledControllerProcessesClosed(destination, backup);
            }

            Directory.Move(staging, destination);
            candidateActivated = true;
            await VerifyControllerDirectoryAsync(destination, CancellationToken.None);
            await configureActivatedPayload();
            // This flushed marker is the durable commit point and is written only
            // after bootstrap, shortcuts, and PATH all succeed.
            WriteControllerReadyMarker(destination);
            // Keep one verified .previous payload until the next locked run. Startup
            // recovery can restore it after a power loss, and PATH repair refuses it.
        }
        catch (Exception installError)
        {
            try
            {
                if (candidateActivated && Directory.Exists(destination))
                    Directory.Move(destination, failed);
                if (previousMoved && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                DeleteSafeInstallDirectory(failed, parent, leaf + ".failed-");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Controller payload installation failed and rollback also failed. The prior payload remains at {backup}.",
                    installError,
                    rollbackError);
            }
            throw;
        }
        finally
        {
            DeleteSafeInstallDirectory(staging, parent, leaf + ".installing-");
        }
    }

    private static async Task RecoverControllerDirectoryTransactionAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidOperationException("The controller installation directory is unsafe.");
        var leaf = Path.GetFileName(destination);
        var backup = destination + ".previous";
        RequireSafeInstallSibling(backup, parent, leaf + ".previous");
        if (File.Exists(backup))
            throw new InvalidDataException($"The controller backup path is a file: {backup}");
        if (!Directory.Exists(backup)) return;

        RequireInstalledControllerProcessesClosed(destination, backup);
        await RequireOwnedControllerDirectoryAsync(backup, allowLegacyCanonical: true, cancellationToken);
        if (!Directory.Exists(destination))
        {
            if (File.Exists(destination))
                throw new InvalidDataException("The controller installation path is a file; the prior payload was preserved.");
            await RequireCommittedOrLegacyControllerDirectoryAsync(backup, cancellationToken);
            Directory.Move(backup, destination);
            return;
        }

        try
        {
            await RequireOwnedControllerDirectoryAsync(destination, allowLegacyCanonical: true, cancellationToken);
        }
        catch (Exception liveValidationError)
        {
            throw new InvalidDataException(
                $"Both the live controller directory and a recoverable prior payload exist. The prior payload was preserved at {backup}; repair the live directory before retrying.",
                liveValidationError);
        }
        RequireInstalledControllerProcessesClosed(destination, backup);
        if (HasExactControllerReadyMarker(destination))
        {
            await DeleteOwnedControllerDirectoryAsync(backup, allowLegacyCanonical: true, cancellationToken);
            return;
        }

        await RequireCommittedOrLegacyControllerDirectoryAsync(backup, cancellationToken);
        var failed = Path.Combine(parent, $"{leaf}.failed-{Guid.NewGuid():N}");
        RequireSafeInstallSibling(failed, parent, leaf + ".failed-");
        Directory.Move(destination, failed);
        try
        {
            Directory.Move(backup, destination);
            await DeleteOwnedControllerDirectoryAsync(failed, allowLegacyCanonical: true, CancellationToken.None);
        }
        catch (Exception rollbackError)
        {
            throw new InvalidDataException(
                $"An uncommitted controller payload was detected, but the prior payload could not be restored. The uncommitted payload remains at {failed}.",
                rollbackError);
        }
    }

    private static void WriteControllerOwnershipMarker(string directory)
    {
        File.WriteAllText(
            Path.Combine(directory, ControllerOwnershipMarkerName),
            ControllerOwnershipMarkerValue,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteControllerReadyMarker(string directory)
    {
        using var stream = new FileStream(
            Path.Combine(directory, ControllerReadyMarkerName),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using (var writer = new StreamWriter(
                   stream,
                   new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   4096,
                   leaveOpen: true))
        {
            var version = ReadExactControllerFileVersion(Path.Combine(directory, "Opticon.exe"), "UI");
            writer.Write($"{ControllerReadyMarkerValue}|{version}");
            writer.Flush();
        }
        stream.Flush(flushToDisk: true);
    }

    private static bool HasExactControllerReadyMarker(string directory)
    {
        var marker = Path.Combine(directory, ControllerReadyMarkerName);
        if (!File.Exists(marker)) return false;
        try
        {
            var uiVersion = ReadExactControllerFileVersion(Path.Combine(directory, "Opticon.exe"), "UI");
            var cliVersion = ReadExactControllerFileVersion(Path.Combine(directory, "Cli", "opticon.exe"), "CLI");
            return uiVersion == cliVersion
                   && File.ReadAllText(marker).Equals(
                       $"{ControllerReadyMarkerValue}|{uiVersion}",
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task RequireCommittedOrLegacyControllerDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        await RequireOwnedControllerDirectoryAsync(directory, allowLegacyCanonical: true, cancellationToken);
        if (File.Exists(Path.Combine(directory, ControllerOwnershipMarkerName))
            && !HasExactControllerReadyMarker(directory))
            throw new InvalidDataException($"The retained controller payload was owned but never durably committed: {directory}");
    }

    private static async Task VerifyControllerDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "controller directory");
        var marker = Path.Combine(directory, ControllerOwnershipMarkerName);
        if (!File.Exists(marker)
            || !string.Equals(
                await File.ReadAllTextAsync(marker, cancellationToken),
                ControllerOwnershipMarkerValue,
                StringComparison.Ordinal))
            throw new InvalidDataException("The controller installation ownership marker is missing or invalid.");
        var controller = Path.Combine(directory, "Opticon.exe");
        var cli = Path.Combine(directory, "Cli", "opticon.exe");
        if (!File.Exists(controller) || !File.Exists(cli))
            throw new FileNotFoundException("The staged controller UI or CLI is missing.");
        var executables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).ToArray();
        if (executables.Length < 2)
            throw new InvalidDataException("The staged controller payload is incomplete.");
        foreach (var executable in executables)
            await InvitationSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
        var uiVersion = ReadExactControllerFileVersion(controller, "UI");
        var cliVersion = ReadExactControllerFileVersion(cli, "CLI");
        if (uiVersion != cliVersion)
            throw new InvalidDataException(
                $"The controller UI ({uiVersion}) and CLI ({cliVersion}) versions do not match.");
    }

    private static Version ReadExactControllerFileVersion(string path, string description)
    {
        var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(text, out var version)
            ? version
            : throw new InvalidDataException($"The controller {description} has no valid file version.");
    }

    private static async Task RequireOwnedControllerDirectoryAsync(
        string directory,
        bool allowLegacyCanonical,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "controller directory");
        var marker = Path.Combine(directory, ControllerOwnershipMarkerName);
        if (File.Exists(marker))
        {
            await VerifyControllerDirectoryAsync(directory, cancellationToken);
            return;
        }

        var canonical = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, "Admin"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!allowLegacyCanonical
            || (!full.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(canonical + ".previous", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Refusing to replace unowned controller directory: {full}");

        var legacyExecutable = new[]
            {
                Path.Combine(full, "Opticon.exe"),
                Path.Combine(full, "Taildesk.Admin.exe")
            }
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException($"The legacy controller directory is not recognizably Opticon-owned: {full}");
        var legacyExecutables = Directory.EnumerateFiles(full, "*.exe", SearchOption.AllDirectories).ToArray();
        if (legacyExecutables.Length == 0)
            throw new InvalidDataException($"The legacy controller directory has no executable payload: {full}");
        foreach (var executable in legacyExecutables)
            await InvitationSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private static async Task DeleteOwnedControllerDirectoryAsync(
        string path,
        bool allowLegacyCanonical,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return;
        await RequireOwnedControllerDirectoryAsync(path, allowLegacyCanonical, cancellationToken);
        Directory.Delete(path, recursive: true);
    }

    private static void RequireSafeInstallSibling(string path, string parent, string leafPrefix)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(leafPrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe controller installation transaction path: {fullPath}");
    }

    private static void DeleteSafeInstallDirectory(string path, string parent, string leafPrefix)
    {
        RequireSafeInstallSibling(path, parent, leafPrefix);
        if (File.Exists(path))
            throw new InvalidDataException($"Controller transaction directory path is a file: {path}");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "controller transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void RejectDirectoryReparsePoint(string path, string description)
    {
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The {description} contains a reparse point: {directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The {description} contains a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void RequireInstalledControllerProcessesClosed(params string[] directories)
    {
        var roots = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return;

        foreach (var processName in new[] { "Opticon", "Taildesk.Admin", "Taildesk.OpticonCli" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string runningPath;
                    try
                    {
                        runningPath = Path.GetFullPath(process.MainModule?.FileName
                            ?? throw new InvalidOperationException("Windows did not expose the process executable path."));
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"Opticon could not verify running process {process.ProcessName} ({process.Id}); close it before installation.",
                            exception);
                    }
                    if (roots.Any(root => IsPathWithinDirectory(runningPath, root)))
                        throw new InvalidOperationException(
                            "Close the installed or retained Opticon UI and CLI normally before upgrading. " +
                            "This lets active SSH sessions revoke their leases and erase ephemeral keys.");
                }
            }
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private ControllerConfigurationSnapshot CaptureControllerConfiguration(string bootstrapPath)
    {
        var files = new[]
        {
            bootstrapPath,
            Path.Combine(_userProfile.Desktop, "Opticon.lnk"),
            Path.Combine(_userProfile.Startup, "Opticon.lnk"),
            Path.Combine(_userProfile.Programs, "Opticon.lnk")
        }.Select(CaptureFile).ToArray();
        using var environment = Registry.Users.CreateSubKey($"{_userProfile.Sid}\\Environment", writable: true)
                                ?? throw new InvalidOperationException("The signed-in user environment key could not be opened.");
        using var state = Registry.Users.CreateSubKey($"{_userProfile.Sid}\\Software\\Taildesk\\Opticon", writable: true)
                          ?? throw new InvalidOperationException("The signed-in user Opticon installation key could not be opened.");
        return new ControllerConfigurationSnapshot(
            files,
            CaptureRegistryValue(environment, "Path"),
            CaptureRegistryValue(state, "CliPath"),
            CaptureRegistryValue(state, ControllerInstallDirectoryValueName));
    }

    private async Task RestoreControllerConfigurationAsync(ControllerConfigurationSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            if (file.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                await File.WriteAllBytesAsync(file.Path, file.Content!, CancellationToken.None);
            }
            else if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
            }
        }
        using var environment = Registry.Users.CreateSubKey($"{_userProfile.Sid}\\Environment", writable: true)
                                ?? throw new InvalidOperationException("The signed-in user environment key could not be restored.");
        using var state = Registry.Users.CreateSubKey($"{_userProfile.Sid}\\Software\\Taildesk\\Opticon", writable: true)
                          ?? throw new InvalidOperationException("The signed-in user Opticon installation key could not be restored.");
        RestoreRegistryValue(environment, "Path", snapshot.Path);
        RestoreRegistryValue(state, "CliPath", snapshot.CliPath);
        RestoreRegistryValue(state, ControllerInstallDirectoryValueName, snapshot.InstallDirectory);
        _ = SendMessageTimeout(
            new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    private static FileSnapshot CaptureFile(string path) => File.Exists(path)
        ? new FileSnapshot(path, true, File.ReadAllBytes(path))
        : new FileSnapshot(path, false, null);

    private static RegistryValueSnapshot CaptureRegistryValue(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new RegistryValueSnapshot(false, null, RegistryValueKind.None);
        return new RegistryValueSnapshot(
            true,
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            key.GetValueKind(name));
    }

    private static void RestoreRegistryValue(RegistryKey key, string name, RegistryValueSnapshot snapshot)
    {
        if (snapshot.Existed)
            key.SetValue(name, snapshot.Value!, snapshot.Kind);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    private void AddInteractiveUserPathEntry(string directory)
    {
        if (string.IsNullOrWhiteSpace(_userProfile.Sid))
            throw new InvalidOperationException("The signed-in user SID is unavailable for Opticon CLI installation.");
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var installDirectory = Path.GetDirectoryName(target)
                               ?? throw new InvalidOperationException("The Opticon controller installation path is invalid.");
        using var key = Registry.Users.CreateSubKey($"{_userProfile.Sid}\\Environment", writable: true)
                        ?? throw new InvalidOperationException("The signed-in user environment key could not be opened.");
        using var stateKey = Registry.Users.CreateSubKey(
                                 $"{_userProfile.Sid}\\Software\\Taildesk\\Opticon", writable: true)
                             ?? throw new InvalidOperationException("The signed-in user Opticon installation key could not be opened.");
        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                      ?? string.Empty;
        var previous = NormalizePathEntry(stateKey.GetValue("CliPath") as string);
        if (previous is not null && !previous.Equals(target, StringComparison.OrdinalIgnoreCase))
            previous = null; // Never remove an unverified registry-supplied PATH directory.
        var retained = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry =>
            {
                var normalized = NormalizePathEntry(entry);
                return normalized is null
                       || (!normalized.Equals(target, StringComparison.OrdinalIgnoreCase)
                           && (previous is null || !normalized.Equals(previous, StringComparison.OrdinalIgnoreCase)));
            });
        var updated = string.Join(';', new[] { target }.Concat(retained));
        if (updated.Length > 32767)
            throw new InvalidOperationException("The signed-in user PATH is too long to add the Opticon CLI safely.");
        RegistryValueKind kind;
        try { kind = key.GetValueKind("Path"); }
        catch (IOException) { kind = RegistryValueKind.ExpandString; }
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidDataException("The signed-in user PATH registry value has an unexpected type.");
        if (!current.Equals(updated, StringComparison.Ordinal))
            key.SetValue("Path", updated, kind);
        stateKey.SetValue("CliPath", target, RegistryValueKind.String);
        stateKey.SetValue(ControllerInstallDirectoryValueName, installDirectory, RegistryValueKind.String);

        _ = SendMessageTimeout(
            new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    private static string? NormalizePathEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch { return null; }
    }

    private sealed record FileSnapshot(string Path, bool Existed, byte[]? Content);
    private sealed record RegistryValueSnapshot(bool Existed, object? Value, RegistryValueKind Kind);
    private sealed record ControllerConfigurationSnapshot(
        FileSnapshot[] Files,
        RegistryValueSnapshot Path,
        RegistryValueSnapshot CliPath,
        RegistryValueSnapshot InstallDirectory);
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

    private async Task RemoveStandaloneComponentAsync(string componentName, string[] displayNames, string executablePath, CancellationToken cancellationToken)
    {
        var productCode = FindInstalledMsiProductCode(displayNames);
        if (string.IsNullOrWhiteSpace(productCode))
        {
            throw new InvalidOperationException($"A standalone {componentName} installation was detected, but Windows did not provide a safe MSI product code for removal. Remove it manually, then run this Opticon invitation again.");
        }

        _progress.Report(new InstallProgress(componentName == "Tailscale" ? 8 : 45, $"Removing the existing standalone {componentName} installation…"));
        var uninstall = await ProcessRunner.RunAsync("msiexec.exe", ["/x", productCode, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(uninstall, $"Could not remove the existing {componentName} installation");

        for (var attempt = 0; attempt < 20 && (File.Exists(executablePath) || FindInstalledMsiProductCode(displayNames) is not null); attempt++)
            await Task.Delay(500, cancellationToken);
        if (File.Exists(executablePath) || FindInstalledMsiProductCode(displayNames) is not null)
        {
            throw new InvalidOperationException($"Windows reported that {componentName} was removed, but its standalone installation is still present. Restart Windows, remove it, then run this Opticon invitation again.");
        }
    }

    private static string? FindInstalledMsiProductCode(IEnumerable<string> displayNames)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
            if (uninstall is null) continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName, writable: false);
                var displayName = entry?.GetValue("DisplayName") as string;
                if (entry is null || !displayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase)) continue;

                var candidate = ExtractMsiProductCode(entry.GetValue("UninstallString") as string)
                                ?? ExtractMsiProductCode(subKeyName);
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? ExtractMsiProductCode(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}");
        return match.Success ? match.Value : null;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wordParameter,
        string stringParameter,
        uint flags,
        uint timeout,
        out UIntPtr result);

    private sealed class LocalTailscaleSnapshot
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DnsName { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public bool Online { get; init; }
        public string Tailnet { get; init; } = string.Empty;
        public string[] Tags { get; init; } = [];
    }

    private sealed record ComponentInstallation(string Path, bool InstalledByOpticon);
}
