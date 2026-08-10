using System.Net;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal sealed class GuardianPathPolicy
{
    public string ProgramDataRoot { get; } = FullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    public string ProgramFilesRoot { get; } = FullPath(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    public string UpdateRoot { get; } = FullPath(AppPaths.UpdateDataDirectory);
    public string JournalPath { get; } = FullPath(AppPaths.UpdateJournalFile);
    public string CommitRequestPath { get; } = FullPath(AppPaths.UpdateCommitRequestFile);
    public string AgentDirectory { get; } = FullPath(AppPaths.AgentInstallDirectory);
    public string AgentExecutable => Path.Combine(AgentDirectory, "Taildesk.Agent.exe");
    public string GuardianDirectory { get; } = FullPath(AppPaths.UpdateGuardianInstallDirectory);
    public string GuardianExecutable => Path.Combine(GuardianDirectory, "Taildesk.UpdateGuardian.exe");

    public GuardianPathPolicy()
    {
        EnsureDescendant(ProgramDataRoot, UpdateRoot, "update data directory");
        EnsureDescendant(UpdateRoot, JournalPath, "update journal");
        EnsureDescendant(UpdateRoot, CommitRequestPath, "update commit request");
        EnsureDescendant(ProgramFilesRoot, AgentDirectory, "Agent installation directory");
        EnsureDescendant(ProgramFilesRoot, GuardianDirectory, "guardian installation directory");
        EnsureExistingAncestorsAreNotReparsePoints(UpdateRoot, ProgramDataRoot);
        EnsureExistingAncestorsAreNotReparsePoints(AgentDirectory, ProgramFilesRoot);
        EnsureExistingAncestorsAreNotReparsePoints(GuardianDirectory, ProgramFilesRoot);
    }

    public void ValidateRunningGuardian()
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("The guardian process path is unavailable.");
        RequireExactPath(processPath, GuardianExecutable, "guardian executable");
        EnsureExistingAncestorsAreNotReparsePoints(processPath, ProgramFilesRoot);
    }

    public OperationPaths ValidateJournal(UpdateJournal journal)
    {
        if (journal.OperationId == Guid.Empty
            || !Enum.IsDefined(journal.DeliveryMode)
            || (journal.DeliveryMode == UpdateDeliveryMode.SignedBundle && journal.SchemaVersion != 1)
            || (journal.DeliveryMode == UpdateDeliveryMode.SourceArchive && journal.SchemaVersion != 2))
            throw new InvalidDataException("The update journal has an unsupported schema or operation ID.");
        if (!Enum.IsDefined(journal.Role))
            throw new InvalidDataException("The update journal contains an unsupported device role.");
        if (!journal.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)
            && !journal.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update journal contains an unsupported architecture.");
        _ = UpdatePackageVerifier.ParseVersion(journal.CurrentVersion);
        _ = UpdatePackageVerifier.ParseVersion(journal.TargetVersion);
        if (journal.PackageSize is < 1024 or > 1024L * 1024 * 1024
            || journal.PackageSha256.Length != 64
            || journal.PackageSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The update journal contains an invalid package pin.");
        if (!TryParseTailscaleIpv4(journal.BindAddress, out _))
            throw new InvalidDataException("The update journal is not bound to a Tailscale IPv4 address.");
        if (journal.StartedAt == default || journal.StartedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("The update journal contains an invalid start time.");
        if (journal.AgentProcessId < 0)
            throw new InvalidDataException("The update journal contains an invalid Agent process ID.");

        var operationName = journal.OperationId.ToString("N");
        var operationDirectory = FullPath(Path.Combine(UpdateRoot, operationName));
        var sourceBuildOutput = FullPath(Path.Combine(operationDirectory, "source-build"));
        var sourceBuildAttestation = FullPath(Path.Combine(operationDirectory, "source-build-attestation.json"));
        var expected = new OperationPaths(
            operationDirectory,
            FullPath(Path.Combine(operationDirectory, "package.zip")),
            journal.DeliveryMode == UpdateDeliveryMode.SourceArchive
                ? FullPath(Path.Combine(sourceBuildOutput, "Payload", "Agent"))
                : FullPath(Path.Combine(operationDirectory, "staged-agent")),
            sourceBuildOutput,
            sourceBuildAttestation,
            FullPath(Path.Combine(sourceBuildOutput, "Payload", "UpdateGuardian")),
            FullPath(AgentDirectory + ".candidate-" + operationName),
            FullPath(AgentDirectory + ".rollback-" + operationName),
            FullPath(AgentDirectory + ".failed-" + operationName));

        RequireExactPath(journal.PackagePath, expected.PackagePath, "staged package");
        RequireExactPath(journal.StagedAgentDirectory, expected.StagedAgentDirectory, "staged Agent directory");
        RequireExactPath(journal.CandidateDirectory, expected.CandidateDirectory, "candidate Agent directory");
        RequireExactPath(journal.RollbackDirectory, expected.RollbackDirectory, "rollback Agent directory");
        RequireExactPath(journal.FailedCandidateDirectory, expected.FailedCandidateDirectory, "failed candidate directory");
        if (journal.DeliveryMode == UpdateDeliveryMode.SourceArchive)
        {
            RequireExactPath(journal.SourceBuildOutputDirectory, expected.SourceBuildOutputDirectory,
                "source build output directory");
            RequireExactPath(journal.SourceBuildAttestationPath, expected.SourceBuildAttestationPath,
                "source build attestation");
            SourceUpdatePackageVerifier.ValidateRequest(CreateSourceVerificationRequest(journal));
        }
        EnsureDescendant(UpdateRoot, expected.OperationDirectory, "operation directory");
        EnsureDescendant(UpdateRoot, expected.PackagePath, "staged package");
        EnsureDescendant(UpdateRoot, expected.SourceBuildOutputDirectory, "source build output directory");
        EnsureDescendant(UpdateRoot, expected.SourceBuildAttestationPath, "source build attestation");
        EnsureDescendant(UpdateRoot, expected.StagedGuardianDirectory, "staged Guardian directory");
        EnsureDescendant(ProgramFilesRoot, expected.CandidateDirectory, "candidate Agent directory");
        EnsureDescendant(ProgramFilesRoot, expected.RollbackDirectory, "rollback Agent directory");
        EnsureDescendant(ProgramFilesRoot, expected.FailedCandidateDirectory, "failed candidate directory");

        EnsureExistingAncestorsAreNotReparsePoints(expected.PackagePath, UpdateRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.StagedAgentDirectory, UpdateRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.SourceBuildOutputDirectory, UpdateRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.SourceBuildAttestationPath, UpdateRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.StagedGuardianDirectory, UpdateRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.CandidateDirectory, ProgramFilesRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.RollbackDirectory, ProgramFilesRoot);
        EnsureExistingAncestorsAreNotReparsePoints(expected.FailedCandidateDirectory, ProgramFilesRoot);

        if (journal.Phase is UpdatePhase.ActivationScheduled or UpdatePhase.Activating or UpdatePhase.AwaitingCommit)
        {
            if (journal.ActivateAfter is not { } activateAfter || journal.CommitDeadline is not { } deadline
                || activateAfter < journal.StartedAt || deadline <= activateAfter
                || deadline - activateAfter > RemoteAdministrationProtocol.UpdateCommitWindow.Add(TimeSpan.FromSeconds(30)))
                throw new InvalidDataException("The update journal contains an invalid activation or commit deadline.");
        }

        return expected;
    }

    public OpticonUpdateRequest CreateVerificationRequest(UpdateJournal journal) => new()
    {
        ProtocolVersion = RemoteAdministrationProtocol.UpdateVersion,
        OperationId = journal.OperationId,
        TargetVersion = journal.TargetVersion,
        Role = journal.Role,
        Architecture = journal.Architecture,
        DownloadUrl = "https://guardian.invalid/release.zip",
        PackageSize = journal.PackageSize,
        PackageSha256 = journal.PackageSha256
    };

    public SourceUpdateRequest CreateSourceVerificationRequest(UpdateJournal journal) => new()
    {
        ProtocolVersion = SourceUpdateProtocol.Version,
        OperationId = journal.OperationId,
        TargetVersion = journal.TargetVersion,
        Role = journal.Role,
        Architecture = journal.Architecture,
        DownloadUrl = journal.SourceDownloadUrl,
        SourceFile = journal.SourceFile,
        SourceSize = journal.PackageSize,
        SourceSha256 = journal.PackageSha256,
        SourceManifestSha256 = journal.SourceManifestSha256,
        SourceManifestKeyId = journal.SourceManifestKeyId,
        SigningProfile = journal.SigningProfile,
        ProductSignerThumbprint = journal.ProductSignerThumbprint,
        SdkVersion = journal.SdkVersion,
        RuntimeVersion = journal.RuntimeVersion,
        TargetRuntime = journal.TargetRuntime
    };

    public void EnsureSafeTree(string path, string allowedRoot)
    {
        EnsureDescendant(allowedRoot, path, "directory tree");
        EnsureExistingAncestorsAreNotReparsePoints(path, allowedRoot);
        if (!Directory.Exists(path)) return;

        var pending = new Stack<string>();
        pending.Push(FullPath(path));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNotReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNotReparsePoint(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    public void EnsureProtectedPath(string path, string allowedRoot, string description)
    {
        EnsureDescendant(allowedRoot, path, description);
        EnsureExistingAncestorsAreNotReparsePoints(path, allowedRoot);
    }

    public void RequireExactPath(string actual, string expected, string description)
    {
        if (string.IsNullOrWhiteSpace(actual) || !PathEquals(actual, expected))
            throw new InvalidDataException($"The {description} path does not match the protected update layout.");
    }

    public static bool PathEquals(string left, string right)
    {
        try
        {
            return FullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(FullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool TryParseTailscaleIpv4(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed)
            && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = parsed.GetAddressBytes();
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                address = parsed;
                return true;
            }
        }

        address = IPAddress.None;
        return false;
    }

    private static void EnsureDescendant(string root, string path, string description)
    {
        var normalizedRoot = FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var normalizedPath = FullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {description} escapes its expected root.");
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string path, string stopRoot)
    {
        var current = File.Exists(path) ? FullPath(path) : Directory.Exists(path) ? FullPath(path) : Path.GetDirectoryName(FullPath(path));
        var stop = FullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(current))
        {
            EnsureNotReparsePoint(current);
            if (current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(stop, StringComparison.OrdinalIgnoreCase))
                return;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("A protected path is not rooted under its expected directory.");
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Protected update path is a reparse point: {path}");
    }

    private static string FullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A required protected path is empty.");
        return Path.GetFullPath(value);
    }
}

internal sealed record OperationPaths(
    string OperationDirectory,
    string PackagePath,
    string StagedAgentDirectory,
    string SourceBuildOutputDirectory,
    string SourceBuildAttestationPath,
    string StagedGuardianDirectory,
    string CandidateDirectory,
    string RollbackDirectory,
    string FailedCandidateDirectory);
