using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Admin;

internal static class CliPathIntegration
{
    internal const string OwnershipMarkerName = ".opticon-controller-owned";
    internal const string OwnershipMarkerValue = "Opticon command-center controller payload v1";
    internal const string ReadyMarkerName = ".opticon-controller-ready";
    internal const string ReadyMarkerValue = "Opticon command-center controller payload ready v1";
    internal const string InstallDirectoryValueName = "InstallDirectory";
    internal const string InstallLockFileName = ".controller-install.lock";
    private static FileStream? _lifetimeInstallLease;

    public static async Task EnsureForCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var runningDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        var defaultInstalledDirectory = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, "Admin"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var retainedInstalledDirectory = defaultInstalledDirectory + ".previous";
        var runningDefaultInstall = runningDirectory.Equals(defaultInstalledDirectory, StringComparison.OrdinalIgnoreCase);
        var runningRetainedInstall = runningDirectory.Equals(retainedInstalledDirectory, StringComparison.OrdinalIgnoreCase);
        if (!runningDefaultInstall && !runningRetainedInstall)
            return; // Only canonical installed/retained copies participate in repair locking.

        await AcquireControllerLifetimeLeaseAsync(cancellationToken);
        if (runningRetainedInstall)
        {
            if (!await HasExactControllerMarkersAsync(runningDirectory, cancellationToken))
                throw new InvalidDataException("The retained Opticon payload changed while it was starting.");
            return; // Retained payloads take a safety lease but never mutate PATH.
        }

        using var stateKey = Registry.CurrentUser.CreateSubKey("Software\\Taildesk\\Opticon", writable: true)
                             ?? throw new InvalidOperationException("The Opticon user installation key could not be opened.");
        if (!await HasExactControllerMarkersAsync(runningDirectory, cancellationToken))
            throw new InvalidDataException(
                "The recorded Opticon installation ownership or ready marker is missing or invalid. Run command-center repair.");

        var uiExecutable = Path.Combine(runningDirectory, "Opticon.exe");
        var cliDirectory = Path.Combine(runningDirectory, "Cli");
        var cliExecutable = Path.Combine(cliDirectory, "opticon.exe");
        var processPath = Environment.ProcessPath is { Length: > 0 } value
            ? Path.GetFullPath(value)
            : throw new InvalidOperationException("Windows did not expose the running Opticon executable path.");
        if (!processPath.Equals(Path.GetFullPath(uiExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the recorded installed Opticon UI may repair the CLI PATH.");
        if (!File.Exists(cliExecutable))
            throw new FileNotFoundException(
                "The installed Opticon CLI is missing. Run command-center repair before using agent automation.",
                cliExecutable);
        await ProductSigning.VerifyAuthenticodeAsync(uiExecutable, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);

        var uiVersion = ReadExactFileVersion(uiExecutable, "Opticon UI");
        var cliVersion = ReadExactFileVersion(cliExecutable, "Opticon CLI");
        if (uiVersion != cliVersion)
            throw new InvalidDataException(
                $"The installed Opticon UI ({uiVersion}) and CLI ({cliVersion}) versions do not match. Run command-center repair.");
        var recordedDirectory = NormalizePathEntry(
            stateKey.GetValue(
                InstallDirectoryValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
        if (recordedDirectory is null)
            stateKey.SetValue(
                InstallDirectoryValueName,
                defaultInstalledDirectory,
                RegistryValueKind.String);
        else if (!recordedDirectory.Equals(defaultInstalledDirectory, StringComparison.OrdinalIgnoreCase)
                 || !runningDirectory.Equals(recordedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The current user's recorded Opticon installation conflicts with the verified canonical payload. Run command-center repair.");

        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true)
                        ?? throw new InvalidOperationException("The current user environment key could not be opened.");
        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                      ?? string.Empty;
        var target = Path.GetFullPath(cliDirectory).TrimEnd(Path.DirectorySeparatorChar);
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
            throw new InvalidOperationException("The current user PATH is too long to add the Opticon CLI safely.");
        RegistryValueKind kind;
        try { kind = key.GetValueKind("Path"); }
        catch (IOException) { kind = RegistryValueKind.ExpandString; }
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidDataException("The current user PATH registry value has an unexpected type.");
        if (!current.Equals(updated, StringComparison.Ordinal))
            key.SetValue("Path", updated, kind);
        stateKey.SetValue("CliPath", target, RegistryValueKind.String);

        _ = SendMessageTimeout(
            new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    private static async Task<bool> HasExactControllerMarkersAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var ownershipMarker = Path.Combine(directory, OwnershipMarkerName);
        if (!File.Exists(ownershipMarker)
            || !string.Equals(
                await File.ReadAllTextAsync(ownershipMarker, cancellationToken),
                OwnershipMarkerValue,
                StringComparison.Ordinal))
            return false;
        var executingVersion = Assembly.GetExecutingAssembly().GetName().Version;
        if (executingVersion is null) return false;
        var readyMarker = Path.Combine(directory, ReadyMarkerName);
        return File.Exists(readyMarker)
               && string.Equals(
                   await File.ReadAllTextAsync(readyMarker, cancellationToken),
                   $"{ReadyMarkerValue}|{executingVersion}",
                   StringComparison.Ordinal);
    }
    private static async Task AcquireControllerLifetimeLeaseAsync(CancellationToken cancellationToken)
    {
        if (_lifetimeInstallLease is not null) return;
        var path = Path.Combine(AppPaths.InstallDirectory, InstallLockFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The Opticon installation lock is missing. Run command-center repair before starting the installed UI.",
                path);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _lifetimeInstallLease = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.None);
                return;
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Opticon startup waited two minutes for another controller installation to finish.",
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

    private static Version ReadExactFileVersion(string path, string description)
    {
        var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(text, out var version)
            ? version
            : throw new InvalidDataException($"The installed {description} has no valid file version.");
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wordParameter,
        string stringParameter,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
