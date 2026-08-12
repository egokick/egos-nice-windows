using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;
    private readonly AgentClient _agents;
    private readonly TransferManager _transfers;
    private readonly ScheduledTransferManager _scheduledTransfers;
    private readonly OpticonReleaseClient _releases = new();
    private readonly ReleaseDeploymentService _releaseDeployment;
    private readonly RemoteDeviceUpdateCoordinator _deviceUpdates;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly SemaphoreSlim _releaseDeploymentGate = new(1, 1);
    private readonly SemaphoreSlim _invitationInventoryGate = new(1, 1);
    private List<ReleaseInvitationSummary> _remoteInvitations = [];
    private readonly HashSet<SshSessionHandle> _sshSessions = [];
    private DeviceRecord? _selectedDevice;
    private string _status = "Ready";
    private bool _busy;
    private bool _checksRunning;
    private bool _releaseDeploymentBusy;
    private string _checksSummary = "Checks have not run yet";
    private string _checksLastRun = "Not yet run";
    private string _deployedReleaseVersion = "Not checked";
    private string _releaseDeploymentStatus = "Read the live invite release state to prepare a deployment.";
    private ReleaseDeploymentPreflight? _releasePreflight;
    private bool _releaseDeploymentCanResume;
    private readonly SemaphoreSlim _checksGate = new(1, 1);

    public MainViewModel(AdminState state, HeadscaleApiClient headscale, AgentClient agents, TransferManager transfers,
        ScheduledTransferManager scheduledTransfers)
    {
        _state = state;
        _headscale = headscale;
        _agents = agents;
        _transfers = transfers;
        _scheduledTransfers = scheduledTransfers;
        _releaseDeployment = new ReleaseDeploymentService(state);
        _deviceUpdates = new RemoteDeviceUpdateCoordinator(agents);
        Transfers = transfers.Items;
        ScheduledTransfers = scheduledTransfers.Schedules;
        ScheduledTransferHistory = scheduledTransfers.History;
    }

    public ObservableCollection<DeviceRecord> Devices { get; } = [];
    public ObservableCollection<InviteRecord> Invites { get; } = [];
    public ObservableCollection<TransferRow> Transfers { get; }
    public ObservableCollection<ScheduledTransferRow> ScheduledTransfers { get; }
    public ObservableCollection<ScheduledTransferHistoryRow> ScheduledTransferHistory { get; }
    public ScheduledTransferManager ScheduledTransferManager => _scheduledTransfers;

    public void CancelTransfer(TransferRow transfer) => _transfers.Cancel(transfer);

    public void ResumeTransfer(TransferRow transfer) => _transfers.Resume(transfer);
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> UpdateProgressLines { get; } = [];
    public ObservableCollection<SystemCheckResult> SystemChecks { get; } = [];
    public ObservableCollection<DeployedReleaseArtifactRow> DeployedReleaseArtifacts { get; } = [];
    public AdminConfig Config => _state.Config;
    public bool IsPrimary => Config.Mode == AdminMode.Primary;
    public string OpticonVersion { get; } = UpdatePackageVerifier.NormalizeVersion(
        typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? string.Empty);
    public DeviceRecord? SelectedDevice { get => _selectedDevice; set { _selectedDevice = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
    public bool Busy { get => _busy; set { _busy = value; Changed(); } }
    public bool ChecksRunning { get => _checksRunning; private set { _checksRunning = value; Changed(); Changed(nameof(CanRunSystemChecks)); } }
    public bool CanRunSystemChecks => !ChecksRunning;
    public string ChecksSummary { get => _checksSummary; private set { _checksSummary = value; Changed(); } }
    public string ChecksLastRun { get => _checksLastRun; private set { _checksLastRun = value; Changed(); } }
    public string DeployedReleaseVersion { get => _deployedReleaseVersion; private set { _deployedReleaseVersion = value; Changed(); } }
    public string ReleaseDeploymentStatus { get => _releaseDeploymentStatus; private set { _releaseDeploymentStatus = value; Changed(); } }
    public bool ReleaseDeploymentBusy
    {
        get => _releaseDeploymentBusy;
        private set
        {
            _releaseDeploymentBusy = value;
            Changed();
            Changed(nameof(CanDeployRelease));
            Changed(nameof(DeployReleaseButtonText));
        }
    }
    public bool CanDeployRelease => IsPrimary && !ReleaseDeploymentBusy
        && _releasePreflight is { AlreadyDeployed: false, TargetIsOlder: false }
        && (!_releasePreflight.DeploymentBlocked || _releaseDeploymentCanResume);
    public bool CanResumeReleaseDeployment => _releaseDeploymentCanResume;
    public string DeployReleaseButtonText => ReleaseDeploymentBusy
        ? "Deploying source release…"
        : _releasePreflight?.AlreadyDeployed == true
            ? $"Deployed version {_releasePreflight.DeployedVersion} already matches this Command Center"
            : _releasePreflight?.DeploymentBlocked == true && _releaseDeploymentCanResume
                ? $"Resume deployment {OpticonVersion} for invitations"
                : _releasePreflight?.DeploymentBlocked == true
                    ? "Another release deployment is in progress"
                    : _releasePreflight?.TargetIsOlder == true
                        ? $"Deployed version {_releasePreflight.DeployedVersion} is newer"
            : $"Deploy {OpticonVersion} for invitations";
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ReplaceInvites();
        if (Config.SetupComplete) await RefreshAsync(cancellationToken);
    }

    public async Task ShutdownSshSessionsAsync(CancellationToken cancellationToken = default)
    {
        SshSessionHandle[] sessions;
        lock (_sshSessions) sessions = [.. _sshSessions];
        if (sessions.Length == 0) return;

        await Task.WhenAll(sessions.Select(session => session.TerminateAsync(cancellationToken)));
    }

    public async Task RunSystemChecksAsync(CancellationToken cancellationToken = default)
    {
        if (!await _checksGate.WaitAsync(0, cancellationToken)) return;
        ChecksRunning = true;
        ChecksSummary = "Running comprehensive system checks...";
        Status = "Checking Opticon configuration...";
        SystemChecks.Clear();
        try
        {
            var progress = new Progress<SystemCheckResult>(item => SystemChecks.Add(item));
            var results = await new SystemHealthChecker(_state, _headscale).RunAsync(progress, cancellationToken);
            var passed = results.Count(item => item.Severity == SystemCheckSeverity.Pass);
            var warnings = results.Count(item => item.Severity == SystemCheckSeverity.Warning);
            var failures = results.Count(item => item.Severity == SystemCheckSeverity.Failure);
            ChecksSummary = $"{passed} passed  ·  {warnings} warnings  ·  {failures} failures";
            ChecksLastRun = $"Last run {DateTime.Now:g}";
            Status = failures == 0
                ? warnings == 0 ? "System checks passed" : "System checks passed with warnings"
                : $"System checks found {failures} failure(s)";
            Log($"System checks completed: {passed} passed, {warnings} warnings, {failures} failures.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ChecksSummary = "Checks canceled";
            Status = "System checks canceled";
        }
        catch (Exception exception)
        {
            SystemChecks.Add(new SystemCheckResult("Opticon", "Diagnostics engine", SystemCheckSeverity.Failure, exception.Message));
            ChecksSummary = "Diagnostics could not complete";
            Status = "System checks failed";
            Log("System diagnostics failed: " + exception.Message);
        }
        finally
        {
            ChecksRunning = false;
            _checksGate.Release();
        }
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Busy || !Config.SetupComplete) return;
        Busy = true;
        Status = "Refreshing devices…";
        try
        {
            if (Config.Mode == AdminMode.Secondary)
            {
                await SyncSecondaryAsync(cancellationToken);
            }
            else
            {
                await RefreshPrimaryTailnetAsync(cancellationToken);
                await CleanupInactiveHostedInvitationsAsync(cancellationToken);
                try
                {
                    await LoadHostedInvitationInventoryAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    Log("Hosted invitation inventory refresh failed: " + exception.Message);
                }
            }

            var currentDevices = Devices.ToArray();
            await Task.WhenAll(currentDevices.Select(device => ProbeAgentAsync(device, cancellationToken)));
            foreach (var device in currentDevices)
            {
                if (Config.Mode == AdminMode.Primary && device.PendingRoleSync && device.State == DeviceConnectionState.Online)
                {
                    try
                    {
                        await _agents.SetRoleAsync(device, GetAgentToken(device), device.Role, cancellationToken);
                        device.PendingRoleSync = false;
                    }
                    catch (Exception exception)
                    {
                        Log($"Role shortcut sync pending for {device.Name}: {exception.Message}");
                    }
                }
                if (Config.Mode == AdminMode.Primary && device.PendingCredentialRotation && device.State == DeviceConnectionState.Online)
                {
                    await RotateOneAsync(device, cancellationToken);
                }
            }
            ReplaceDevices(Config.Mode == AdminMode.Primary ? Config.Devices : Devices.ToArray());
            if (Config.Mode == AdminMode.Primary) await _state.SaveAsync(cancellationToken);
            Status = $"{Devices.Count(device => device.State == DeviceConnectionState.Online)} online / {Devices.Count} enrolled";
        }
        catch (Exception exception)
        {
            Log("Refresh failed: " + exception.Message);
            Status = "Refresh failed";
        }
        finally
        {
            Busy = false;
        }
    }

    public string GetAgentToken(DeviceRecord device) => SecretProtector.Unprotect(device.AgentTokenProtected);
    public string GetRustDeskPassword(DeviceRecord device) => SecretProtector.Unprotect(device.RustDeskPasswordProtected);

    public async Task RenameDeviceAsync(
        DeviceRecord device,
        string newName,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(newName);
        if (Busy) throw new InvalidOperationException("Wait for the current device refresh or operation to finish.");

        var normalized = newName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Enter a name for the device.", nameof(newName));
        if (normalized.Length > 100)
            throw new ArgumentException("Device names can contain at most 100 characters.", nameof(newName));
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Device names must be a single line and cannot contain control characters.", nameof(newName));

        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            var registered = Config.Devices.FirstOrDefault(item => item.Id == device.Id)
                             ?? throw new InvalidOperationException("That device is no longer enrolled.");
            if (Config.Devices.Any(item => item.Id != registered.Id
                                           && new[] { item.Name, item.HostName, item.DnsName, item.TailscaleIp, item.TailnetDeviceId }
                                               .Any(selector => string.Equals(selector, normalized, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("Another enrolled device already uses that name or device identifier.");
            }

            var oldStoredName = registered.Name;
            var oldDisplayName = string.IsNullOrWhiteSpace(oldStoredName) ? registered.HostName : oldStoredName;
            if (string.Equals(oldStoredName, normalized, StringComparison.Ordinal)) return;

            registered.Name = normalized;
            try
            {
                await _state.SaveAsync(cancellationToken);
            }
            catch
            {
                registered.Name = oldStoredName;
                throw;
            }

            ReplaceDevices(Config.Devices);
            Status = $"Renamed {oldDisplayName} to {normalized}";
            Log($"Renamed enrolled device {oldDisplayName} to {normalized}. Its Windows hostname and Tailscale identity were not changed.");
        }
        finally
        {
            _state.InviteGate.Release();
        }
    }

    public async Task ChangeRoleAsync(DeviceRecord device, DeviceRole newRole, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (device.Role == newRole) return;
        var oldToken = GetAgentToken(device);
        if (newRole == DeviceRole.ManagedOnly)
        {
            await _headscale.SetDeviceRoleAsync(device.TailnetDeviceId, newRole, device.AdvertisesExitNode, cancellationToken);
            device.Role = newRole;
            device.PendingRoleSync = true;
            foreach (var target in Config.Devices) target.PendingCredentialRotation = true;
            await _state.SaveAsync(cancellationToken);
            try
            {
                await _agents.SetRoleAsync(device, oldToken, newRole, cancellationToken);
                device.PendingRoleSync = false;
            }
            catch (Exception exception)
            {
                Log($"Network access was revoked; local controller shortcuts will be removed when {device.Name} is online: {exception.Message}");
            }
            foreach (var target in Config.Devices.Where(target => target.State == DeviceConnectionState.Online).ToArray())
            {
                try { await RotateOneAsync(target, cancellationToken); }
                catch (Exception exception) { Log($"Credential rotation pending for {target.Name}: {exception.Message}"); }
            }
            await _state.SaveAsync(cancellationToken);
            Log($"{device.Name} is now managed-only. Controller reachability was revoked; peer credentials were rotated or queued for rotation.");
        }
        else
        {
            await _agents.SetRoleAsync(device, oldToken, newRole, cancellationToken);
            await _headscale.SetDeviceRoleAsync(device.TailnetDeviceId, newRole, device.AdvertisesExitNode, cancellationToken);
            device.Role = newRole;
            device.PendingRoleSync = false;
            await _state.SaveAsync(cancellationToken);
            Log($"{device.Name} can now open Opticon and control permitted machines.");
        }
        ReplaceDevices(Config.Devices);
    }

    public async Task RemoveDeviceAsync(DeviceRecord device, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (Busy)
        {
            throw new InvalidOperationException("Wait for the current device refresh or operation to finish.");
        }

        Busy = true;
        var displayName = string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
        Status = $"Revoking {displayName}…";
        var tailscaleDeleted = false;
        var remoteStateResolved = false;
        var registryCommitted = false;
        var removedController = false;
        var bundleDeleteFailures = new List<string>();
        DeviceRecord[] rotationTargets = [];

        try
        {
            await _state.InviteGate.WaitAsync(cancellationToken);
            try
            {
                var registered = Config.Devices.FirstOrDefault(item => item.Id == device.Id)
                                 ?? throw new InvalidOperationException("This device is no longer enrolled in Opticon.");
                if (string.IsNullOrWhiteSpace(registered.TailnetDeviceId))
                {
                    throw new InvalidOperationException(
                        "This registry entry has no Tailscale device ID, so Opticon cannot safely revoke it. Refresh the device list and try again.");
                }

                displayName = string.IsNullOrWhiteSpace(registered.Name) ? registered.HostName : registered.Name;
                removedController = registered.Role == DeviceRole.ControllerAndManaged;

                // Revoke network membership before forgetting the credentials.
                // If this fails, the device remains visible and manageable here.
                tailscaleDeleted = await _headscale.DeleteDeviceAsync(registered.TailnetDeviceId, cancellationToken);
                remoteStateResolved = true;
                Config.Devices.RemoveAll(item => item.Id == registered.Id);

                foreach (var invite in Config.Invites.Where(item => item.EnrolledDeviceId == registered.Id))
                {
                    invite.AgentTokenProtected = string.Empty;
                    invite.RustDeskPasswordProtected = string.Empty;
                    invite.ControllerTokenProtected = string.Empty;
                    if (string.IsNullOrWhiteSpace(invite.BundlePath)) continue;

                    try
                    {
                        if (File.Exists(invite.BundlePath)) File.Delete(invite.BundlePath);
                        invite.BundlePath = string.Empty;
                    }
                    catch (Exception exception)
                    {
                        bundleDeleteFailures.Add($"{invite.BundlePath}: {exception.Message}");
                    }
                }

                if (removedController)
                {
                    foreach (var peer in Config.Devices) peer.PendingCredentialRotation = true;
                    rotationTargets = Config.Devices
                        .Where(peer => peer.State == DeviceConnectionState.Online)
                        .ToArray();
                }

                // Remote deletion is irreversible, so finish the local commit even
                // if the initiating UI cancellation token changes at this point.
                await _state.SaveAsync(CancellationToken.None);
                registryCommitted = true;
            }
            finally
            {
                _state.InviteGate.Release();
            }

            foreach (var target in rotationTargets)
            {
                try
                {
                    await RotateOneAsync(target, cancellationToken);
                }
                catch (Exception exception)
                {
                    Log($"Credential rotation remains pending for {target.Name}: {exception.Message}");
                }
            }

            ReplaceDevices(Config.Devices);
            ReplaceInvites();
            foreach (var failure in bundleDeleteFailures)
            {
                Log("An associated invitation bundle could not be deleted: " + failure);
            }

            Status = bundleDeleteFailures.Count == 0
                ? $"{displayName} removed from Opticon"
                : $"{displayName} removed; delete the listed invitation bundle manually";
            Log(tailscaleDeleted
                ? $"Revoked {displayName} in Tailscale and removed its Opticon registry entry."
                : $"Tailscale already no longer listed {displayName}; removed its stale Opticon registry entry.");
            if (removedController)
            {
                Log("Peer credential rotation was requested; unreachable peers remain marked for rotation on reconnect.");
            }
        }
        catch (Exception exception) when (remoteStateResolved && !registryCommitted)
        {
            Status = $"{displayName} revoked; registry cleanup needs attention";
            throw new InvalidOperationException(
                $"{displayName} was revoked in Tailscale, but Opticon could not save the local registry cleanup. Restart Opticon and remove the stale entry again.",
                exception);
        }
        catch
        {
            Status = $"Could not remove {displayName}";
            throw;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SetExitAdvertisementAsync(DeviceRecord device, bool enabled, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        var token = GetAgentToken(device);
        if (enabled)
        {
            await _agents.SetExitNodeAsync(device, token, true, cancellationToken);
            await _headscale.SetDeviceRoleAsync(device.TailnetDeviceId, device.Role, exitNode: true, cancellationToken);
            await Task.Delay(1500, cancellationToken);
            await _headscale.ApproveExitNodeRoutesAsync(device.TailnetDeviceId, cancellationToken);
        }
        else
        {
            await _agents.SetExitNodeAsync(device, token, false, cancellationToken);
            await _headscale.SetDeviceRoleAsync(device.TailnetDeviceId, device.Role, exitNode: false, cancellationToken);
        }
        device.AdvertisesExitNode = enabled;
        device.ExitNodeApproved = enabled;
        await _state.SaveAsync(cancellationToken);
        Log($"Exit-node service {(enabled ? "enabled" : "disabled")} on {device.Name}.");
    }

    public async Task UseExitNodeAsync(DeviceRecord device, CancellationToken cancellationToken = default)
    {
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp)) throw new InvalidOperationException("The selected device has no valid Tailscale IPv4 address.");
        var tailscale = FindTailscale();
        var result = await ProcessRunner.RunAsync(tailscale,
            ["set", $"--exit-node={device.TailscaleIp}", "--exit-node-allow-lan-access=false"], TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.StandardError.Trim());
        Status = $"VPN egress: {device.Name}";
        Log($"This machine is now using {device.Name} as its internet exit node.");
    }

    public async Task StopUsingExitNodeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(FindTailscale(), ["set", "--exit-node="], TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.StandardError.Trim());
        Status = "VPN egress: local connection";
        Log("Stopped using a remote exit node.");
    }

    public async Task SetPrivacyMode2Async(DeviceRecord device, bool enabled, CancellationToken cancellationToken = default)
    {
        device.PrivacyMode2Enabled = enabled;
        Config.PrivacyMode2ByDevice[device.Id] = enabled;
        await _state.SaveAsync(cancellationToken);
        Status = $"Privacy Mode 2: {(enabled ? "on" : "off")} for {device.Name}";
        Log($"RustDesk Privacy Mode 2 will be {(enabled ? "enabled" : "disabled")} when opening {device.Name}.");
        Changed(nameof(SelectedDevice));
    }

    public async Task LaunchRemoteControlAsync(DeviceRecord device, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Config.RustDeskPath)) throw new FileNotFoundException("RustDesk was not found. Set its path in Settings.");
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp)) throw new InvalidOperationException("The selected device has no valid Tailscale address.");
        if (device.State == DeviceConnectionState.Offline)
            throw new InvalidOperationException($"{device.Name} is offline. Wake or power on the device and wait for it to reconnect to the private network.");
        var password = GetRustDeskPassword(device);
        await RustDeskSessionLauncher.LaunchAsync(
            Config.RustDeskPath,
            device.TailscaleIp,
            password,
            device.PrivacyMode2Enabled,
            cancellationToken);
        Status = $"Remote session: {device.Name}";
        Log($"Opened a private direct-IP remote session to {device.Name} through its Tailscale address.");
    }

    public async Task LaunchSshAsync(DeviceRecord device, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        RequireLiveAgent(device, "SSH");
        Status = $"Preparing protected administrative SSH on {device.Name}...";
        Log($"Preparing the target's isolated OpenSSH listener for {device.Name}; first use or repair can take up to a minute.");
        var token = GetAgentToken(device);
        var requestedLifetime = TimeSpan.FromHours(1);
        var handle = await SshSessionLauncher.LaunchAsync(
            new SshSessionLaunchOptions
            {
                ExpectedHost = device.TailscaleIp,
                RequestedLifetime = requestedLifetime
            },
            async (publicKey, lifetime, innerCancellation) =>
            {
                var requestedAt = DateTimeOffset.UtcNow;
                var response = await _agents.OpenSshAsync(device, token, new SshAccessRequest
                {
                    PublicKey = publicKey,
                    RequestedLifetimeSeconds = checked((int)lifetime.TotalSeconds),
                    ExpiresAt = requestedAt.Add(lifetime)
                }, innerCancellation);
                return response;
            },
            (sessionId, innerCancellation) => _agents.RevokeSshAsync(device, token, sessionId, innerCancellation),
            cancellationToken);

        lock (_sshSessions) _sshSessions.Add(handle);
        _ = ObserveSshSessionAsync(device.Name, handle);
        Status = $"Administrative SSH: {device.Name}";
        Log($"Opened a host-key-pinned, one-hour administrative SSH lease to {device.Name}. The target revokes it when ssh.exe exits or the independent expiry deadline is reached.");
    }

    public async Task<OpticonUpdateRelease?> FindUpdateAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        RequireLiveAgent(device, "remote update");
        Status = $"Checking signed Opticon Agent releases for {device.Name}...";
        return await _releases.FindUpdateAsync(Config, device, cancellationToken);
    }

    public async Task<bool> SnapshotMaintenanceSshAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        RequireLiveAgent(device, "maintenance recovery snapshot");
        var listening = await AgentClient.ProbeTcpAsync(
            device.TailscaleIp, RemoteAdministrationProtocol.SshPort,
            TimeSpan.FromSeconds(5), cancellationToken);
        Log(listening
            ? $"Snapshotted the administrative SSH listener on {device.Name}; the replacement must preserve it."
            : $"No administrative SSH listener was active on {device.Name} immediately before maintenance.");
        return listening;
    }

    public async Task<UpdateStatusDto> ObserveMaintenanceBootstrapAsync(
        DeviceRecord device,
        OpticonUpdateRelease release,
        Guid operationId,
        bool sshWasListening,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        RequireLiveAgent(device, "maintenance confirmation");
        if (!await _updateGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another guarded device update is already running from this command center.");
        Busy = true;
        UpdateProgressLines.Clear();
        try
        {
            var progress = new Progress<string>(message =>
            {
                Status = message;
                UpdateProgressLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                Log(message);
            });
            var result = await _deviceUpdates.ObserveMaintenanceBootstrapAsync(
                device, GetAgentToken(device), release, operationId, sshWasListening,
                progress, cancellationToken);
            device.UpdateStatus = result;
            if (result.Phase == UpdatePhase.Committed) device.AgentVersion = result.TargetVersion;
            Status = result.Phase switch
            {
                UpdatePhase.Committed => $"Opticon Agent {result.TargetVersion} committed on {device.Name}",
                UpdatePhase.RolledBack => $"{device.Name} safely rolled back to Opticon {result.CurrentVersion}",
                _ => $"Maintenance on {device.Name}: {result.Phase}"
            };
            await _state.SaveAsync(cancellationToken);
            return result;
        }
        finally
        {
            Busy = false;
            _updateGate.Release();
        }
    }

    public async Task<UpdateStatusDto> UpdateDeviceAsync(
        DeviceRecord device,
        OpticonUpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        RequireLiveAgent(device, "remote update");
        if (!await _updateGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another guarded device update is already running from this command center.");
        Busy = true;
        UpdateProgressLines.Clear();
        try
        {
            var progress = new Progress<string>(message =>
            {
                Status = message;
                UpdateProgressLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                Log(message);
            });
            var result = await _deviceUpdates.UpdateAsync(
                device, GetAgentToken(device), release, progress, cancellationToken);
            device.UpdateStatus = result;
            if (result.Phase == UpdatePhase.Committed) device.AgentVersion = result.TargetVersion;
            Status = result.Phase switch
            {
                UpdatePhase.Committed => $"Opticon Agent {result.TargetVersion} committed on {device.Name}",
                UpdatePhase.RolledBack => $"{device.Name} safely rolled back to Opticon {result.CurrentVersion}",
                _ => $"Update on {device.Name}: {result.Phase}"
            };
            await _state.SaveAsync(cancellationToken);
            return result;
        }
        finally
        {
            Busy = false;
            _updateGate.Release();
        }
    }

    public async Task<InviteBundleResult> CreateInviteAsync(
        string deviceName,
        DeviceRole role,
        bool exitNode,
        IReadOnlyCollection<string> allowedRoots,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        var progress = new Progress<string>(message => Status = message);
        var result = await new InviteBundleService(_state, _headscale).CreateAsync(
            deviceName,
            role,
            exitNode,
            allowedRoots,
            progress,
            cancellationToken);
        ReplaceInvites();
        Log($"Created single-use invite for {deviceName} with {allowedRoots.Count} shared folder(s). It expires in {InvitationPolicy.DefaultLifetimeDays} days.");
        Status = $"Invitation ready: {deviceName}";
        return result;
    }

    public async Task RefreshInvitationsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPrimary)
        {
            ReplaceInvites();
            return;
        }
        if (!await _invitationInventoryGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            Status = "Reading active invitation links from the Opticon gateway…";
            await LoadHostedInvitationInventoryAsync(cancellationToken);
            Status = $"{_remoteInvitations.Count} active gateway invitation link(s)";
        }
        finally
        {
            _invitationInventoryGate.Release();
        }
    }

    public async Task ExtendInviteAsync(InviteRecord invite, int additionalDays, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (invite.IsRemoteOrphan)
            throw new InvalidOperationException("A gateway-only invitation cannot be extended because this Command Center does not have its private URL. Expire it and create a new invitation.");
        await _state.InviteGate.WaitAsync(cancellationToken);
        bool oldKeyRevoked;
        try
        {
            var progress = new Progress<string>(message => Status = message);
            oldKeyRevoked = await new InviteBundleService(_state, _headscale).ExtendAsync(invite, additionalDays, progress, cancellationToken);
        }
        finally { _state.InviteGate.Release(); }
        ReplaceInvites();
        Status = $"Invitation extended: {invite.DeviceName}";
        Log($"Extended the single-use invitation for {invite.DeviceName} by {additionalDays} day(s), through {invite.ExpiresAt.LocalDateTime:g}.");
        if (!oldKeyRevoked) Log("The replacement link is active; the superseded Headscale key is durably queued for revocation and will be retried automatically.");
    }
    public async Task CancelInviteAsync(InviteRecord invite, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (invite.IsRemoteOrphan)
        {
            if (string.IsNullOrWhiteSpace(invite.HostedInviteIdHash))
                throw new InvalidDataException("The gateway invitation has no safe identity.");
            await new HostedInviteClient(_state).DeleteAsync(invite.HostedInviteIdHash, cancellationToken);
            await LoadHostedInvitationInventoryAsync(cancellationToken);
            Log(invite.CanRevokeRemoteNetworkKey
                ? $"Expired the gateway-only invitation for {invite.DeviceName}; its hosted link and network key are disabled."
                : $"Removed the gateway-only hosted link for {invite.DeviceName}. Its legacy network key identity was unavailable and may remain usable until {invite.ExpiresAt.LocalDateTime:g}.");
            Status = $"Invitation expired: {invite.DeviceName}";
            return;
        }
        var hostedIdHash = invite.HostedInviteIdHash;
        var result = await new InviteBundleService(_state, _headscale).CancelAsync(invite, cancellationToken);
        if (result.HostedLinkRemoved && !string.IsNullOrWhiteSpace(hostedIdHash))
            _remoteInvitations.RemoveAll(remote =>
                string.Equals(remote.IdHash, hostedIdHash, StringComparison.OrdinalIgnoreCase));
        ReplaceInvites();
        Log(result.HostedLinkRemoved && result.LegacyBundleDeleted
            ? $"Canceled the invitation for {invite.DeviceName}; its link and network key are disabled."
            : $"Canceled the network key for {invite.DeviceName}; retry cancellation to finish any pending cleanup.");
    }

    /// <summary>
    /// Refreshes the live release manifest and the gateway's authoritative
    /// invitation-removal plan. This is read-only; it is deliberately separate
    /// from the deploy action so the UI can show the currently deployed files
    /// and explain an already-matching version before the operator clicks it.
    /// </summary>
    public async Task RefreshReleaseDeploymentAsync(CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (!await _releaseDeploymentGate.WaitAsync(0, cancellationToken)) return;
        ReleaseDeploymentBusy = true;
        try
        {
            Status = "Reading the live Opticon invite release…";
            var preflight = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
            ApplyReleasePreflight(preflight);
            if (preflight.AlreadyDeployed)
            {
                var recovery = _releaseDeployment.TryGetLeaseRecovery(OpticonVersion);
                if (recovery is not null)
                    await _releaseDeployment.FinalizeLeaseAsync(OpticonVersion, recovery, cancellationToken);
                await _releaseDeployment.ClearLeaseRecoveryAsync(cancellationToken);
                _releaseDeploymentCanResume = false;
                Changed(nameof(CanDeployRelease));
                Changed(nameof(CanResumeReleaseDeployment));
                Changed(nameof(DeployReleaseButtonText));
            }
            Status = preflight.AlreadyDeployed
                ? $"Invite release {preflight.DeployedVersion} already matches this Command Center"
                : $"Invite release {preflight.DeployedVersion} is currently deployed";
        }
        catch
        {
            ClearReleaseDeploymentState("Live invite release state could not be read.");
            throw;
        }
        finally
        {
            ReleaseDeploymentBusy = false;
            _releaseDeploymentGate.Release();
        }
    }

    /// <summary>
    /// Obtains a fresh, gateway-authoritative, no-side-effect deployment plan.
    /// The caller can therefore ask for the active-invitation decision as soon
    /// as the Deploy button is clicked; publisher readiness is verified after
    /// Yes but before the deployment lease or any remote mutation.
    /// </summary>
    public async Task<ReleaseDeploymentPreflight> PrepareReleaseDeploymentAsync(
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        if (!await _releaseDeploymentGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another release deployment operation is already in progress.");
        ReleaseDeploymentBusy = true;
        try
        {
            Status = "Preparing the invite release deployment…";
            var preflight = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
            ApplyReleasePreflight(preflight);
            if (preflight.AlreadyDeployed)
            {
                var recovery = _releaseDeployment.TryGetLeaseRecovery(OpticonVersion);
                if (recovery is not null)
                    await _releaseDeployment.FinalizeLeaseAsync(OpticonVersion, recovery, cancellationToken);
                await _releaseDeployment.ClearLeaseRecoveryAsync(cancellationToken);
                _releaseDeploymentCanResume = false;
                return preflight;
            }
            if (preflight.TargetIsOlder)
                throw new InvalidOperationException(
                    $"The deployed invite release {preflight.DeployedVersion} is newer than this Command Center ({OpticonVersion}). A downgrade is refused.");
            if (preflight.DeploymentBlocked && !_releaseDeploymentCanResume)
                throw new InvalidOperationException(preflight.DeploymentBlockedReason);
            return preflight;
        }
        catch
        {
            if (_releasePreflight is null) ClearReleaseDeploymentState("Live invite release state could not be read.");
            throw;
        }
        finally
        {
            ReleaseDeploymentBusy = false;
            _releaseDeploymentGate.Release();
        }
    }

    /// <summary>
    /// Executes the mutation phase only after MainWindow obtained an explicit
    /// Yes. It re-reads the plan, binds cancellation to the original gateway
    /// revision, rechecks afterward, then starts the existing S3 publisher.
    /// </summary>
    public async Task DeployReleaseAsync(
        ReleaseDeploymentPreflight confirmedPlan,
        CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        ArgumentNullException.ThrowIfNull(confirmedPlan);
        if (!string.Equals(confirmedPlan.TargetVersion, OpticonVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The release confirmation does not match this Command Center version. Refresh and try again.");
        if (!await _releaseDeploymentGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another release deployment operation is already in progress.");
        ReleaseDeploymentBusy = true;
        Busy = true;
        ReleaseDeploymentLease? lease = null;
        var cancellationCompleted = false;
        var manifestCommitted = false;
        try
        {
            // The UI has already completed its read-only plan and Yes/No
            // decision. Repeat readiness immediately before acquiring the
            // lease so revoked invitations are never the first evidence of a
            // missing signer, AWS identity, git sync, or gateway credential.
            var prerequisites = await _releaseDeployment.ResolvePublisherPrerequisitesAsync(OpticonVersion, cancellationToken);
            await _releaseDeployment.VerifyPublisherReadinessAsync(OpticonVersion, prerequisites, cancellationToken);

            var preflight = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
            ApplyReleasePreflight(preflight);
            if (preflight.AlreadyDeployed)
            {
                Status = $"Invite release {preflight.DeployedVersion} already matches this Command Center";
                return;
            }
            if (preflight.TargetIsOlder)
                throw new InvalidOperationException("The live invite release is newer than this Command Center. A downgrade is refused.");

            var recovery = _releaseDeployment.TryGetLeaseRecovery(OpticonVersion);
            var resuming = ReleaseDeploymentService.RecoveryMatchesLiveLease(preflight, recovery);
            if (preflight.DeploymentBlocked && !resuming)
                throw new InvalidOperationException(preflight.DeploymentBlockedReason);

            if (resuming)
            {
                lease = recovery!;
                Status = "Resuming the confirmed Opticon release deployment…";
            }
            else
            {
                if (confirmedPlan.DeploymentBlocked
                    || !string.Equals(preflight.DeploymentRevision, confirmedPlan.DeploymentRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active invitation state changed after confirmation. Review the refreshed plan and click Deploy again; no invitation was removed.");

                // This phase can take time and may expose environmental release
                // problems (signing, S3, CloudFront) but it leaves the current
                // live manifest and invitations untouched. Never acquire the
                // cancellation lease until the immutable archive is verified.
                var stageProgress = new Progress<string>(message => Status = message);
                await _releaseDeployment.StageAsync(OpticonVersion, prerequisites, stageProgress, cancellationToken);

                preflight = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
                ApplyReleasePreflight(preflight);
                if (preflight.AlreadyDeployed)
                {
                    Status = $"Invite release {preflight.DeployedVersion} already matches this Command Center";
                    return;
                }
                if (preflight.TargetIsOlder)
                    throw new InvalidOperationException("The live invite release is newer than this Command Center. A downgrade is refused.");
                if (preflight.DeploymentBlocked
                    || !string.Equals(preflight.DeploymentRevision, confirmedPlan.DeploymentRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active invitation state changed while the replacement archive was staged. Review the refreshed plan and click Deploy again; no invitation was removed.");

                // Persist a caller-generated opaque token before POSTing it.
                // If the gateway acquires the lease but its response is lost,
                // this exact token makes the acquire idempotently recoverable.
                var candidate = ReleaseDeploymentService.CreateLeaseCandidate(preflight);
                await _releaseDeployment.SaveLeaseRecoveryAsync(preflight, candidate, cancellationToken);
                lease = await _releaseDeployment.AcquireLeaseAsync(preflight, candidate.LeaseToken, cancellationToken);
                await _releaseDeployment.SaveLeaseRecoveryAsync(preflight, lease, cancellationToken);
            }

            // A resumed lease may already have deleted the hosted records and
            // journaled the full removal result. Calling the idempotent gateway
            // operation retrieves that result for local cleanup.
            if (preflight.RequiresInvitationRemoval || resuming)
            {
                Status = $"Revoking {preflight.BlockingInvitations.Count} active invitation(s)…";
                var cancellation = await _releaseDeployment.RevokeActiveInvitationsAsync(preflight, lease, cancellationToken);
                await MarkLocallyRemovedInvitationsAsync(cancellation.RemovedInviteIds, cancellationToken);
                cancellationCompleted = cancellation.RemovedInviteIds.Count != 0;

                preflight = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
                ApplyReleasePreflight(preflight);
                if (preflight.RequiresInvitationRemoval)
                    throw new InvalidOperationException(
                        "An active invitation remains after the cancellation attempt. Review the refreshed release plan before publishing.");
            }

            var progress = new Progress<string>(message => Status = message);
            await _releaseDeployment.PublishAsync(OpticonVersion, prerequisites, lease, progress, cancellationToken);
            var verified = await _releaseDeployment.PrepareAsync(OpticonVersion, cancellationToken);
            ApplyReleasePreflight(verified);
            if (!verified.AlreadyDeployed)
                throw new InvalidOperationException(
                    "The publisher completed, but the live invite manifest does not contain the exact Command Center source release.");
            await _releaseDeployment.FinalizeLeaseAsync(OpticonVersion, lease, cancellationToken);
            await _releaseDeployment.ClearLeaseRecoveryAsync(cancellationToken);
            manifestCommitted = true;
            Status = $"Invite release {verified.DeployedVersion} is deployed and ready for new invitations";
            Log($"Published verified invite source release {verified.DeployedVersion}; the live manifest and CloudFront artifact were rechecked.");
        }
        finally
        {
            if (lease is not null && !manifestCommitted && !cancellationCompleted)
            {
                try
                {
                    await _releaseDeployment.ReleaseLeaseAsync(lease, CancellationToken.None);
                    await _releaseDeployment.ClearLeaseRecoveryAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    Log($"Could not release the uncommitted Opticon deployment lease; it will expire safely: {exception.Message}");
                }
            }
            Busy = false;
            ReleaseDeploymentBusy = false;
            _releaseDeploymentGate.Release();
        }
    }

    public async Task SavePrimarySettingsAsync(string headscaleApiUrl, string headscaleUserId, string? apiKey, string bindAddress,
        string inviteDirectory, string rustDeskPath, CancellationToken cancellationToken = default)
    {
        if (Config.Mode != AdminMode.Primary) throw new InvalidOperationException("This controller receives settings from the command center.");
        if (!AgentClient.IsTailscaleIp(bindAddress)) throw new InvalidOperationException("Coordinator address must be this laptop's 100.x Tailscale IPv4 address.");
        if (!Uri.TryCreate(headscaleApiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Enter the HTTPS address of your self-hosted Headscale API.");
        if (string.IsNullOrWhiteSpace(headscaleUserId)) throw new InvalidOperationException("Enter the Headscale user ID used for Opticon device keys.");
        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(Config.HeadscaleApiKeyProtected)) throw new InvalidOperationException("Enter the Opticon admin signing secret.");
        var privateInviteDirectory = PrivateStorage.ValidateInviteDirectory(inviteDirectory);
        if (string.IsNullOrWhiteSpace(rustDeskPath)) throw new InvalidOperationException("Choose the RustDesk executable path.");
        Config.HeadscaleApiUrl = apiUri.AbsoluteUri.TrimEnd('/') + "/";
        Config.HeadscaleControlUrl = apiUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        Config.HeadscaleUserId = headscaleUserId.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey)) Config.HeadscaleApiKeyProtected = SecretProtector.Protect(apiKey.Trim());
        Config.CoordinatorBindAddress = bindAddress.Trim();
        Config.CoordinatorUrl = $"http://{Config.CoordinatorBindAddress}:{Config.CoordinatorPort}";
        Config.InviteOutputDirectory = privateInviteDirectory;
        Config.RustDeskPath = Environment.ExpandEnvironmentVariables(rustDeskPath.Trim());
        Config.SetupComplete = true;
        Directory.CreateDirectory(Config.InviteOutputDirectory);
        await _state.SaveAsync(cancellationToken);
        Log("Command-center settings saved.");
    }

    public async Task ApplyTailnetPolicyAsync(CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        Log("Headscale policy changes are intentionally manual and remain under your server control.");
        throw new InvalidOperationException("Install config/headscale-policy.hujson on your Headscale server. Opticon never replaces a server policy remotely.");
    }

    public async Task TagThisMachineAsHubAsync(CancellationToken cancellationToken = default)
    {
        var address = await ProcessRunner.RunAsync(FindTailscale(), ["ip", "-4"], TimeSpan.FromSeconds(15), cancellationToken);
        if (!address.Succeeded) throw new InvalidOperationException(address.StandardError.Trim());
        var ip = address.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var node = (await _headscale.GetDevicesAsync(cancellationToken)).SingleOrDefault(item => item.Ip == ip) ?? throw new InvalidOperationException("This laptop was not found in Headscale inventory. Verify the API address and key.");
        await _headscale.SetTagsAsync(node.Id, ["tag:taildesk-hub"], cancellationToken);
        Log("This machine is tagged tag:taildesk-hub in Headscale.");
    }

    public async Task TestTailscaleApiAsync(CancellationToken cancellationToken = default)
    {
        await _headscale.TestAsync(cancellationToken);
        Log("The private Opticon control API signature is working.");
    }

    public void Log(string message)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            LogLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    private async Task RefreshPrimaryTailnetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HeadscaleDeviceInfo> tailnet = [];
        try { tailnet = await _headscale.GetDevicesAsync(cancellationToken); }
        catch (Exception exception) { Log("Tailscale inventory unavailable: " + exception.Message); }

        foreach (var device in Config.Devices)
        {
            var found = tailnet.FirstOrDefault(item => item.Id == device.TailnetDeviceId)
                        ?? tailnet.FirstOrDefault(item => item.Ip == device.TailscaleIp);
            if (found is not null)
            {
                device.TailscaleIp = found.Ip;
                device.DnsName = found.DnsName;
                device.OperatingSystem = string.IsNullOrWhiteSpace(device.OperatingSystem) ? found.OperatingSystem : device.OperatingSystem;
                device.LastSeen = found.LastSeen;
                device.State = found.Online ? DeviceConnectionState.TailscaleOnly : DeviceConnectionState.Offline;
            }
        }
        ReplaceDevices(Config.Devices);
    }

    private async Task SyncSecondaryAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Config.CoordinatorUrl, UriKind.Absolute, out var coordinator)
            || !AgentClient.IsTailscaleIp(coordinator.Host))
        {
            throw new InvalidOperationException("The coordinator URL must use its Tailscale IPv4 address.");
        }
        using var client = DirectHttp.CreateClient(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(coordinator, "/api/v1/registry"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SecretProtector.Unprotect(Config.ControllerTokenProtected));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<RegistrySnapshot>(JsonDefaults.Options, cancellationToken)
                       ?? throw new InvalidDataException("The command center returned an empty registry.");
        var devices = snapshot.Devices.Select(item => new DeviceRecord
        {
            Id = item.Id,
            Name = item.Name,
            HostName = item.HostName,
            DnsName = item.DnsName,
            TailscaleIp = item.TailscaleIp,
            OperatingSystem = item.OperatingSystem,
            AgentTokenProtected = string.Empty,
            RustDeskPasswordProtected = string.Empty,
            Role = item.Role,
            LastSeen = item.LastSeen,
            State = DeviceConnectionState.TailscaleOnly
        }).ToList();
        ReplaceDevices(devices);
    }

    private async Task ProbeAgentAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        try
        {
            DeviceStatusDto status;
            try
            {
                status = await _agents.GetStatusAsync(device, GetAgentToken(device), cancellationToken);
            }
            catch (Exception activeCredentialError) when (
                activeCredentialError is not OperationCanceledException
                && device.PendingCredentialRotationId.HasValue
                && !string.IsNullOrWhiteSpace(device.PendingAgentTokenProtected))
            {
                try
                {
                    status = await _agents.GetStatusAsync(
                        device,
                        SecretProtector.Unprotect(device.PendingAgentTokenProtected),
                        cancellationToken);
                }
                catch (Exception pendingCredentialError) when (pendingCredentialError is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Neither the active nor pending credential could authenticate. Active: {activeCredentialError.Message} Pending: {pendingCredentialError.Message}",
                        pendingCredentialError);
                }
            }
            device.State = DeviceConnectionState.Online;
            device.LastSeen = DateTimeOffset.UtcNow;
            device.HostName = status.HostName;
            device.OperatingSystem = status.OperatingSystem;
            device.Architecture = status.Architecture;
            device.AgentVersion = status.AgentVersion;
            device.GuardianVersion = status.GuardianVersion;
            device.UpdateProtocolVersion = status.UpdateProtocolVersion;
            device.AdvertisesExitNode = status.AdvertisesExitNode;
            device.RustDeskReady = status.RustDeskReady;
            device.SshReady = status.SshReady;
            device.SshPort = status.SshPort;
            device.UpdateStatus = status.UpdateStatus;
            device.OnlineSince = ToOnlineSince(status.OnlineDurationSeconds);
            device.BatteryPercentage = status.BatteryPercentage;
            if (AgentClient.IsTailscaleIp(status.TailscaleIp)) device.TailscaleIp = status.TailscaleIp;
        }
        catch
        {
            device.State = device.State == DeviceConnectionState.TailscaleOnly ? DeviceConnectionState.TailscaleOnly : DeviceConnectionState.Offline;
            device.OnlineSince = null;
        }
    }

    private async Task RotateOneAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var oldToken = GetAgentToken(device);
        if (!device.PendingCredentialRotationId.HasValue)
        {
            device.PendingCredentialRotationId = Guid.NewGuid();
            device.PendingAgentTokenProtected = SecretProtector.Protect(SecurityHelpers.CreateToken());
            device.PendingRustDeskPasswordProtected = SecretProtector.Protect(SecurityHelpers.CreateHumanPassword());
            device.PendingCredentialRotation = true;
            await _state.SaveAsync(CancellationToken.None);
        }

        var operationId = device.PendingCredentialRotationId.Value;
        var newToken = SecretProtector.Unprotect(device.PendingAgentTokenProtected);
        var newPassword = SecretProtector.Unprotect(device.PendingRustDeskPasswordProtected);
        try
        {
            await _agents.RotateCredentialsAsync(
                device, oldToken, operationId, newToken, newPassword, cancellationToken);
        }
        catch (Exception oldCredentialError) when (oldToken != newToken && oldCredentialError is not OperationCanceledException)
        {
            try
            {
                await _agents.RotateCredentialsAsync(
                    device, newToken, operationId, newToken, newPassword, cancellationToken);
            }
            catch (Exception newCredentialError) when (newCredentialError is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Credential rotation could not be confirmed with either the prior or pending credential. Prior: {oldCredentialError.Message} Pending: {newCredentialError.Message}",
                    newCredentialError);
            }
        }

        device.AgentTokenProtected = SecretProtector.Protect(newToken);
        device.RustDeskPasswordProtected = SecretProtector.Protect(newPassword);
        await _state.SaveAsync(CancellationToken.None);

        await _agents.CommitCredentialRotationAsync(device, newToken, operationId, cancellationToken);
        device.PendingCredentialRotation = false;
        device.PendingCredentialRotationId = null;
        device.PendingAgentTokenProtected = string.Empty;
        device.PendingRustDeskPasswordProtected = string.Empty;
        await _state.SaveAsync(CancellationToken.None);
    }

    private static DateTimeOffset? ToOnlineSince(long? durationSeconds)
    {
        if (durationSeconds is not long seconds || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) return null;
        return DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(seconds));
    }

    private async Task CleanupInactiveHostedInvitationsAsync(CancellationToken cancellationToken)
    {
        var pending = Config.Invites
            .Where(invite => (invite.PendingTailscaleKeyRevocations?.Count ?? 0) > 0
                             || (!string.IsNullOrWhiteSpace(invite.HostedInviteIdHash)
                                 && (invite.RedeemedAt.HasValue || invite.IsExpired)))
            .ToArray();
        if (pending.Length == 0) return;
        var changed = false;
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var invite in pending)
            {
                var pendingCount = invite.PendingTailscaleKeyRevocations?.Count ?? 0;
                try
                {
                    var allRevoked = await new InviteBundleService(_state, _headscale)
                        .RevokePendingKeysAsync(invite, cancellationToken);
                    changed |= pendingCount != (invite.PendingTailscaleKeyRevocations?.Count ?? 0);
                    if (!allRevoked) Log($"Superseded key cleanup pending for {invite.DeviceName}.");
                }
                catch (Exception exception)
                {
                    Log($"Superseded key cleanup pending for {invite.DeviceName}: {exception.Message}");
                }

                if (string.IsNullOrWhiteSpace(invite.HostedInviteIdHash)
                    || (!invite.RedeemedAt.HasValue && !invite.IsExpired))
                    continue;
                if (!invite.RedeemedAt.HasValue)
                {
                    try { await _headscale.RevokeKeyAsync(invite.TailscaleKeyId, cancellationToken); }
                    catch (Exception exception) { Log($"Expired key cleanup pending for {invite.DeviceName}: {exception.Message}"); }
                }
                try
                {
                    await new HostedInviteClient(_state).DeleteAsync(invite.HostedInviteIdHash, cancellationToken);
                    invite.HostedInviteIdHash = string.Empty;
                    invite.HostedUrlProtected = string.Empty;
                    changed = true;
                }
                catch (Exception exception) { Log($"Hosted invitation cleanup pending for {invite.DeviceName}: {exception.Message}"); }
            }
            if (changed) await _state.SaveAsync(cancellationToken);
        }
        finally { _state.InviteGate.Release(); }
        if (changed) ReplaceInvites();
    }

    private void ApplyReleasePreflight(ReleaseDeploymentPreflight preflight)
    {
        _releasePreflight = preflight;
        _releaseDeploymentCanResume = ReleaseDeploymentService.RecoveryMatchesLiveLease(
            preflight,
            _releaseDeployment.TryGetLeaseRecovery(OpticonVersion));
        DeployedReleaseArtifacts.Clear();
        foreach (var artifact in ReleaseDeploymentService.ToArtifactRows(preflight.Manifest))
            DeployedReleaseArtifacts.Add(artifact);
        DeployedReleaseVersion = preflight.DeployedVersion;
        ReleaseDeploymentStatus = preflight.AlreadyDeployed
            ? $"Deployed version {preflight.DeployedVersion} already matches this Command Center."
            : preflight.TargetIsOlder
                ? $"Deployed version {preflight.DeployedVersion} is newer than this Command Center; deployment is blocked."
                : preflight.DeploymentBlocked
                    ? _releaseDeploymentCanResume
                        ? $"A confirmed Opticon deployment is paused until {preflight.LeaseExpiresAt.LocalDateTime:g}; click Deploy to resume it."
                        : preflight.DeploymentBlockedReason
                : preflight.RequiresInvitationRemoval
                    ? preflight.BlockingInvitations.Any(item => !item.CanRevoke)
                        ? $"Deployment requires removing {preflight.BlockingInvitations.Count} active invitation(s); legacy hosted links can be abandoned after confirmation."
                        : $"Deployment requires removing {preflight.BlockingInvitations.Count} active invitation(s) before the manifest can change."
                    : $"Deployed version {preflight.DeployedVersion}; {OpticonVersion} is ready to publish for new invitations.";
        Changed(nameof(CanDeployRelease));
        Changed(nameof(CanResumeReleaseDeployment));
        Changed(nameof(DeployReleaseButtonText));
    }

    private void ClearReleaseDeploymentState(string status)
    {
        _releasePreflight = null;
        _releaseDeploymentCanResume = false;
        DeployedReleaseArtifacts.Clear();
        DeployedReleaseVersion = "Unavailable";
        ReleaseDeploymentStatus = status;
        Changed(nameof(CanDeployRelease));
        Changed(nameof(CanResumeReleaseDeployment));
        Changed(nameof(DeployReleaseButtonText));
    }

    private async Task MarkLocallyRemovedInvitationsAsync(
        IEnumerable<string> removedInviteIds,
        CancellationToken cancellationToken)
    {
        var removed = removedInviteIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var invite in Config.Invites.Where(invite => removed.Contains(invite.HostedInviteIdHash)))
        {
            // The gateway has already revoked the exact network key before
            // removing its hosted link. Keep any legacy local file untouched,
            // but ensure this command center never presents the link as active.
            invite.ExpiresAt = now;
            invite.HostedInviteIdHash = string.Empty;
            invite.HostedUrlProtected = string.Empty;
            changed = true;
        }
        if (!changed) return;
        await _state.SaveAsync(cancellationToken);
        ReplaceInvites();
    }

    private void ReplaceDevices(IEnumerable<DeviceRecord> devices)
    {
        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in devices.OrderByDescending(device => device.State == DeviceConnectionState.Online).ThenBy(device => device.Name))
        {
            // Virtual-display privacy is opt-in: it requires a compatible RustDesk
            // virtual display and otherwise produces a "No virtual displays" session.
            device.PrivacyMode2Enabled = Config.PrivacyMode2ByDevice.TryGetValue(device.Id, out var enabled) && enabled;
            Devices.Add(device);
        }
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == selectedId) ?? Devices.FirstOrDefault();
    }

    private void ReplaceInvites()
    {
        var localActiveHashes = Config.Invites
            .Where(invite => !invite.IsExpired && !string.IsNullOrWhiteSpace(invite.HostedInviteIdHash))
            .Select(invite => invite.HostedInviteIdHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combined = new List<InviteRecord>(Config.Invites.Count + _remoteInvitations.Count);
        foreach (var invite in Config.Invites)
        {
            invite.IsRemoteOrphan = false;
            invite.CanRevokeRemoteNetworkKey = false;
            combined.Add(invite);
        }
        foreach (var remote in _remoteInvitations.Where(remote =>
                     !string.IsNullOrWhiteSpace(remote.IdHash) && !localActiveHashes.Contains(remote.IdHash)))
        {
            combined.Add(new InviteRecord
            {
                Id = Guid.Empty,
                DeviceName = remote.DeviceName,
                Role = Enum.TryParse<DeviceRole>(remote.Role, out var role) ? role : DeviceRole.ManagedOnly,
                CreatedAt = remote.CreatedAt,
                ExpiresAt = remote.ExpiresAt,
                HostedInviteIdHash = remote.IdHash,
                ReleaseVersion = remote.ReleaseVersion,
                SourceFile = remote.SourceFile,
                InstallProtocol = remote.InstallProtocol,
                IsRemoteOrphan = true,
                CanRevokeRemoteNetworkKey = remote.CanRevoke
            });
        }
        Invites.Clear();
        foreach (var invite in combined.OrderByDescending(invite => invite.CreatedAt)) Invites.Add(invite);
    }

    private async Task LoadHostedInvitationInventoryAsync(CancellationToken cancellationToken)
    {
        _remoteInvitations = [.. await new HostedInviteClient(_state).GetActiveInvitationsAsync(cancellationToken)];
        ReplaceInvites();
    }

    private async Task ObserveSshSessionAsync(string deviceName, SshSessionHandle handle)
    {
        try
        {
            var exitCode = await handle.Completion;
            Log($"Administrative SSH to {deviceName} closed (ssh.exe exit {exitCode}).");
            if (handle.RemoteRevocationError is not null)
                Log($"Immediate SSH revocation could not be confirmed for {deviceName}; its independent lease expiry remains in force: {handle.RemoteRevocationError.Message}");
            if (handle.LocalCleanupError is not null)
                Log($"The ephemeral local SSH key directory for {deviceName} could not be removed and will be retried as stale data on the next SSH launch: {handle.LocalCleanupError.Message}");
        }
        catch (Exception exception)
        {
            Log($"SSH session monitor for {deviceName} ended unexpectedly: {exception.Message}");
        }
        finally
        {
            lock (_sshSessions) _sshSessions.Remove(handle);
        }
    }

    private static void RequireLiveAgent(DeviceRecord device, string operation)
    {
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp))
            throw new InvalidOperationException("The selected device has no valid Tailscale IPv4 address.");
        if (device.State != DeviceConnectionState.Online)
            throw new InvalidOperationException($"{operation} requires a live Opticon Agent on {device.Name}; Tailscale-only or offline state is not sufficient.");
    }

    private void RequirePrimary()
    {
        if (Config.Mode != AdminMode.Primary) throw new InvalidOperationException("Only the primary command center can change network permissions.");
    }

    private static string FindTailscale()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Tailscale is not installed.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
