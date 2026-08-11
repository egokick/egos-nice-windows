using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Taildesk.Shared;

namespace Taildesk.Setup;

/// <summary>
/// Read-only installer discovery.  It deliberately accumulates independent
/// defects so Setup can explain the full repair plan before mutating a
/// machine.  Individual Ensure operations repeat their own verification after
/// this report because the machine may drift between discovery and repair.
/// </summary>
internal static class SetupPreflight
{
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    internal static Task<InstallerPreflightReport> DiscoverElevatedAsync(
        InvitePayload invite,
        string bundleDirectory,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = new InstallerPreflightReport();
        Probe(report, InstallerPreflightScope.Unelevated, "Windows", () =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Opticon Setup requires Windows.");
            var architecture = RuntimeInformation.OSArchitecture;
            if (architecture is not (Architecture.X64 or Architecture.Arm64))
                throw new PlatformNotSupportedException(
                    $"Opticon supports Windows x64 and ARM64, not {architecture}.");
        }, blocked: true);

        Probe(report, InstallerPreflightScope.Unelevated, "Disk space", () =>
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.MachineDataDirectory));
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Windows did not report the ProgramData volume.");
            var disk = new DriveInfo(root);
            if (!disk.IsReady || disk.AvailableFreeSpace < MinimumFreeBytes)
                throw new IOException($"At least {MinimumFreeBytes / (1024 * 1024 * 1024)} GB free space is required on {root}.");
        }, blocked: true);

        Probe(report, InstallerPreflightScope.Unelevated, "Invitation", () =>
        {
            if (invite.InviteId == Guid.Empty || invite.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("The invitation is expired or invalid.");
            if (!Uri.TryCreate(invite.CoordinatorUrl, UriKind.Absolute, out var coordinator)
                || coordinator.Scheme != Uri.UriSchemeHttps || coordinator.UserInfo.Length != 0)
                throw new InvalidDataException("The invitation has no canonical HTTPS coordinator endpoint.");
        }, blocked: true);

        Probe(report, InstallerPreflightScope.Unelevated, "Payload", () =>
        {
            var root = Path.GetFullPath(bundleDirectory);
            RequirePayload(root, "Payload", "Agent", "Taildesk.Agent.exe");
            RequirePayload(root, "Payload", "UpdateGuardian", "Taildesk.UpdateGuardian.exe");
            if (invite.Role == DeviceRole.ControllerAndManaged)
            {
                RequirePayload(root, "Payload", "Admin", "Opticon.exe");
                RequirePayload(root, "Payload", "Admin", "Cli", "opticon.exe");
            }
        }, blocked: true);

        ProbeRepair(report, "Build environment", () =>
        {
            var dotnetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk");
            var acceptedSdk = Directory.Exists(dotnetRoot)
                              && Directory.EnumerateDirectories(dotnetRoot)
                                  .Select(Path.GetFileName)
                                  .Any(version => !string.IsNullOrWhiteSpace(version)
                                                  && DotNetSdkPolicy.IsAcceptedVersion(version));
            return acceptedSdk
                ? null
                : "Install the pinned stable .NET 10 SDK, then rebuild with the isolated offline package environment.";
        });

        Probe(report, InstallerPreflightScope.Unelevated, "Authenticated source", () =>
        {
            // The source launcher has already downloaded the invitation and
            // archive over its authenticated channel. Setup reads the active
            // binding here so that source hash/provenance mismatches appear in
            // the aggregate report before machine-wide repairs begin.
            _ = SourceBuildProvenance.RequireActiveInstallationBinding(invite.InviteId);
        }, blocked: true);
        report.Add(new InstallerPreflightFinding(
            InstallerPreflightScope.Unelevated,
            InstallerPreflightSeverity.Informational,
            "Authenticated source",
            "The invitation, source archive, and source-manifest hashes were verified before elevation."));

        Probe(report, InstallerPreflightScope.Elevated, "Administrator approval", () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Setup must be elevated before it can repair protected components.");
        }, blocked: true);

        Probe(report, InstallerPreflightScope.Elevated, "Interactive profile", () =>
        {
            _ = InteractiveUserProfile.Resolve();
        }, blocked: true);

        ProbeRepair(report, "Protected storage", () =>
        {
            if (!Directory.Exists(AppPaths.MachineDataDirectory)
                || !Directory.Exists(AppPaths.SetupStagingDirectory))
                return "Create the protected Taildesk machine-state and SetupStaging directories.";
            try
            {
                MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.MachineDataDirectory);
                MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
                return null;
            }
            catch (Exception exception)
            {
                // Generic machine state can contain active secrets, so an ACL
                // violation is a security block rather than an unsafe repair.
                report.Add(new InstallerPreflightFinding(
                    InstallerPreflightScope.Elevated,
                    InstallerPreflightSeverity.Blocked,
                    "Protected storage",
                    exception.Message));
                return null;
            }
        });

        ProbeRepair(report, "Source provenance", () =>
        {
            var provenance = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonProvenance");
            return Directory.Exists(provenance)
                ? "Revalidate and normalize regenerable source provenance before using it."
                : "Create the protected source-provenance directory from the authenticated source archive.";
        });

        ProbeRepair(report, "OpenSSH", () =>
        {
            var sshd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "sshd.exe");
            return File.Exists(sshd) ? null : "Install the Windows OpenSSH Server capability.";
        });

        ProbeRepair(report, "Private-network component", () =>
        {
            var tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            return DependencyRepairPlan(
                tailscale, DependencyArtifacts.Tailscale(RuntimeInformation.OSArchitecture), "Tailscale");
        });

        ProbeRepair(report, "Remote-access component", () =>
        {
            var rustDesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
            return DependencyRepairPlan(
                rustDesk, DependencyArtifacts.RustDesk(RuntimeInformation.OSArchitecture), "RustDesk");
        });

        Probe(report, InstallerPreflightScope.Elevated, "Reboot state", () =>
        {
            if (HasPendingReboot())
                throw new InvalidOperationException(
                    "Windows has a pending reboot; restart before Setup can verify OpenSSH recovery.");
        }, blocked: true);

        return report;
    }, cancellationToken);

    private static void Probe(
        InstallerPreflightReport report,
        InstallerPreflightScope scope,
        string area,
        Action action,
        bool blocked)
    {
        try { action(); }
        catch (Exception exception)
        {
            report.Add(new InstallerPreflightFinding(
                scope,
                blocked ? InstallerPreflightSeverity.Blocked : InstallerPreflightSeverity.Repair,
                area,
                exception.Message));
        }
    }

    private static void ProbeRepair(
        InstallerPreflightReport report,
        string area,
        Func<string?> repair)
    {
        try
        {
            var plan = repair();
            if (!string.IsNullOrWhiteSpace(plan))
                report.Add(new InstallerPreflightFinding(
                    InstallerPreflightScope.Elevated,
                    InstallerPreflightSeverity.Repair,
                    area,
                    plan,
                    plan));
        }
        catch (Exception exception)
        {
            report.Add(new InstallerPreflightFinding(
                InstallerPreflightScope.Elevated,
                InstallerPreflightSeverity.Blocked,
                area,
                exception.Message));
        }
    }

    private static void RequirePayload(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        if (!File.Exists(path))
            throw new FileNotFoundException("The authenticated Opticon payload is incomplete.", path);
    }

    private static string? DependencyRepairPlan(
        string executable,
        DependencyArtifact artifact,
        string product)
    {
        if (!File.Exists(executable)) return $"Install pinned {product} {artifact.Version}.";
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Trim();
            return string.Equals(version, artifact.Version, StringComparison.Ordinal)
                   || string.Equals(version, artifact.Version + ".0", StringComparison.Ordinal)
                ? null
                : $"Upgrade or repair {product} to pinned version {artifact.Version}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Repair the unreadable {product} installation to pinned version {artifact.Version}.";
        }
    }

    private static bool HasPendingReboot()
    {
        using var sessionManager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager", writable: false);
        var renameOperations = sessionManager?.GetValue("PendingFileRenameOperations");
        if (renameOperations is not null) return true;
        using var componentServicing = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", writable: false);
        return componentServicing is not null;
    }
}

internal sealed class SetupPreflightBlockedException : InvalidOperationException
{
    public SetupPreflightBlockedException(InstallerPreflightReport report)
        : base("Opticon Setup cannot continue until the blocked preflight conditions are resolved.")
    {
        Report = report;
    }

    public InstallerPreflightReport Report { get; }
}
