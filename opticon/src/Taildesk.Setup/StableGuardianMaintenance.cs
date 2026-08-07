using System.Diagnostics;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal static class StableGuardianMaintenance
{
    private const string ExecutableName = "Taildesk.UpdateGuardian.exe";

    public static async Task ReconcileSignedReleaseAsync(
        string sourceDirectory,
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var sourceExecutable = Path.Combine(sourceDirectory, ExecutableName);
        var installedExecutable = Path.Combine(installedDirectory, ExecutableName);
        if (!File.Exists(sourceExecutable) || !File.Exists(installedExecutable))
            throw new FileNotFoundException("Stable Guardian maintenance requires both signed executables.");

        RequireSingleExecutable(sourceDirectory, "signed release Guardian");
        await InvitationSigning.VerifyAuthenticodeAsync(sourceExecutable, cancellationToken);
        await InvitationSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);

        var sourceVersion = ReadVersion(sourceExecutable);
        var installedVersion = ReadVersion(installedExecutable);
        var installedFiles = RequireRecognizedInstalledFiles(installedDirectory);
        var contentMatches = await FilesMatchAsync(sourceExecutable, installedExecutable, cancellationToken);
        if (installedVersion > sourceVersion || contentMatches)
        {
            await DeleteMaintenanceArtifactsAsync(installedFiles, cancellationToken);
            return;
        }

        EnsureNoActiveUpdate();
        using var coordination = await UpdateJournalCoordination.AcquireAsync(
            TimeSpan.FromMinutes(2),
            cancellationToken);
        EnsureNoActiveUpdate();

        // A prior successful ReplaceFile can leave its now-unused rollback
        // artifact behind if antivirus briefly holds it. These exact names are
        // private transaction residue, never runnable Guardian companions.
        await DeleteMaintenanceArtifactsAsync(installedFiles, cancellationToken);

        foreach (var taskName in new[]
                 {
                     RemoteAdministrationProtocol.SshSupervisorTaskName,
                     RemoteAdministrationProtocol.GuardianWatchdogTaskName,
                     RemoteAdministrationProtocol.GuardianTaskName
                 })
        {
            _ = await ProcessRunner.RunAsync(
                "schtasks.exe",
                ["/End", "/TN", taskName],
                TimeSpan.FromSeconds(15),
                cancellationToken);
        }
        await Task.Delay(750, cancellationToken);

        var suffix = Guid.NewGuid().ToString("N");
        var staged = installedExecutable + ".upgrade-" + suffix;
        var backup = installedExecutable + ".backup-" + suffix;
        var failed = installedExecutable + ".failed-" + suffix;
        var promoted = false;
        try
        {
            File.Copy(sourceExecutable, staged, overwrite: false);
            await InvitationSigning.VerifyAuthenticodeAsync(staged, cancellationToken);

            // File.Replace uses Windows ReplaceFile semantics: the signed new
            // executable is promoted at the fixed path while the prior signed
            // Guardian is retained as a rollback copy.
            await ReplaceWithRetryAsync(staged, installedExecutable, backup, cancellationToken);
            promoted = true;
            await InvitationSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
            if (ReadVersion(installedExecutable) != sourceVersion)
                throw new InvalidDataException("The promoted stable Guardian version does not match the signed release.");
            if (!await FilesMatchAsync(sourceExecutable, installedExecutable, cancellationToken))
                throw new InvalidDataException("The promoted stable Guardian does not match the signed release.");

            await DeleteWithRetryAsync(backup, cancellationToken);
        }
        catch
        {
            if (promoted && File.Exists(backup))
            {
                try
                {
                    File.Replace(backup, installedExecutable, failed, ignoreMetadataErrors: true);
                    await InvitationSigning.VerifyAuthenticodeAsync(installedExecutable, CancellationToken.None);
                }
                catch
                {
                    // Preserve every recovery copy if rollback itself fails.
                }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { if (File.Exists(failed)) File.Delete(failed); } catch { }
        }
    }

    private static Version ReadVersion(string path) =>
        UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty));

    private static async Task ReplaceWithRetryAsync(
        string staged,
        string installed,
        string backup,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(staged, installed, backup, ignoreMetadataErrors: false);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    private static void RequireSingleExecutable(string directory, string description)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .ToArray();
        if (files.Length != 1 || !files[0].Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The {description} contains companion files and cannot be atomically upgraded by this Setup.");
    }

    private static string[] RequireRecognizedInstalledFiles(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.Contains('/') || !IsMaintenanceArtifact(relative))
                throw new InvalidOperationException(
                    "The installed stable Guardian contains an unrecognized companion file and was not changed.");
        }
        return files.Where(path => !Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool IsMaintenanceArtifact(string fileName)
    {
        foreach (var marker in new[] { ".upgrade-", ".backup-", ".failed-" })
        {
            var prefix = ExecutableName + marker;
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(fileName[prefix.Length..], "N", out _))
                return true;
        }
        return false;
    }

    private static async Task DeleteMaintenanceArtifactsAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
            await DeleteWithRetryAsync(path, cancellationToken);
    }

    private static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; File.Exists(path); attempt++)
        {
            try { File.Delete(path); }
            catch (IOException) when (attempt < 10) { }
            catch (UnauthorizedAccessException) when (attempt < 10) { }
            if (!File.Exists(path)) return;
            if (attempt >= 10)
                throw new IOException($"Windows kept a stable Guardian maintenance artifact locked: {Path.GetFileName(path)}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private static async Task<bool> FilesMatchAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        await using var leftInput = File.OpenRead(left);
        await using var rightInput = File.OpenRead(right);
        var leftHash = await System.Security.Cryptography.SHA256.HashDataAsync(leftInput, cancellationToken);
        var rightHash = await System.Security.Cryptography.SHA256.HashDataAsync(rightInput, cancellationToken);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static void EnsureNoActiveUpdate()
    {
        UpdateJournal? journal;
        try { journal = UpdateJournalPersistence.Load(); }
        catch (Exception exception)
        {
            throw new InvalidDataException("The protected update journal cannot be read before Guardian maintenance.", exception);
        }
        if (journal is not null
            && journal.Phase is not UpdatePhase.None and not UpdatePhase.Failed and not UpdatePhase.RolledBack)
            throw new InvalidOperationException(
                $"Stable Guardian maintenance cannot interrupt update {journal.OperationId:N}, which is {journal.Phase}.");
    }
}
