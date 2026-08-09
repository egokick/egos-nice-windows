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
    private FileStream? _agentInstallLock;
    private AgentInstallTransactionJournal? _agentInstallTransaction;
    private bool _agentInstallCommitted;

    public InstallCoordinator(InvitePayload invite, string bundleDirectory, IProgress<InstallProgress> progress, bool allowTailscaleReauthentication = false)
    {
        _invite = invite;
        _bundleDirectory = Path.GetFullPath(bundleDirectory);
        _progress = progress;
        _allowTailscaleReauthentication = allowTailscaleReauthentication;
        _userProfile = InteractiveUserProfile.Resolve();
        _http = DirectHttp.CreateClient(TimeSpan.FromMinutes(10));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Taildesk-Setup/1.0");
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        EnsureInviteIsValid();
        MachineStorageSecurity.EnsureOpticonMachineState();
        await AcquireAgentInstallLockAsync(cancellationToken);

        var canResumeExistingSession = false;
        string? tempDirectory = null;
        try
        {
            var hasInterruptedAgentInstall = AgentInstallTransactionPersistence.Load() is not null;
            if (!hasInterruptedAgentInstall && File.Exists(AppPaths.AgentConfigFile))
            {
                var installedState = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
                    .LoadAsync(cancellationToken);
                RequireSafeInvitationResume(installedState);
                canResumeExistingSession = installedState.PendingInviteId == _invite.InviteId;
            }
            tempDirectory = MachineStorageSecurity.CreateRestrictedChildDirectory(
                AppPaths.SetupStagingDirectory, "install-");
            _progress.Report(new InstallProgress(4, "Checking the invitation and local payload…"));
            var agentPayload = Path.Combine(_bundleDirectory, "Payload", "Agent");
            var agentExecutable = Path.Combine(agentPayload, "Taildesk.Agent.exe");
            if (!File.Exists(agentExecutable))
            {
                throw new FileNotFoundException(
                    $"The invitation bundle is incomplete (Payload\\Agent is missing from {_bundleDirectory}).",
                    agentExecutable);
            }
            await ProductSigning.VerifyAuthenticodeAsync(agentExecutable, cancellationToken);
            var guardianPayload = Path.Combine(_bundleDirectory, "Payload", "UpdateGuardian");
            var guardianExecutable = Path.Combine(guardianPayload, "Taildesk.UpdateGuardian.exe");
            if (!File.Exists(guardianExecutable))
            {
                throw new FileNotFoundException(
                    $"The invitation bundle is incomplete (Payload\\UpdateGuardian is missing from {_bundleDirectory}).",
                    guardianExecutable);
            }
            await ProductSigning.VerifyAuthenticodeAsync(guardianExecutable, cancellationToken);
            if (_invite.Role == DeviceRole.ControllerAndManaged)
            {
                var controllerPayload = Path.Combine(_bundleDirectory, "Payload", "Admin");
                var controllerExecutable = Path.Combine(controllerPayload, "Opticon.exe");
                var cliExecutable = Path.Combine(controllerPayload, "Cli", "opticon.exe");
                if (!File.Exists(controllerExecutable) || !File.Exists(cliExecutable))
                    throw new FileNotFoundException("This controller invite is missing its signed UI or CLI payload.");
                await ProductSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);
                await ProductSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);
                foreach (var executable in Directory.EnumerateFiles(
                             controllerPayload, "*.exe", SearchOption.AllDirectories))
                    await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
                var installedController = Path.Combine(AppPaths.InstallDirectory, "Admin");
                RequireInstalledControllerProcessesClosed(installedController, installedController + ".previous");
            }

            await RecoverAgentInstallTransactionAsync(agentPayload, cancellationToken);
            if (File.Exists(AppPaths.AgentConfigFile))
            {
                var recoveredState = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
                    .LoadAsync(cancellationToken);
                RequireSafeInvitationResume(recoveredState);
                if (recoveredState.CompletedInviteId == _invite.InviteId)
                {
                    await CommitEnrollmentReceiptAsync(recoveredState, cancellationToken);
                    _progress.Report(new InstallProgress(100, "This invitation is already installed and enrolled."));
                    return;
                }
                canResumeExistingSession = recoveredState.PendingInviteId == _invite.InviteId;
            }

            // Prove or install the stable Guardian before changing recovery,
            // network, remote-access, enrollment, or Agent state. A compatible
            // signed Guardian remains pinned even when Setup itself is newer.
            await InstallGuardianAsync(guardianPayload, cancellationToken);
            _progress.Report(new InstallProgress(7, "Checking the Windows OpenSSH recovery component…"));
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

                var up = await RunPrivilegedChildAsync(tailscale,
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
                var advertise = await RunPrivilegedChildAsync(tailscale, ["set", "--advertise-exit-node"], TimeSpan.FromSeconds(30), cancellationToken);
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
            await InstallAgentAsync(agentPayload, snapshot.Ip, cancellationToken);
            await ConfigureFirewallAsync(snapshot.Ip, rustDesk, cancellationToken);
            OpticonComponentIntegration.Integrate(installedNetworkComponent, rustDeskInstallation.InstalledByOpticon);

            await InstallControllerPayloadAsync(_invite.Role == DeviceRole.ControllerAndManaged, cancellationToken);

            _progress.Report(new InstallProgress(94, "Starting the Opticon agent…"));
            var start = await RunSystemToolAsync("schtasks.exe", ["/Run", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(20), cancellationToken);
            if (!await WaitForListeningPortAsync(45831, TimeSpan.FromSeconds(30), cancellationToken))
                throw new InvalidOperationException("The Opticon agent task started but did not open its private API listener on TCP 45831.");
            EnsureSuccess(start, "The Opticon background agent task could not be started");
            _progress.Report(new InstallProgress(96, "Waiting for the command center to confirm enrollment…"));
            await WaitForEnrollmentAsync(cancellationToken);
            _progress.Report(new InstallProgress(100, "Connected. This machine is ready."));
        }
        catch (Exception installError)
        {
            if (!_agentInstallCommitted)
            {
                try { await RollbackAgentInstallTransactionAsync(CancellationToken.None); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "Opticon installation failed and the prior Agent generation could not be restored. " +
                        "The protected transaction journal was retained for recovery.",
                        installError,
                        rollbackError);
                }
            }
            throw;
        }
        finally
        {
            try { if (tempDirectory is not null) MachineStorageSecurity.DeleteRestrictedDirectory(tempDirectory); } catch { }
            _agentInstallLock?.Dispose();
            _agentInstallLock = null;
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

        MachineStorageSecurity.RequireRestrictedDirectory(stateDirectory);

        var phase = journalExists
            ? System.Text.Encoding.UTF8.GetString(MachineStorageSecurity.ReadRestrictedFile(journalPath, 64 * 1024))
            : string.Empty;
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
        var isolated = await RunSystemToolAsync(
            Path.GetRelativePath(Environment.SystemDirectory, powershell),
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
            var result = await RunSystemToolAsync(
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
        var value = $"{{\"schemaVersion\":1,\"phase\":\"{phase}\",\"updatedAt\":\"{DateTimeOffset.UtcNow:O}\"}}";
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
            path, System.Text.Encoding.UTF8.GetBytes(value), cancellationToken);
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
        await using var installer = await DownloadVerifiedAsync(artifact, tempDirectory, cancellationToken);
        _progress.Report(new InstallProgress(18, "Installing the Opticon private-network component…"));
        var result = await InstallVerifiedMsiAsync(installer, cancellationToken);
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

        _progress.Report(new InstallProgress(49, $"Downloading Opticon's remote-access component ({artifact.Version})…"));
        await using var installer = await DownloadVerifiedAsync(artifact, tempDirectory, cancellationToken);
        _progress.Report(new InstallProgress(56, "Installing the Opticon remote-access component…"));
        var install = await InstallVerifiedMsiAsync(installer, cancellationToken);
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
        var service = await RunSystemToolAsync("sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!service.Succeeded)
        {
            // RustDesk starts long-lived service/session children which inherit redirected
            // standard handles. Capturing them would wait forever after this command exits.
            var installService = await RunPrivilegedChildAsync(rustDesk, ["--install-service"],
                TimeSpan.FromSeconds(20), cancellationToken, captureOutput: false);
            EnsureSuccess(installService, "RustDesk service installation failed");
        }
        var automatic = await RunSystemToolAsync("sc.exe", ["config", "RustDesk", "start=", "auto"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(automatic, "RustDesk could not be configured for automatic startup");
        var recovery = await RunSystemToolAsync("sc.exe",
            ["failure", "RustDesk", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
            TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(recovery, "RustDesk service recovery could not be configured");
        var failureFlag = await RunSystemToolAsync("sc.exe", ["failureflag", "RustDesk", "1"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(failureFlag, "RustDesk non-crash failure recovery could not be configured");


        // RustDesk 1.4.x can launch a long-lived child while setting the password.
        // Do not redirect its inherited handles: they would otherwise keep Setup
        // waiting after the password command itself has completed.
        var password = await RunPrivilegedChildAsync(rustDesk, ["--password", _invite.RustDeskPassword],
            TimeSpan.FromSeconds(15), cancellationToken, captureOutput: false);
        EnsureSuccess(password, "RustDesk password provisioning failed");

        _ = await RunSystemToolAsync("sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        RustDeskServiceProfileStore.HardenAll();
        var restart = await RunSystemToolAsync("sc.exe", ["start", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(restart, "The private RustDesk service could not be restarted");
    }

    private async Task InstallAgentAsync(
        string source, string tailscaleIp, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(70, "Installing the Opticon background agent…"));
        var destination = AppPaths.AgentInstallDirectory;
        if (_agentInstallTransaction is null)
        {
            var operationId = Guid.NewGuid();
            var candidate = AgentInstallTransactionPersistence.CandidateDirectory(operationId);
            var rollback = AgentInstallTransactionPersistence.RollbackDirectory(operationId);
            var failed = AgentInstallTransactionPersistence.FailedDirectory(operationId);
            RequireAgentTransactionPath(candidate, operationId, "installing");
            RequireAgentTransactionPath(rollback, operationId, "rollback");
            RequireAgentTransactionPath(failed, operationId, "failed");
            if (File.Exists(destination))
                throw new InvalidDataException("The Agent installation path is a file.");
            var hadPreviousAgent = Directory.Exists(destination);
            if (hadPreviousAgent) await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
            var previousConfig = File.Exists(AppPaths.AgentConfigFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.AgentConfigFile, 4 * 1024 * 1024)
                : [];
            var previousReceipt = File.Exists(AppPaths.InstallReceiptFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.InstallReceiptFile, 256 * 1024)
                : [];
            var journal = new AgentInstallTransactionJournal
            {
                OperationId = operationId,
                InviteId = _invite.InviteId,
                Phase = AgentInstallTransactionPhase.Preparing,
                HadPreviousAgent = hadPreviousAgent,
                HadPreviousConfig = previousConfig.Length > 0,
                HadPreviousReceipt = previousReceipt.Length > 0,
                PreviousConfig = previousConfig,
                PreviousReceipt = previousReceipt
            };
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            _agentInstallTransaction = journal;

            CopyDirectory(source, candidate);
            await VerifyPayloadDirectoryCopyAsync(
                source, candidate, verifyDestinationExecutables: false, cancellationToken);
            journal.Phase = AgentInstallTransactionPhase.CandidateReady;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);

            _ = await RunSystemToolAsync(
                "schtasks.exe", ["/End", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(15), cancellationToken);
            await RequireAgentProcessesClosedAsync(destination, cancellationToken);
            if (hadPreviousAgent)
            {
                if (Directory.Exists(rollback) || File.Exists(rollback))
                    throw new InvalidOperationException("The Agent rollback directory is already occupied.");
                Directory.Move(destination, rollback);
                journal.Phase = AgentInstallTransactionPhase.PreviousMoved;
                await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            }
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("The Agent destination changed during its protected swap.");
            Directory.Move(candidate, destination);
            journal.Phase = AgentInstallTransactionPhase.CandidateActivated;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            await VerifyPayloadDirectoryCopyAsync(
                source, destination, verifyDestinationExecutables: true, CancellationToken.None);
        }
        else
        {
            if (_agentInstallTransaction.InviteId != _invite.InviteId
                || _agentInstallTransaction.Phase != AgentInstallTransactionPhase.CandidateActivated)
                throw new InvalidDataException("The recovered Agent installation transaction cannot resume this invitation.");
            await VerifyPayloadDirectoryCopyAsync(
                source, destination, verifyDestinationExecutables: true, cancellationToken);
        }

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
            ExposeAllLocalVolumes = false,
            ControllerShortcutPaths = []
        };
        await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).SaveAsync(config, cancellationToken);

        var agentExe = Path.Combine(destination, "Taildesk.Agent.exe");
        var taskCommand = $"\"{agentExe}\"";
        var task = await RunSystemToolAsync("schtasks.exe",
            ["/Create", "/TN", "Taildesk Agent", "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(task, "Could not create the Opticon background-agent startup task");

        const string taskSettings = "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0); Set-ScheduledTask -TaskName 'Taildesk Agent' -Settings $s | Out-Null";
        var settings = await RunSystemToolAsync(@"WindowsPowerShell\v1.0\powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", taskSettings],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(settings, "Could not apply Opticon background-agent recovery settings");
    }

    private async Task AcquireAgentInstallLockAsync(CancellationToken cancellationToken)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        _ = await MachineStorageSecurity.WriteRestrictedFileCreateNewAsync(
            AppPaths.AgentInstallTransactionLockFile, new byte[] { 0x01 }, cancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MachineStorageSecurity.RequireRestrictedFile(AppPaths.AgentInstallTransactionLockFile);
                _agentInstallLock = new FileStream(
                    AppPaths.AgentInstallTransactionLockFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    1,
                    FileOptions.None);
                return;
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Another Agent installation still owns the protected transaction lock.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private async Task RecoverAgentInstallTransactionAsync(string source, CancellationToken cancellationToken)
    {
        var journal = AgentInstallTransactionPersistence.Load();
        if (journal is null) return;
        _agentInstallTransaction = journal;
        var candidateDirectory = AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId);
        var rollbackDirectory = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        var failedDirectory = AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId);
        if (journal.Phase != AgentInstallTransactionPhase.CandidateActivated
            && Directory.Exists(AppPaths.AgentInstallDirectory)
            && !Directory.Exists(candidateDirectory) && !File.Exists(candidateDirectory)
            && !Directory.Exists(failedDirectory) && !File.Exists(failedDirectory)
            && Directory.Exists(rollbackDirectory) == journal.HadPreviousAgent
            && !File.Exists(rollbackDirectory))
        {
            await VerifyPayloadDirectoryCopyAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            journal.Phase = AgentInstallTransactionPhase.CandidateActivated;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        }
        var receipt = File.Exists(AppPaths.InstallReceiptFile)
            ? await new MachineJsonFileStore<EnrollmentReceipt>(AppPaths.InstallReceiptFile).LoadAsync(cancellationToken)
            : null;
        if (receipt is not null
            && receipt.SchemaVersion == 3
            && receipt.AgentInstallOperationId == journal.OperationId
            && receipt.InviteId == journal.InviteId)
        {
            await VerifyCommittedReceiptAgentAsync(receipt, cancellationToken);
            _agentInstallCommitted = true;
            await FinalizeAgentInstallTransactionAsync(cancellationToken);
            return;
        }

        if (journal.InviteId == _invite.InviteId
            && journal.Phase == AgentInstallTransactionPhase.CandidateActivated
            && Directory.Exists(AppPaths.AgentInstallDirectory))
        {
            await VerifyPayloadDirectoryCopyAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            return;
        }

        await RollbackAgentInstallTransactionAsync(cancellationToken);
    }

    private async Task RollbackAgentInstallTransactionAsync(CancellationToken cancellationToken)
    {
        var journal = _agentInstallTransaction ?? AgentInstallTransactionPersistence.Load();
        if (journal is null) return;
        _agentInstallTransaction = journal;
        var destination = AppPaths.AgentInstallDirectory;
        var candidate = AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId);
        var rollback = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        var failed = AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId);
        RequireAgentTransactionPath(candidate, journal.OperationId, "installing");
        RequireAgentTransactionPath(rollback, journal.OperationId, "rollback");
        RequireAgentTransactionPath(failed, journal.OperationId, "failed");

        _ = await RunSystemToolAsync(
            "schtasks.exe", ["/End", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(15), cancellationToken);
        await RequireAgentProcessesClosedAsync(destination, cancellationToken);
        if (Directory.Exists(rollback))
        {
            await VerifyInstalledExecutableDirectoryAsync(rollback, cancellationToken);
            if (Directory.Exists(destination))
            {
                await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
                if (Directory.Exists(failed) || File.Exists(failed))
                    throw new InvalidOperationException("The Agent failed-candidate directory is already occupied.");
                Directory.Move(destination, failed);
            }
            else if (File.Exists(destination))
                throw new InvalidDataException("The Agent destination is a file during rollback.");
            Directory.Move(rollback, destination);
            await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
            DeleteAgentTransactionDirectory(failed, journal.OperationId, "failed");
        }
        else if (journal.HadPreviousAgent)
        {
            if (journal.Phase >= AgentInstallTransactionPhase.PreviousMoved)
                throw new InvalidDataException("The prior Agent rollback directory is missing.");
            await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
        }
        else if (Directory.Exists(destination))
        {
            if (journal.Phase != AgentInstallTransactionPhase.CandidateActivated)
                throw new InvalidDataException("An unexpected Agent directory blocks first-install rollback.");
            await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
            DeleteAgentCanonicalDirectory(destination);
        }
        else if (File.Exists(destination))
            throw new InvalidDataException("The Agent destination is a file during first-install rollback.");

        DeleteAgentTransactionDirectory(candidate, journal.OperationId, "installing");
        DeleteAgentTransactionDirectory(failed, journal.OperationId, "failed");
        await RestoreAgentInstallStateAsync(journal, cancellationToken);
        AgentInstallTransactionPersistence.Delete();
        if (journal.HadPreviousAgent)
            _ = await RunSystemToolAsync(
                "schtasks.exe", ["/Run", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(20), cancellationToken);
        ClearAgentInstallTransaction(journal);
    }

    private async Task FinalizeAgentInstallTransactionAsync(CancellationToken cancellationToken)
    {
        var journal = _agentInstallTransaction;
        if (journal is null) return;
        var rollback = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        if (Directory.Exists(rollback))
        {
            await VerifyInstalledExecutableDirectoryAsync(rollback, cancellationToken);
            DeleteAgentTransactionDirectory(rollback, journal.OperationId, "rollback");
        }
        DeleteAgentTransactionDirectory(
            AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId), journal.OperationId, "installing");
        DeleteAgentTransactionDirectory(
            AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId), journal.OperationId, "failed");
        AgentInstallTransactionPersistence.Delete();
        ClearAgentInstallTransaction(journal);
    }

    private void RequireSafeInvitationResume(AgentConfig state)
    {
        if (state.CompletedInviteId is Guid completed && completed != _invite.InviteId)
            throw new InvalidOperationException(
                "This machine is already enrolled through a different invitation. " +
                "Use the authenticated update/maintenance workflow; invitation reinstall is disabled to preserve the working recovery identity.");
        if (state.PendingInviteId is Guid pending && pending != _invite.InviteId)
            throw new InvalidOperationException(
                "A different invitation is already pending on this machine. Resume that exact invitation or use authenticated recovery.");
    }

    private static async Task VerifyCommittedReceiptAgentAsync(
        EnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
        await using var stream = new FileStream(
            executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (receipt.AgentSize <= 0 || stream.Length != receipt.AgentSize)
            throw new InvalidDataException("The committed Agent no longer matches its protected enrollment receipt size.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!FixedAsciiEquals(hash, receipt.AgentSha256))
            throw new InvalidDataException("The committed Agent no longer matches its protected enrollment receipt hash.");
    }

    private static async Task RestoreAgentInstallStateAsync(
        AgentInstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.HadPreviousConfig)
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                AppPaths.AgentConfigFile, journal.PreviousConfig, cancellationToken);
        else MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.AgentConfigFile);
        if (journal.HadPreviousReceipt)
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                AppPaths.InstallReceiptFile, journal.PreviousReceipt, cancellationToken);
        else MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.InstallReceiptFile);
    }

    private void ClearAgentInstallTransaction(AgentInstallTransactionJournal journal)
    {
        CryptographicOperations.ZeroMemory(journal.PreviousConfig);
        CryptographicOperations.ZeroMemory(journal.PreviousReceipt);
        if (ReferenceEquals(_agentInstallTransaction, journal)) _agentInstallTransaction = null;
    }

    private static async Task RequireAgentProcessesClosedAsync(
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var processNames = Directory.Exists(installedDirectory)
            ? Directory.EnumerateFiles(installedDirectory, "*.exe", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .Append("Taildesk.Agent")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : ["Taildesk.Agent"];
        while (true)
        {
            var running = false;
            foreach (var processName in processNames)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        string processPath;
                        try
                        {
                            processPath = Path.GetFullPath(process.MainModule?.FileName
                                ?? throw new InvalidOperationException("Windows did not expose the Agent process path."));
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Opticon could not prove Agent process {process.Id} stopped before the directory swap.", exception);
                        }
                        if (IsPathWithinDirectory(processPath, installedDirectory)) running = true;
                    }
                }
            }
            if (!running) return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The prior Opticon Agent did not stop before the protected directory swap.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task VerifyInstalledExecutableDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "installed executable directory");
        var executables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).ToArray();
        if (executables.Length == 0)
            throw new InvalidDataException("The installed executable directory contains no executable.");
        foreach (var executable in executables)
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private static async Task VerifyPayloadDirectoryCopyAsync(
        string source,
        string destination,
        bool verifyDestinationExecutables,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(source, "source payload directory");
        RejectDirectoryReparsePoint(destination, "copied payload directory");
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(destination, path), StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count == 0
            || sourceFiles.Count != destinationFiles.Count
            || sourceFiles.Keys.Except(destinationFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The staged payload is not the exact authenticated source tree.");
        foreach (var (relative, sourcePath) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetExtension(sourcePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(sourcePath, cancellationToken);
            var destinationPath = destinationFiles[relative];
            if (new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length)
                throw new InvalidDataException($"The staged payload size changed at {relative}.");
            await using var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destinationStream = new FileStream(
                destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
            var destinationHash = await SHA256.HashDataAsync(destinationStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                throw new InvalidDataException($"The staged payload hash changed at {relative}.");
            if (verifyDestinationExecutables
                && Path.GetExtension(destinationPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(destinationPath, cancellationToken);
        }
    }

    private static void RequireAgentTransactionPath(string path, Guid operationId, string kind)
    {
        var expected = Path.GetFullPath(Path.Combine(
            AppPaths.InstallDirectory, $"Agent.{kind}-{operationId:N}"));
        if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(expected)!.Equals(
                Path.GetFullPath(AppPaths.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent installation transaction path is unsafe.");
    }

    private static void DeleteAgentTransactionDirectory(string path, Guid operationId, string kind)
    {
        RequireAgentTransactionPath(path, operationId, kind);
        if (File.Exists(path)) throw new InvalidDataException("An Agent transaction directory path is a file.");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "Agent transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void DeleteAgentCanonicalDirectory(string path)
    {
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(AppPaths.AgentInstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent installation directory is not canonical.");
        RejectDirectoryReparsePoint(path, "Agent installation directory");
        Directory.Delete(path, recursive: true);
    }

    private async Task InstallGuardianAsync(string source, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(6, "Checking the fail-safe update guardian..."));
        var sourceExecutable = Path.Combine(source, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(sourceExecutable))
            throw new FileNotFoundException("The signed update guardian payload is missing.", sourceExecutable);
        await ProductSigning.VerifyAuthenticodeAsync(sourceExecutable, cancellationToken);

        // The guardian is deliberately outside the swappable Agent directory.
        // Keep a compatible signed Guardian stable across ordinary Setup and
        // Agent releases; its product version need not equal the Setup version.
        var destination = AppPaths.UpdateGuardianInstallDirectory;
        var installedExecutable = Path.Combine(destination, "Taildesk.UpdateGuardian.exe");
        if (File.Exists(AppPaths.GuardianInstallTransactionFile) || !File.Exists(installedExecutable))
            await InstallGuardianFreshTransactionalAsync(source, destination, cancellationToken);
        else await StableGuardianMaintenance.ReconcileSignedReleaseAsync(source, destination, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
        await RequireInstalledGuardianWatchdogCompatibilityAsync(source, destination, cancellationToken);
        var installedVersion = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(installedExecutable).ProductVersion ?? string.Empty);
        var sourceVersion = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(sourceExecutable).ProductVersion ?? string.Empty);
        if (installedVersion == sourceVersion)
            SourceBuildProvenance.CommitActiveComponent(destination);
        _progress.Report(new InstallProgress(
            6,
            $"Signed stable Guardian {installedVersion} supports the watchdog contract; keeping it pinned."));

        var taskCommand = $"\"{installedExecutable}\"";
        var task = await RunSystemToolAsync("schtasks.exe",
            ["/Create", "/TN", RemoteAdministrationProtocol.GuardianTaskName, "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(task, "Could not create the fail-safe update-guardian task");

        var watchdogCommand = $"\"{installedExecutable}\" {RemoteAdministrationProtocol.GuardianWatchdogArgument}";
        var watchdog = await RunSystemToolAsync("schtasks.exe",
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
        var settings = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", guardianTaskSettings],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(settings, "Could not apply fail-safe update-guardian recovery/watchdog settings");
    }

    private static async Task InstallGuardianFreshTransactionalAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        GuardianInstallTransactionJournal? journal = null;
        if (File.Exists(AppPaths.GuardianInstallTransactionFile))
            journal = await new MachineJsonFileStore<GuardianInstallTransactionJournal>(
                AppPaths.GuardianInstallTransactionFile).LoadAsync(cancellationToken);
        if (journal is not null)
        {
            ValidateGuardianInstallJournal(journal);
            var interruptedStage = GuardianTransactionDirectory(journal.OperationId);
            if (Directory.Exists(destination))
            {
                await VerifyGuardianDirectoryAgainstJournalAsync(
                    destination, journal, verifyExecutables: true, cancellationToken);
                if (Directory.Exists(interruptedStage))
                    DeleteGuardianTransactionDirectory(interruptedStage, journal.OperationId);
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                return;
            }
            if (Directory.Exists(interruptedStage))
            {
                try
                {
                    await VerifyGuardianDirectoryAgainstJournalAsync(
                        interruptedStage, journal, verifyExecutables: false, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    DeleteGuardianTransactionDirectory(interruptedStage, journal.OperationId);
                    if (await SourceMatchesGuardianJournalAsync(source, journal, cancellationToken))
                    {
                        CopyDirectory(source, interruptedStage);
                        await VerifyGuardianDirectoryAgainstJournalAsync(
                            interruptedStage, journal, verifyExecutables: false, cancellationToken);
                    }
                    else
                    {
                        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                        journal = null;
                    }
                }
            }
            else
            {
                // The crash occurred after the protected journal commit but before
                // the first namespace mutation, so restarting with the current
                // authenticated source is safe.
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                journal = null;
            }
            if (journal is not null)
            {
                Directory.Move(interruptedStage, destination);
                await VerifyGuardianDirectoryAgainstJournalAsync(
                    destination, journal, verifyExecutables: true, CancellationToken.None);
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                return;
            }
        }

        var operationId = Guid.NewGuid();
        var staging = GuardianTransactionDirectory(operationId);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException("The Guardian destination changed before its atomic installation.");
        if (Directory.Exists(staging) || File.Exists(staging))
            throw new InvalidOperationException("The Guardian staging directory is already occupied.");
        journal = new GuardianInstallTransactionJournal
        {
            OperationId = operationId,
            Files = await CreateGuardianFileRecordsAsync(source, cancellationToken)
        };
        await new MachineJsonFileStore<GuardianInstallTransactionJournal>(AppPaths.GuardianInstallTransactionFile)
            .SaveAsync(journal, cancellationToken);
        CopyDirectory(source, staging);
        await VerifyPayloadDirectoryCopyAsync(
            source, staging, verifyDestinationExecutables: false, cancellationToken);
        Directory.Move(staging, destination);
        await VerifyPayloadDirectoryCopyAsync(
            source, destination, verifyDestinationExecutables: true, CancellationToken.None);
        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
    }

    private static string GuardianTransactionDirectory(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new InvalidDataException("The Guardian transaction operation ID is empty.");
        return Path.Combine(AppPaths.InstallDirectory, $"UpdateGuardian.installing-{operationId:N}");
    }

    private static void DeleteGuardianTransactionDirectory(string path, Guid operationId)
    {
        var expected = Path.GetFullPath(GuardianTransactionDirectory(operationId));
        if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Guardian transaction path is unsafe.");
        if (File.Exists(path)) throw new InvalidDataException("The Guardian transaction path is a file.");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "Guardian transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateGuardianInstallJournal(GuardianInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion != 2 || journal.OperationId == Guid.Empty
            || journal.Files.Count is < 1 or > 32
            || journal.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count
            || journal.Files.Any(file => string.IsNullOrWhiteSpace(file.Path)
                                         || Path.IsPathRooted(file.Path)
                                         || file.Path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")
                                         || file.Size <= 0
                                         || !Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$")))
            throw new InvalidDataException("The protected Guardian installation journal is invalid.");
    }

    private static async Task<List<GuardianInstallFileRecord>> CreateGuardianFileRecordsAsync(
        string source,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(source, "Guardian source directory");
        var records = new List<GuardianInstallFileRecord>();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(new GuardianInstallFileRecord
            {
                Path = Path.GetRelativePath(source, path).Replace('\\', '/'),
                Size = stream.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()
            });
        }
        var journal = new GuardianInstallTransactionJournal { Files = records };
        journal.OperationId = Guid.NewGuid();
        ValidateGuardianInstallJournal(journal);
        return records;
    }

    private static async Task VerifyGuardianDirectoryAgainstJournalAsync(
        string directory,
        GuardianInstallTransactionJournal journal,
        bool verifyExecutables,
        CancellationToken cancellationToken)
    {
        ValidateGuardianInstallJournal(journal);
        RejectDirectoryReparsePoint(directory, "Guardian transaction payload");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (files.Count != journal.Files.Count
            || files.Keys.Except(journal.Files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The Guardian transaction payload does not match its protected journal.");
        foreach (var expected in journal.Files)
        {
            var path = files[expected.Path];
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.Size)
                throw new InvalidDataException("The Guardian transaction payload size changed.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!FixedAsciiEquals(hash, expected.Sha256))
                throw new InvalidDataException("The Guardian transaction payload hash changed.");
            if (verifyExecutables && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
        }
    }

    private static async Task<bool> SourceMatchesGuardianJournalAsync(
        string source,
        GuardianInstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyGuardianDirectoryAgainstJournalAsync(source, journal, verifyExecutables: true, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
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
        var minimumWatchdogVersion = UpdatePackageVerifier.ParseVersion(
            RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion);
        if (sourceVersion < minimumWatchdogVersion)
            throw new InvalidOperationException(
                $"This Setup carries Guardian {sourceVersion}, but watchdog mode requires {minimumWatchdogVersion} or newer.");
        var installedFiles = Directory.EnumerateFiles(installedDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(installedDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        if (installedVersion != sourceVersion)
        {
            if (RemoteAdministrationProtocol.SupportsGuardianWatchdog(installedVersion)
                && installedFiles.Count == 1
                && installedFiles.ContainsKey("Taildesk.UpdateGuardian.exe"))
                return;
            if (installedVersion < minimumWatchdogVersion)
                throw new InvalidOperationException(
                    $"The existing stable Guardian {installedVersion} predates watchdog support {minimumWatchdogVersion} and was not overwritten. " +
                    "Complete attended stable-Guardian maintenance before reinstalling Opticon.");
            throw new InvalidOperationException(
                $"The stable Guardian {installedVersion} has companion files this Setup cannot attest. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");
        }

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
            _ = await RunSystemToolAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={rule}"], TimeSpan.FromSeconds(20), cancellationToken);
        }
        _ = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "delete", "rule", "name=all", "dir=in", $"program={rustDesk}"],
            TimeSpan.FromSeconds(20), cancellationToken);

        var agentRule = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=Taildesk Agent (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=45831", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={agent}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(agentRule, "Could not create the Opticon agent firewall rule");

        var rustDeskRule = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk Direct (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=21118", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskRule, "Could not create the RustDesk firewall rule");

        var rustDeskExternalV4Block = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk External IPv4 Block", "dir=out", "action=block", "remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskExternalV4Block, "Could not block RustDesk from non-Tailscale IPv4 destinations");

        var rustDeskExternalV6Block = await RunSystemToolAsync("netsh.exe",
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
        await ProductSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);

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
        var bootstrapBytes = JsonSerializer.SerializeToUtf8Bytes(bootstrap, JsonDefaults.Options);
        if (File.Exists(AppPaths.ControllerBootstrapFile) || Directory.Exists(AppPaths.ControllerBootstrapFile))
            throw new InvalidOperationException(
                "A protected controller bootstrap is already waiting for the selected user to consume it.");
        var bootstrapWritten = false;
        try
        {
            await InstallControllerDirectoryTransactionalAsync(
                source,
                destination,
                async () =>
                {
                    await MachineStorageSecurity.WriteUserBootstrapAsync(
                        AppPaths.ControllerBootstrapFile,
                        bootstrapBytes,
                        _userProfile.Sid,
                        cancellationToken);
                    bootstrapWritten = true;
                },
                cancellationToken);
            SourceBuildProvenance.CommitActiveComponent(destination);
        }
        catch
        {
            if (bootstrapWritten || File.Exists(AppPaths.ControllerBootstrapFile))
            {
                try
                {
                    MachineStorageSecurity.DeleteUserBootstrap(
                        AppPaths.ControllerBootstrapFile, _userProfile.Sid);
                }
                catch { }
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
            // after the protected bootstrap and controller payload succeed.
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
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
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
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
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
    private async Task<LocalTailscaleSnapshot> ReadTailscaleStatusAsync(string tailscale, CancellationToken cancellationToken)
    {
        var result = await RunPrivilegedChildAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(30), cancellationToken);
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
            var result = await RunPrivilegedChildAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
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
        var store = new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var state = await store.LoadAsync(cancellationToken);
            if (EnrollmentMatchesInvitation(state))
            {
                await CommitEnrollmentReceiptAsync(state, cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("The agent is installed and will keep retrying, but the command center did not confirm enrollment within two minutes. Make sure the Opticon command center is running and the private-network policy is active.");
    }

    private bool EnrollmentMatchesInvitation(AgentConfig state)
    {
        var expectedTokenHash = SecurityHelpers.HashToken(_invite.AgentToken);
        return state.CompletedInviteId == _invite.InviteId
               && state.PendingInviteId is null
               && string.IsNullOrEmpty(state.PendingInviteSecretProtected)
               && state.DeviceId != Guid.Empty
               && state.Role == _invite.Role
               && state.DeviceName.Equals(_invite.DeviceName, StringComparison.Ordinal)
               && state.CoordinatorUrl.Equals(_invite.CoordinatorUrl, StringComparison.Ordinal)
               && FixedAsciiEquals(state.AgentTokenHash, expectedTokenHash);
    }

    private async Task CommitEnrollmentReceiptAsync(
        AgentConfig state,
        CancellationToken cancellationToken)
    {
        if (!EnrollmentMatchesInvitation(state))
            throw new InvalidDataException(
                "The protected Agent state does not prove completion of this exact invitation.");
        var agentExecutable = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        await ProductSigning.VerifyAuthenticodeAsync(agentExecutable, cancellationToken);
        var version = FileVersionInfo.GetVersionInfo(agentExecutable).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The enrolled Agent executable has no product version.");
        await using var stream = new FileStream(
            agentExecutable, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        var receipt = new EnrollmentReceipt
        {
            InviteId = _invite.InviteId,
            DeviceId = state.DeviceId,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            AgentTokenHash = state.AgentTokenHash,
            AgentVersion = version,
            AgentSize = stream.Length,
            AgentSha256 = sha256,
            AgentInstallOperationId = _agentInstallTransaction?.OperationId ?? Guid.Empty
        };
        var store = new MachineJsonFileStore<EnrollmentReceipt>(AppPaths.InstallReceiptFile);
        await store.SaveAsync(receipt, cancellationToken);
        var committed = await store.LoadAsync(cancellationToken);
        if (committed.SchemaVersion != 3
            || committed.InviteId != receipt.InviteId
            || committed.DeviceId != receipt.DeviceId
            || committed.AgentVersion != receipt.AgentVersion
            || committed.AgentSize != receipt.AgentSize
            || committed.AgentInstallOperationId != receipt.AgentInstallOperationId
            || !FixedAsciiEquals(committed.AgentTokenHash, receipt.AgentTokenHash)
            || !FixedAsciiEquals(committed.AgentSha256, receipt.AgentSha256))
            throw new InvalidDataException("The protected enrollment success receipt did not verify after commit.");
        SourceBuildProvenance.CommitActiveInstallation();
        _agentInstallCommitted = true;
        await FinalizeAgentInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.PruneInstalledTrust();
    }

    private static bool FixedAsciiEquals(string left, string right)
    {
        if (left.Length != right.Length || left.Any(character => character > 0x7f)
            || right.Any(character => character > 0x7f))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));
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

    private async Task<VerifiedInstallerLease> DownloadVerifiedAsync(
        DependencyArtifact artifact,
        string protectedDirectory,
        CancellationToken cancellationToken)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(protectedDirectory);
        if (!string.Equals(Path.GetFileName(artifact.FileName), artifact.FileName, StringComparison.Ordinal)
            || !artifact.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pinned dependency filename is unsafe.");
        var destination = Path.Combine(protectedDirectory, artifact.FileName);
        var errors = new List<string>();
        foreach (var url in new[] { artifact.PrimaryUrl, artifact.FallbackUrl })
        {
            try
            {
                DeleteStagedPartial(destination, protectedDirectory);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidDataException("The pinned dependency URL is not safe HTTPS.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.Clear();
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new HttpRequestException($"Dependency server returned HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength != artifact.Size)
                    throw new InvalidDataException("The dependency response omitted or changed its pinned Content-Length.");
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Encoded dependency responses are not accepted.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(
                                 destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[128 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var remaining = artifact.Size - total;
                        var requested = checked((int)Math.Min(buffer.Length, Math.Max(1L, remaining + 1)));
                        var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > artifact.Size)
                            throw new InvalidDataException("The dependency response exceeded its pinned size.");
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    if (total != artifact.Size)
                        throw new InvalidDataException("The dependency response ended before its pinned size.");
                }
                if (!CryptographicOperations.FixedTimeEquals(
                        hasher.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
                    throw new InvalidDataException("SHA-256 did not match the pinned artifact.");
                MachineStorageSecurity.SealRestrictedFile(destination);
                var lease = new VerifiedInstallerLease(
                    destination,
                    artifact,
                    new FileStream(
                        destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
                try
                {
                    await VerifyInstallerLeaseAsync(lease, cancellationToken);
                    return lease;
                }
                catch
                {
                    await lease.DisposeAsync();
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{new Uri(url).Host}: {exception.Message}");
            }
        }
        try { DeleteStagedPartial(destination, protectedDirectory); } catch { }
        throw new InvalidDataException($"Neither verified source supplied {artifact.Product} {artifact.Version}: {string.Join(" | ", errors)}");
    }

    private static async Task<ProcessResult> InstallVerifiedMsiAsync(
        VerifiedInstallerLease lease,
        CancellationToken cancellationToken)
    {
        await VerifyInstallerLeaseAsync(lease, cancellationToken);
        return await ProcessRunner.RunAsync(
            SystemExecutable("msiexec.exe"),
            ["/i", lease.Path, "/qn", "/norestart"],
            TimeSpan.FromMinutes(5),
            cancellationToken,
            environment: BuildPrivilegedEnvironment(),
            clearEnvironment: true);
    }

    private static async Task VerifyInstallerLeaseAsync(
        VerifiedInstallerLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.Stream.Length != lease.Artifact.Size)
            throw new InvalidDataException("The held installer lease changed size.");
        lease.Stream.Position = 0;
        var hash = await SHA256.HashDataAsync(lease.Stream, cancellationToken);
        lease.Stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(lease.Artifact.Sha256)))
            throw new InvalidDataException("The held installer lease no longer matches its pinned SHA-256.");
        var signer = await RequireInstallerSignatureAsync(lease.Path, cancellationToken);
        var expectedSigner = lease.Artifact.ExpectedSignerThumbprint.ToUpperInvariant();
        if (!Regex.IsMatch(expectedSigner, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expectedSigner),
                System.Text.Encoding.ASCII.GetBytes(signer)))
            throw new InvalidDataException("The dependency signer does not match its pinned publisher.");
        if (lease.SignerThumbprint is null) lease.SignerThumbprint = signer;
        else if (!CryptographicOperations.FixedTimeEquals(
                     System.Text.Encoding.ASCII.GetBytes(lease.SignerThumbprint),
                     System.Text.Encoding.ASCII.GetBytes(signer)))
            throw new InvalidDataException("The held installer signer changed after verification.");
    }

    private static async Task<string> RequireInstallerSignatureAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        var command =
            "$ErrorActionPreference='Stop';" +
            "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + pathBase64 + "'));" +
            @"$s=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $p;" +
            "if($s.Status.ToString() -cne 'Valid' -or $null -eq $s.SignerCertificate -or $null -eq $s.TimeStamperCertificate){exit 41};" +
            "$eku=$s.SignerCertificate.EnhancedKeyUsageList | Where-Object {$_.ObjectId -eq '1.3.6.1.5.5.7.3.3'};" +
            "if($null -eq $eku){exit 42};" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint.ToUpperInvariant())";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var result = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        var signer = result.StandardOutput.Trim().ToUpperInvariant();
        if (!result.Succeeded || !Regex.IsMatch(signer, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The pinned dependency is not signed and timestamped by a valid Windows publisher.");
        return signer;
    }

    private static void DeleteStagedPartial(string path, string protectedDirectory)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(protectedDirectory);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(protectedDirectory),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The dependency staging path escaped its protected directory.");
        if (!File.Exists(full))
        {
            if (Directory.Exists(full))
                throw new InvalidDataException("The dependency staging path is a directory.");
            return;
        }
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The dependency staging object is not a regular file.");
        File.Delete(full);
    }

    private static string SystemExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || fileName.Contains(':'))
            throw new InvalidDataException("The privileged Windows executable name is unsafe.");
        var systemDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.SystemDirectory));
        var executable = Path.GetFullPath(Path.Combine(systemDirectory, fileName));
        if (!executable.StartsWith(systemDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))
            throw new FileNotFoundException("The fixed System32 executable is missing.", executable);
        var relative = Path.GetRelativePath(systemDirectory, executable);
        var current = systemDirectory;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The privileged Windows executable path is unsafe.");
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The privileged Windows executable path contains a reparse point.");
        }
        var attributes = File.GetAttributes(executable);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The fixed System32 executable is not a regular file.");
        return executable;
    }

    private static Task<ProcessResult> RunSystemToolAsync(
        string relativePath,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool captureOutput = true) =>
        ProcessRunner.RunAsync(
            SystemExecutable(relativePath),
            arguments,
            timeout,
            cancellationToken,
            captureOutput,
            BuildPrivilegedEnvironment(),
            clearEnvironment: true);

    private static Task<ProcessResult> RunPrivilegedChildAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        bool captureOutput = true)
    {
        var full = Path.GetFullPath(executable);
        var programFiles = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        if (!full.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The privileged child escaped the fixed Program Files root.");
        var current = programFiles;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixed Program Files root is a reparse point.");
        foreach (var component in Path.GetRelativePath(programFiles, full).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The privileged child path is unsafe.");
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new FileNotFoundException("The fixed privileged child path is incomplete.", current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The privileged child path contains a reparse point.");
        }
        if (!File.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("The fixed privileged child executable is missing or unsafe.", full);
        return ProcessRunner.RunAsync(
            full,
            arguments,
            timeout,
            cancellationToken,
            captureOutput,
            BuildPrivilegedEnvironment(),
            clearEnvironment: true);
    }

    private static IReadOnlyDictionary<string, string?> BuildPrivilegedEnvironment()
    {
        var windows = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var system32 = Path.GetFullPath(Environment.SystemDirectory);
        var programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["ProgramData"] = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ["ProgramFiles"] = programFiles,
            ["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            ["ComSpec"] = SystemExecutable("cmd.exe"),
            ["PATH"] = system32 + Path.PathSeparator + Path.Combine(system32, "Wbem"),
            ["PATHEXT"] = ".COM;.EXE",
            ["PSModulePath"] = Path.Combine(system32, "WindowsPowerShell", "v1.0", "Modules"),
            ["USERPROFILE"] = Path.Combine(system32, "config", "systemprofile"),
            ["APPDATA"] = Path.Combine(system32, "config", "systemprofile", "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(system32, "config", "systemprofile", "AppData", "Local"),
            ["TEMP"] = AppPaths.SetupStagingDirectory,
            ["TMP"] = AppPaths.SetupStagingDirectory
        };
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            environment["ProgramFiles(x86)"] = Path.GetFullPath(programFilesX86);
        return environment;
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
        var uninstall = await RunSystemToolAsync("msiexec.exe", ["/x", productCode, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
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
        var result = await RunPrivilegedChildAsync(executable, ["version"], TimeSpan.FromSeconds(20), cancellationToken);
        return result.Succeeded && result.StandardOutput.TrimStart().StartsWith(version, StringComparison.Ordinal);
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
            .Select(name => new KeyValuePair<string, string>(
                name, PathGuard.ValidateRemoteFileRoot(known[name])))
            .Where(pair => Directory.Exists(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"The protected payload directory is missing: {source}");
        RejectDirectoryReparsePoint(source, "source payload directory");
        var destinationParent = Path.GetDirectoryName(destination)
                                ?? throw new InvalidDataException("The payload destination has no parent directory.");
        if (Directory.Exists(destinationParent))
            RejectDirectoryReparsePoint(destinationParent, "payload destination parent");
        Directory.CreateDirectory(destination);
        RejectDirectoryReparsePoint(destination, "payload destination directory");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The source payload contains an unsafe directory.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The payload directory escaped its destination.");
            Directory.CreateDirectory(target);
            RejectDirectoryReparsePoint(target, "payload destination directory");
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var attributes = File.GetAttributes(file);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("The source payload contains an unsafe file.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The payload file escaped its destination.");
            if (File.Exists(target)
                && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The payload destination file is a reparse point.");
            using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.WriteThrough);
            input.CopyTo(output, 128 * 1024);
            output.Flush(flushToDisk: true);
        }
        RejectDirectoryReparsePoint(destination, "copied payload directory");
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

    private sealed class LocalTailscaleSnapshot
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DnsName { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public bool Online { get; init; }
        public string Tailnet { get; init; } = string.Empty;
        public string[] Tags { get; init; } = [];
    }

    private sealed class EnrollmentReceipt
    {
        public int SchemaVersion { get; set; } = 3;
        public Guid InviteId { get; set; }
        public Guid DeviceId { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string AgentTokenHash { get; set; } = string.Empty;
        public string AgentVersion { get; set; } = string.Empty;
        public long AgentSize { get; set; }
        public string AgentSha256 { get; set; } = string.Empty;
        public Guid AgentInstallOperationId { get; set; }
    }

    private sealed class GuardianInstallTransactionJournal
    {
        public int SchemaVersion { get; set; } = 2;
        public Guid OperationId { get; set; }
        public List<GuardianInstallFileRecord> Files { get; set; } = [];
    }

    private sealed class GuardianInstallFileRecord
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class VerifiedInstallerLease : IAsyncDisposable
    {
        private bool _disposed;

        public VerifiedInstallerLease(string path, DependencyArtifact artifact, FileStream stream)
        {
            Path = path;
            Artifact = artifact;
            Stream = stream;
        }

        public string Path { get; }
        public DependencyArtifact Artifact { get; }
        public FileStream Stream { get; }
        public string? SignerThumbprint { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Stream.DisposeAsync();
            DeleteStagedPartial(Path, System.IO.Path.GetDirectoryName(Path)
                                      ?? throw new InvalidOperationException(
                                          "The held installer has no protected parent directory."));
        }
    }

    private sealed record ComponentInstallation(string Path, bool InstalledByOpticon);
}
