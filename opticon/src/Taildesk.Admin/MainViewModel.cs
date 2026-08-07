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
    private readonly OpticonReleaseClient _releases = new();
    private readonly RemoteDeviceUpdateCoordinator _deviceUpdates;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly HashSet<SshSessionHandle> _sshSessions = [];
    private DeviceRecord? _selectedDevice;
    private string _status = "Ready";
    private bool _busy;
    private bool _checksRunning;
    private string _checksSummary = "Checks have not run yet";
    private string _checksLastRun = "Not yet run";
    private readonly SemaphoreSlim _checksGate = new(1, 1);

    public MainViewModel(AdminState state, HeadscaleApiClient headscale, AgentClient agents, TransferManager transfers)
    {
        _state = state;
        _headscale = headscale;
        _agents = agents;
        _transfers = transfers;
        _deviceUpdates = new RemoteDeviceUpdateCoordinator(agents);
        Transfers = transfers.Items;
    }

    public ObservableCollection<DeviceRecord> Devices { get; } = [];
    public ObservableCollection<InviteRecord> Invites { get; } = [];
    public ObservableCollection<TransferRow> Transfers { get; }
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> UpdateProgressLines { get; } = [];
    public ObservableCollection<SystemCheckResult> SystemChecks { get; } = [];
    public AdminConfig Config => _state.Config;
    public bool IsPrimary => Config.Mode == AdminMode.Primary;
    public DeviceRecord? SelectedDevice { get => _selectedDevice; set { _selectedDevice = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
    public bool Busy { get => _busy; set { _busy = value; Changed(); } }
    public bool ChecksRunning { get => _checksRunning; private set { _checksRunning = value; Changed(); Changed(nameof(CanRunSystemChecks)); } }
    public bool CanRunSystemChecks => !ChecksRunning;
    public string ChecksSummary { get => _checksSummary; private set { _checksSummary = value; Changed(); } }
    public string ChecksLastRun { get => _checksLastRun; private set { _checksLastRun = value; Changed(); } }

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
            await _headscale.ApproveAdvertisedRoutesAsync(device.TailnetDeviceId, cancellationToken);
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
                    ExpiresAt = requestedAt.Add(lifetime)
                }, innerCancellation);
                if (response.ExpiresAt > requestedAt.Add(lifetime).AddSeconds(10))
                    throw new InvalidDataException("The target granted a longer SSH lease than requested.");
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

    public async Task ExtendInviteAsync(InviteRecord invite, int additionalDays, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
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
        if (!oldKeyRevoked) Log("The replacement link is active, but retry expiration of the superseded Headscale key from Fly diagnostics.");
    }
    public async Task CancelInviteAsync(InviteRecord invite, CancellationToken cancellationToken = default)
    {
        RequirePrimary();
        var bundlePath = string.Empty;
        var hostedRemoved = true;
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            if (invite.RedeemedAt.HasValue) throw new InvalidOperationException("A redeemed invitation cannot be canceled; change or remove the enrolled device instead.");
            await _headscale.RevokeKeyAsync(invite.TailscaleKeyId, cancellationToken);
            if (invite.RedeemedAt.HasValue) throw new InvalidOperationException("The device completed enrollment before cancellation; the invitation was not canceled.");
            if (!string.IsNullOrWhiteSpace(invite.HostedInviteIdHash))
            {
                try
                {
                    await new HostedInviteClient(_state).DeleteAsync(invite.HostedInviteIdHash, cancellationToken);
                    invite.HostedInviteIdHash = string.Empty;
                    invite.HostedUrlProtected = string.Empty;
                }
                catch (Exception exception)
                {
                    hostedRemoved = false;
                    Log($"The network key was revoked, but Fly link cleanup will need retrying: {exception.Message}");
                }
            }
            bundlePath = invite.BundlePath;
            invite.ExpiresAt = DateTimeOffset.UtcNow;
            invite.BundlePath = string.Empty;
            await _state.SaveAsync(cancellationToken);
        }
        finally
        {
            _state.InviteGate.Release();
        }
        var bundleDeleted = true;
        try { if (File.Exists(bundlePath)) File.Delete(bundlePath); }
        catch (Exception exception)
        {
            bundleDeleted = false;
            Log($"Invitation was canceled, but its legacy local bundle could not be deleted ({bundlePath}): {exception.Message}");
        }
        ReplaceInvites();
        Log(hostedRemoved && bundleDeleted
            ? $"Canceled the invitation for {invite.DeviceName}; its link and network key are disabled."
            : $"Canceled the network key for {invite.DeviceName}; retry cancellation to finish any pending cleanup.");
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
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
            var status = await _agents.GetStatusAsync(device, GetAgentToken(device), cancellationToken);
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
            if (AgentClient.IsTailscaleIp(status.TailscaleIp)) device.TailscaleIp = status.TailscaleIp;
        }
        catch
        {
            device.State = device.State == DeviceConnectionState.TailscaleOnly ? DeviceConnectionState.TailscaleOnly : DeviceConnectionState.Offline;
        }
    }

    private async Task RotateOneAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var oldToken = GetAgentToken(device);
        var newToken = SecurityHelpers.CreateToken();
        var newPassword = SecurityHelpers.CreateHumanPassword();
        await _agents.RotateCredentialsAsync(device, oldToken, newToken, newPassword, cancellationToken);
        device.AgentTokenProtected = SecretProtector.Protect(newToken);
        device.RustDeskPasswordProtected = SecretProtector.Protect(newPassword);
        device.PendingCredentialRotation = false;
        await _state.SaveAsync(cancellationToken);
    }

    private async Task CleanupInactiveHostedInvitationsAsync(CancellationToken cancellationToken)
    {
        var pending = Config.Invites
            .Where(invite => !string.IsNullOrWhiteSpace(invite.HostedInviteIdHash) && (invite.RedeemedAt.HasValue || invite.IsExpired))
            .ToArray();
        if (pending.Length == 0) return;
        var changed = false;
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var invite in pending)
            {
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
        Invites.Clear();
        foreach (var invite in Config.Invites.OrderByDescending(invite => invite.CreatedAt)) Invites.Add(invite);
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
