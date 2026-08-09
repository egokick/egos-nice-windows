using System.Diagnostics;
using System.Reflection;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class AgentRuntime
{
    private readonly AgentState _state;
    private readonly TailscaleCli _tailscale;
    private readonly UpdateManager _updates;
    private readonly SshAccessManager _ssh;
    private readonly BatteryStatusProvider _battery;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public AgentRuntime(AgentState state, TailscaleCli tailscale, UpdateManager updates, SshAccessManager ssh,
        BatteryStatusProvider battery)
    {
        _state = state;
        _tailscale = tailscale;
        _updates = updates;
        _ssh = ssh;
        _battery = battery;
    }

    public async Task<DeviceStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        TailscaleSnapshot tailscale;
        try
        {
            tailscale = await _tailscale.GetStatusAsync(cancellationToken);
        }
        catch
        {
            tailscale = new TailscaleSnapshot();
        }

        var systemDrive = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady && drive.Name.StartsWith(
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", StringComparison.OrdinalIgnoreCase));
        var ssh = await _ssh.GetStatusAsync(cancellationToken);
        var rustDeskReady = UpdateManager.IsRustDeskReady();

        return new DeviceStatusDto
        {
            HostName = Environment.MachineName,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = UpdateManager.CurrentArchitecture,
            AgentVersion = UpdateManager.CurrentVersion,
            GuardianVersion = GetInstalledGuardianVersion(),
            UpdateProtocolVersion = RemoteAdministrationProtocol.UpdateVersion,
            TailscaleIp = tailscale.Ip,
            TailnetDeviceId = tailscale.DeviceId,
            RustDeskRunning = rustDeskReady,
            RustDeskReady = rustDeskReady,
            SshReady = ssh.Listening,
            SshPort = RemoteAdministrationProtocol.SshPort,
            AdvertisesExitNode = _state.Config.AdvertiseExitNode,
            FreeDiskBytes = systemDrive?.AvailableFreeSpace ?? 0,
            TotalDiskBytes = systemDrive?.TotalSize ?? 0,
            StartedAt = _startedAt,
            OnlineDurationSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            BatteryPercentage = _battery.GetBatteryPercentage(),
            ServerTime = DateTimeOffset.UtcNow,
            UpdateStatus = _updates.GetStatus()
        };
    }

    private static string GetInstalledGuardianVersion()
    {
        var executable = Path.Combine(
            AppPaths.UpdateGuardianInstallDirectory,
            "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(executable)) return string.Empty;
        try
        {
            return UpdatePackageVerifier.NormalizeVersion(
                FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task SetExitNodeAdvertisementAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _tailscale.SetAdvertiseExitNodeAsync(enabled, cancellationToken);
        _state.Config.AdvertiseExitNode = enabled;
        await _state.SaveAsync(cancellationToken);
    }

    public async Task RotateCredentialsAsync(
        Guid operationId,
        string newAgentToken,
        string newRustDeskPassword,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new InvalidOperationException("Credential rotation requires a non-empty operation ID.");
        if (string.IsNullOrWhiteSpace(newAgentToken) || newAgentToken.Length < 32
            || string.IsNullOrWhiteSpace(newRustDeskPassword) || newRustDeskPassword.Length < 12)
        {
            throw new InvalidOperationException("Replacement credentials do not meet the minimum length.");
        }

        if (_state.Config.PendingCredentialRotationId == operationId
            || _state.Config.LastCompletedCredentialRotationId == operationId)
        {
            if (CredentialRotationState.IsExactAppliedRotation(
                    _state.Config, operationId, newAgentToken, newRustDeskPassword))
                return;
            throw new InvalidOperationException("That credential rotation operation was already used with different credentials.");
        }
        if (_state.Config.PendingCredentialRotationId.HasValue)
            throw new InvalidOperationException("A different credential rotation is awaiting confirmation.");

        var rustDesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        if (!File.Exists(rustDesk))
        {
            throw new FileNotFoundException("RustDesk is not installed.");
        }

        var changed = await ProcessRunner.RunAsync(rustDesk, ["--password", newRustDeskPassword], TimeSpan.FromSeconds(30), cancellationToken);
        if (!changed.Succeeded)
        {
            throw new InvalidOperationException("RustDesk rejected the replacement permanent password.");
        }

        CredentialRotationState.Begin(
            _state.Config, operationId, newAgentToken, newRustDeskPassword, DateTimeOffset.UtcNow);
        await _state.SaveAsync(cancellationToken);
    }

    public async Task CommitCredentialRotationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        CredentialRotationState.Commit(_state.Config, operationId);
        await _state.SaveAsync(cancellationToken);
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken cancellationToken)
    {
        var admin = Path.Combine(AppPaths.InstallDirectory, "Admin", "Opticon.exe");
        if (role == DeviceRole.ControllerAndManaged && !File.Exists(admin))
            throw new FileNotFoundException("The controller payload is not installed on this machine.");
        _state.Config.Role = role;
        _state.Config.ControllerShortcutPaths = [];
        await _state.SaveAsync(cancellationToken);
    }
}
