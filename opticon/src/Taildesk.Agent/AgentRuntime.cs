using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class AgentRuntime
{
    private readonly AgentState _state;
    private readonly TailscaleCli _tailscale;
    private readonly UpdateManager _updates;
    private readonly SshAccessManager _ssh;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public AgentRuntime(AgentState state, TailscaleCli tailscale, UpdateManager updates, SshAccessManager ssh)
    {
        _state = state;
        _tailscale = tailscale;
        _updates = updates;
        _ssh = ssh;
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

    public async Task RotateCredentialsAsync(string newAgentToken, string newRustDeskPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newAgentToken) || newAgentToken.Length < 32
            || string.IsNullOrWhiteSpace(newRustDeskPassword) || newRustDeskPassword.Length < 12)
        {
            throw new InvalidOperationException("Replacement credentials do not meet the minimum length.");
        }

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

        _state.Config.AgentTokenHash = SecurityHelpers.HashToken(newAgentToken);
        await _state.SaveAsync(cancellationToken);
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken cancellationToken)
    {
        _state.Config.Role = role;
        await _state.SaveAsync(cancellationToken);
        var admin = Path.Combine(AppPaths.InstallDirectory, "Admin", "Opticon.exe");
        if (!File.Exists(admin))
        {
            throw new FileNotFoundException("The controller payload is not installed on this machine.");
        }

        IReadOnlyList<string> shortcutPaths = _state.Config.ControllerShortcutPaths.Count > 0
            ? _state.Config.ControllerShortcutPaths
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Opticon.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Opticon.lnk")
            };
        if (role == DeviceRole.ControllerAndManaged)
        {
            foreach (var shortcutPath in shortcutPaths) CreateShortcut(admin, shortcutPath);
        }
        else
        {
            foreach (var shortcutPath in shortcutPaths)
            {
                try { File.Delete(shortcutPath); } catch { }
            }
        }
    }

    private static void CreateShortcut(string target, string shortcutPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.Description = "Opticon command center";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }
}
