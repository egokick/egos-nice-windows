using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Taildesk.Shared;

public sealed class SourceBuildAttestation
{
    public int SchemaVersion { get; set; } = 3;
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public string InviteCiphertextSha256 { get; set; } = string.Empty;
    public List<SourceBuildFileAttestation> Files { get; set; } = [];
}

public sealed class SourceBuildFileAttestation
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record SourceInstallationBinding(
    string TransactionId,
    Guid InviteId,
    string InviteCiphertextSha256,
    string SourceSha256,
    string SourceManifestSha256);

internal sealed class InstalledSourceProvenance
{
    public int SchemaVersion { get; set; } = 5;
    public string PendingTransactionId { get; set; } = string.Empty;
    public Guid PendingInviteId { get; set; }
    public string PendingInviteCiphertextSha256 { get; set; } = string.Empty;
    public List<InstalledSourceGeneration> Installed { get; set; } = [];
    public InstalledSourceGeneration? Pending { get; set; }
}

internal sealed class InstalledSourceGeneration
{
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public List<InstalledSourceFile> Files { get; set; } = [];
}

internal sealed class InstalledSourceFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public static class SourceBuildProvenance
{
    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ControllerTransactionPattern = new(
        "^Admin\\.(?:installing|failed)-[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex GuardianTransactionFilePattern = new(
        "^Taildesk\\.UpdateGuardian\\.exe\\.(?:upgrade|backup|failed)-[a-f0-9]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly object Gate = new();
    private static Dictionary<string, InstalledSourceFile>? _activeFiles;
    private static Dictionary<string, InstalledSourceFile>? _activeControllerFiles;
    private static InstalledSourceGeneration? _pendingPromotion;
    private static string _pendingTransactionId = string.Empty;
    private static Guid _pendingInviteId;
    private static string _pendingInviteCiphertextSha256 = string.Empty;
    private static FileStream? _activeStoreLease;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    private static string ProvenanceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonProvenance");
    private static string ProvenanceFile => Path.Combine(ProvenanceDirectory, "source-build-v5.json");
    private static string ProvenanceLockFile => Path.Combine(ProvenanceDirectory, "source-build-v5.lock");

    public static async Task ActivateForSetupAsync(
        string attestationPath,
        string invitePath,
        InvitePayload invite,
        string payloadDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Source-build provenance requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(attestationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(invitePath);
        ArgumentNullException.ThrowIfNull(invite);
        var attestationFullPath = Path.GetFullPath(attestationPath);
        var payloadRoot = Path.GetFullPath(payloadDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetDirectoryName(attestationFullPath)!.Equals(payloadRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source-build attestation is not beside the elevated Setup payload.");
        RejectReparsePoints(payloadRoot);
        if (new FileInfo(attestationFullPath).Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The source-build attestation has an invalid size.");

        await using var attestationStream = new FileStream(attestationFullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        var attestation = await JsonSerializer.DeserializeAsync<SourceBuildAttestation>(attestationStream, JsonDefaults.Options, cancellationToken)
                          ?? throw new InvalidDataException("The source-build attestation is empty.");
        await ValidatePinsAsync(attestation, invitePath, invite, cancellationToken);

        var sourceFiles = new Dictionary<string, InstalledSourceFile>(StringComparer.OrdinalIgnoreCase);
        var installedFiles = new Dictionary<string, InstalledSourceFile>(StringComparer.OrdinalIgnoreCase);
        var controllerFiles = new Dictionary<string, InstalledSourceFile>(StringComparer.OrdinalIgnoreCase);
        if (attestation.Files.Count is < 3 or > 512)
            throw new InvalidDataException("The source-build attestation file count is invalid.");
        foreach (var file in attestation.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(file.Path);
            if (file.Size <= 0 || !Sha256Pattern.IsMatch(file.Sha256))
                throw new InvalidDataException($"The source-build attestation is invalid for {relative}.");
            var sourcePath = Path.GetFullPath(Path.Combine(payloadRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!sourcePath.StartsWith(payloadRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
                throw new InvalidDataException($"The attested source-build file is missing: {relative}.");
            await VerifyHashAsync(sourcePath, file.Size, file.Sha256, cancellationToken);
            var trusted = new InstalledSourceFile { Path = sourcePath, Size = file.Size, Sha256 = file.Sha256 };
            if (!sourceFiles.TryAdd(sourcePath, trusted))
                throw new InvalidDataException("The source-build attestation contains a duplicate path.");

            var installedPath = MapInstalledPath(relative);
            if (installedPath is null) continue;
            var installed = new InstalledSourceFile { Path = installedPath, Size = file.Size, Sha256 = file.Sha256 };
            if (!installedFiles.TryAdd(installedPath, installed))
                throw new InvalidDataException("The source-build attestation maps two files to one installed path.");
            if (relative.StartsWith("Payload/Admin/", StringComparison.Ordinal))
                controllerFiles[relative["Payload/Admin/".Length..]] = installed;
        }

        var actualFiles = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(attestationFullPath, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actualFiles.Count != sourceFiles.Count || actualFiles.Except(sourceFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The elevated source-build payload contains missing, duplicate, or unattested files.");

        var candidate = new InstalledSourceGeneration
        {
            ReleaseVersion = attestation.ReleaseVersion,
            SourceSha256 = attestation.SourceSha256,
            SourceManifestSha256 = attestation.SourceManifestSha256,
            SourceManifestKeyId = attestation.SourceManifestKeyId,
            SigningProfile = attestation.SigningProfile,
            ProductSignerThumbprint = attestation.ProductSignerThumbprint,
            SdkVersion = attestation.SdkVersion,
            RuntimeVersion = attestation.RuntimeVersion,
            TargetRuntime = attestation.TargetRuntime,
            Files = installedFiles.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList()
        };
        FileStream? storeLease = AcquireStoreLease(cancellationToken);
        try
        {
            var current = ReadProtectedStore();
            current.Installed = PruneInstalledGenerations(current.Installed);
            var machineInstall = MachineInstallTransactionPersistence.Load();
            if (machineInstall is not null)
            {
                if (current.Pending is null)
                    throw new InvalidDataException(
                        "Machine-install recovery exists but its pending source provenance is missing.");
                MachineInstallTransactionPersistence.RequireMatches(
                    machineInstall,
                    new SourceInstallationBinding(
                        current.PendingTransactionId,
                        current.PendingInviteId,
                        current.PendingInviteCiphertextSha256,
                        current.Pending.SourceSha256,
                        current.Pending.SourceManifestSha256));
            }
            string transactionId;
            if (current.Pending is not null)
            {
                if (!SameGeneration(current.Pending, candidate)
                    || current.PendingInviteId != invite.InviteId
                    || !FixedHash(current.PendingInviteCiphertextSha256, attestation.InviteCiphertextSha256))
                    throw new InvalidDataException(
                        "A different authenticated source-build invitation is already pending recovery.");
                transactionId = current.PendingTransactionId;
            }
            else
            {
                transactionId = Guid.NewGuid().ToString("N");
                var staged = new InstalledSourceProvenance
                {
                    PendingTransactionId = transactionId,
                    PendingInviteId = invite.InviteId,
                    PendingInviteCiphertextSha256 = attestation.InviteCiphertextSha256,
                    Installed = current.Installed,
                    Pending = candidate
                };
                WriteProtectedStore(staged);
            }

            lock (Gate)
            {
                if (_activeStoreLease is not null)
                    throw new InvalidOperationException("A source-build installation is already active in this Setup process.");
                _activeFiles = sourceFiles.Concat(installedFiles)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                _activeControllerFiles = controllerFiles;
                _pendingPromotion = candidate;
                _pendingTransactionId = transactionId;
                _pendingInviteId = invite.InviteId;
                _pendingInviteCiphertextSha256 = attestation.InviteCiphertextSha256;
                _activeStoreLease = storeLease;
                storeLease = null;
            }
        }
        finally
        {
            storeLease?.Dispose();
        }
    }

    internal static bool TryVerify(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var candidates = new List<InstalledSourceFile>();
            lock (Gate)
            {
                if (_activeFiles?.TryGetValue(fullPath, out var active) == true) candidates.Add(active);
                var transactionFile = ResolveActiveControllerStaging(fullPath);
                if (transactionFile is not null) candidates.Add(transactionFile);
            }
            var store = ReadProtectedStore();
            foreach (var canonical in ResolveCanonicalTrustPaths(fullPath))
            {
                foreach (var installed in store.Installed)
                    candidates.AddRange(installed.Files.Where(item => item.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase)));
                if (store.Pending is not null)
                    candidates.AddRange(store.Pending.Files.Where(item => item.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    VerifyHashAsync(fullPath, candidate.Size, candidate.Sha256, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    return true;
                }
                catch (InvalidDataException) { }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void CommitActiveInstallation()
    {
        var (candidate, transactionId, inviteId, inviteHash, storeLease) = GetActiveInstallation();
        if (candidate is null || string.IsNullOrEmpty(transactionId)) return;
        if (storeLease is null) throw new InvalidOperationException("The source-build installation lease is missing.");
        var agentFiles = FilesBelow(candidate, AppPaths.AgentInstallDirectory);
        if (agentFiles.Count == 0)
            throw new InvalidDataException("The pending source-build provenance has no Agent payload.");
        foreach (var file in agentFiles)
            VerifyHashAsync(file.Path, file.Size, file.Sha256, CancellationToken.None).GetAwaiter().GetResult();
        var store = ReadProtectedStore();
        RequirePendingMatches(store, candidate, transactionId, inviteId, inviteHash);
        AddInstalledGeneration(store, candidate, agentFiles);
        store.Pending = null;
        store.PendingTransactionId = string.Empty;
        store.PendingInviteId = Guid.Empty;
        store.PendingInviteCiphertextSha256 = string.Empty;
        store.Installed = PruneInstalledGenerations(store.Installed);
        WriteProtectedStore(store);
        ReleaseActiveInstallation(candidate, storeLease);
    }

    public static void CommitActiveComponent(string componentDirectory)
    {
        var (candidate, transactionId, inviteId, inviteHash, storeLease) = GetActiveInstallation();
        if (candidate is null || string.IsNullOrEmpty(transactionId)) return;
        if (storeLease is null) throw new InvalidOperationException("The source-build installation lease is missing.");
        var files = FilesBelow(candidate, componentDirectory);
        if (files.Count == 0)
            throw new InvalidDataException("The pending source-build provenance has no files for the installed component.");
        foreach (var file in files)
            VerifyHashAsync(file.Path, file.Size, file.Sha256, CancellationToken.None).GetAwaiter().GetResult();
        var store = ReadProtectedStore();
        RequirePendingMatches(store, candidate, transactionId, inviteId, inviteHash);
        AddInstalledGeneration(store, candidate, files);
        store.Installed = PruneInstalledGenerations(store.Installed);
        WriteProtectedStore(store);
    }

    public static void PruneInstalledTrust()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var storeLease = AcquireStoreLease(CancellationToken.None);
        var store = ReadProtectedStore();
        if (store.Installed.Count == 0) return;
        var pruned = PruneInstalledGenerations(store.Installed);
        if (pruned.Count == store.Installed.Count) return;
        store.Installed = pruned;
        WriteProtectedStore(store);
    }

    /// <summary>
    /// Registers a source-built update only after the Agent has verified its
    /// signed source archive and sealed its build attestation.  The record is
    /// canonicalized to the installed Agent/Guardian paths; TryVerify maps the
    /// guarded candidate, rollback, and source-build staging paths back to
    /// those canonical paths while the exact journal is live.
    /// </summary>
    public static async Task RegisterVerifiedSourceUpdateAsync(
        SourceUpdateBuildAttestation attestation,
        string outputDirectory,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Source update provenance requires Windows.");
        ArgumentNullException.ThrowIfNull(attestation);
        if (operationId == Guid.Empty)
            throw new InvalidDataException("The source update operation ID is empty.");

        var operationDirectory = Path.GetFullPath(Path.Combine(
            AppPaths.UpdateDataDirectory, operationId.ToString("N")));
        var expectedOutput = Path.GetFullPath(Path.Combine(operationDirectory, "source-build"));
        var actualOutput = Path.GetFullPath(outputDirectory);
        if (!actualOutput.Equals(expectedOutput, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(actualOutput))
            throw new InvalidDataException("The source build output is not at the protected transaction path.");
        MachineStorageSecurity.RequireRestrictedDirectory(operationDirectory);
        RejectReparsePoints(actualOutput);

        var journal = UpdateJournalPersistence.Load()
                      ?? throw new InvalidDataException("The source update journal is missing.");
        if (journal.SchemaVersion != 2 || journal.DeliveryMode != UpdateDeliveryMode.SourceArchive
            || journal.OperationId != operationId
            || !Path.GetFullPath(journal.SourceBuildOutputDirectory)
                .Equals(expectedOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source build does not match the protected update journal.");

        if (attestation.SchemaVersion != 1
            || UpdatePackageVerifier.NormalizeVersion(attestation.ReleaseVersion)
                != UpdatePackageVerifier.NormalizeVersion(journal.TargetVersion)
            || attestation.SourceFile != journal.SourceFile
            || attestation.SourceSize != journal.PackageSize
            || !FixedHash(attestation.SourceSha256, journal.PackageSha256)
            || !FixedHash(attestation.SourceManifestSha256, journal.SourceManifestSha256)
            || attestation.SourceManifestKeyId != journal.SourceManifestKeyId
            || attestation.SigningProfile != BuildSigningTrust.ProfileName
            || attestation.SigningProfile != journal.SigningProfile
            || !BuildSigningTrust.IsPublishable
            || attestation.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || attestation.ProductSignerThumbprint != journal.ProductSignerThumbprint
            || attestation.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || attestation.SdkVersion != journal.SdkVersion
            || attestation.SdkVersion != SourceUpdateProtocol.RequiredSdkVersion
            || attestation.RuntimeVersion != journal.RuntimeVersion
            || attestation.RuntimeVersion != SourceUpdateProtocol.RequiredRuntimeVersion
            || attestation.TargetRuntime != journal.TargetRuntime
            || attestation.TargetRuntime != CurrentTargetRuntime()
            || attestation.Role != journal.Role
            || !attestation.Architecture.Equals(journal.Architecture, StringComparison.OrdinalIgnoreCase)
            || attestation.Files.Count is < 2 or > 512)
            throw new InvalidDataException("The source build attestation has unsupported trust metadata.");

        var files = new List<InstalledSourceFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in attestation.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(file.Path);
            var installedPath = MapInstalledPath(relative)
                                ?? throw new InvalidDataException("The source update attestation declares an unsupported component.");
            if (file.Size <= 0 || !Sha256Pattern.IsMatch(file.Sha256))
                throw new InvalidDataException("The source update attestation has an invalid output hash.");
            var outputPath = Path.GetFullPath(Path.Combine(
                actualOutput, relative.Replace('/', Path.DirectorySeparatorChar)));
            var outputPrefix = actualOutput.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!outputPath.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(installedPath))
                throw new InvalidDataException("The source update attestation has a duplicate or unsafe output path.");
            await VerifyHashAsync(outputPath, file.Size, file.Sha256, cancellationToken);
            files.Add(new InstalledSourceFile
            {
                Path = installedPath,
                Size = file.Size,
                Sha256 = file.Sha256.ToLowerInvariant()
            });
        }

        var generation = new InstalledSourceGeneration
        {
            ReleaseVersion = attestation.ReleaseVersion,
            SourceSha256 = attestation.SourceSha256.ToLowerInvariant(),
            SourceManifestSha256 = attestation.SourceManifestSha256.ToLowerInvariant(),
            SourceManifestKeyId = attestation.SourceManifestKeyId,
            SigningProfile = attestation.SigningProfile,
            ProductSignerThumbprint = attestation.ProductSignerThumbprint,
            SdkVersion = attestation.SdkVersion,
            RuntimeVersion = attestation.RuntimeVersion,
            TargetRuntime = attestation.TargetRuntime,
            Files = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList()
        };
        ValidateGeneration(generation);

        using var storeLease = AcquireStoreLease(cancellationToken);
        var store = ReadProtectedStore();
        if (store.Pending is not null)
            throw new InvalidOperationException(
                "A source-installation recovery transaction is still pending; source update trust cannot overlap it.");
        store.Installed = PruneInstalledGenerations(store.Installed);
        AddInstalledGeneration(store, generation, generation.Files);
        WriteProtectedStore(store);
    }

    public static SourceInstallationBinding RequireActiveInstallationBinding(Guid expectedInviteId)
    {
        if (expectedInviteId == Guid.Empty)
            throw new InvalidDataException("The expected source-installation invitation ID is empty.");
        lock (Gate)
        {
            if (_pendingPromotion is null
                || _activeStoreLease is null
                || string.IsNullOrEmpty(_pendingTransactionId)
                || _pendingInviteId != expectedInviteId
                || string.IsNullOrEmpty(_pendingInviteCiphertextSha256))
                throw new InvalidDataException(
                    "No matching authenticated source-build installation is active in this Setup process.");
            return new SourceInstallationBinding(
                _pendingTransactionId,
                _pendingInviteId,
                _pendingInviteCiphertextSha256,
                _pendingPromotion.SourceSha256,
                _pendingPromotion.SourceManifestSha256);
        }
    }

    public static void RollbackActiveInstallation()
    {
        var (candidate, transactionId, inviteId, inviteHash, storeLease) = GetActiveInstallation();
        if (candidate is null || string.IsNullOrEmpty(transactionId)) return;
        if (storeLease is null) throw new InvalidOperationException("The source-build installation lease is missing.");
        var store = ReadProtectedStore();
        RequirePendingMatches(store, candidate, transactionId, inviteId, inviteHash);
        if (MachineInstallTransactionPersistence.Load() is not null)
            throw new InvalidOperationException(
                "Machine enrollment has external side effects under protected recovery; pending source trust was preserved for roll-forward recovery.");
        if (AgentInstallTransactionPersistence.Load() is not null)
            throw new InvalidOperationException(
                "The Agent filesystem transaction is still active; pending source trust was preserved for recovery.");
        foreach (var pendingFile in candidate.Files.Where(file => File.Exists(file.Path)))
        {
            try { VerifyHashAsync(pendingFile.Path, pendingFile.Size, pendingFile.Sha256, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { continue; }
            if (!store.Installed.SelectMany(generation => generation.Files).Any(installed =>
                    installed.Path.Equals(pendingFile.Path, StringComparison.OrdinalIgnoreCase)
                    && installed.Size == pendingFile.Size && FixedHash(installed.Sha256, pendingFile.Sha256)))
                throw new InvalidOperationException(
                    "A pending source-built file remains installed; pending trust was preserved for filesystem recovery.");
        }
        store.Pending = null;
        store.PendingTransactionId = string.Empty;
        store.PendingInviteId = Guid.Empty;
        store.PendingInviteCiphertextSha256 = string.Empty;
        store.Installed = PruneInstalledGenerations(store.Installed);
        if (store.Installed.Count == 0)
        {
            if (File.Exists(ProvenanceFile)) File.Delete(ProvenanceFile);
        }
        else WriteProtectedStore(store);
        ReleaseActiveInstallation(candidate, storeLease);
    }

    private static (
        InstalledSourceGeneration? Candidate,
        string TransactionId,
        Guid InviteId,
        string InviteHash,
        FileStream? StoreLease) GetActiveInstallation()
    {
        lock (Gate)
            return (_pendingPromotion, _pendingTransactionId, _pendingInviteId,
                _pendingInviteCiphertextSha256, _activeStoreLease);
    }

    private static void RequirePendingMatches(
        InstalledSourceProvenance store,
        InstalledSourceGeneration candidate,
        string transactionId,
        Guid inviteId,
        string inviteHash)
    {
        if (store.PendingTransactionId != transactionId
            || store.PendingInviteId != inviteId
            || !FixedHash(store.PendingInviteCiphertextSha256, inviteHash)
            || store.Pending is null
            || !SameGeneration(store.Pending, candidate))
            throw new InvalidDataException("The pending source-build provenance changed before completion.");
    }

    private static List<InstalledSourceFile> FilesBelow(
        InstalledSourceGeneration generation,
        string componentDirectory)
    {
        var root = Path.GetFullPath(componentDirectory).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return generation.Files.Where(file => file.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddInstalledGeneration(
        InstalledSourceProvenance store,
        InstalledSourceGeneration template,
        List<InstalledSourceFile> files)
    {
        var generation = new InstalledSourceGeneration
        {
            ReleaseVersion = template.ReleaseVersion,
            SourceSha256 = template.SourceSha256,
            SourceManifestSha256 = template.SourceManifestSha256,
            SourceManifestKeyId = template.SourceManifestKeyId,
            SigningProfile = template.SigningProfile,
            ProductSignerThumbprint = template.ProductSignerThumbprint,
            SdkVersion = template.SdkVersion,
            RuntimeVersion = template.RuntimeVersion,
            TargetRuntime = template.TargetRuntime,
            Files = files.Select(file => new InstalledSourceFile
            {
                Path = file.Path,
                Size = file.Size,
                Sha256 = file.Sha256
            }).ToList()
        };
        if (!store.Installed.Any(existing => SameGeneration(existing, generation)))
            store.Installed.Add(generation);
    }

    private static void ReleaseActiveInstallation(InstalledSourceGeneration candidate, FileStream storeLease)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_pendingPromotion, candidate))
            {
                _activeFiles = null;
                _activeControllerFiles = null;
                _pendingPromotion = null;
                _pendingTransactionId = string.Empty;
                _pendingInviteId = Guid.Empty;
                _pendingInviteCiphertextSha256 = string.Empty;
                _activeStoreLease = null;
            }
        }
        storeLease.Dispose();
    }

    private static bool SameGeneration(InstalledSourceGeneration left, InstalledSourceGeneration right)
    {
        if (left.ReleaseVersion != right.ReleaseVersion || left.SourceSha256 != right.SourceSha256
            || left.SourceManifestSha256 != right.SourceManifestSha256 || left.SourceManifestKeyId != right.SourceManifestKeyId
            || left.SigningProfile != right.SigningProfile || left.ProductSignerThumbprint != right.ProductSignerThumbprint
            || left.SdkVersion != right.SdkVersion || left.RuntimeVersion != right.RuntimeVersion
            || left.TargetRuntime != right.TargetRuntime || left.Files.Count != right.Files.Count) return false;
        var expected = right.Files.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        return left.Files.All(item => expected.TryGetValue(item.Path, out var match)
                                 && item.Size == match.Size
                                 && FixedHash(item.Sha256, match.Sha256));
    }

    private static List<InstalledSourceGeneration> PruneInstalledGenerations(
        IEnumerable<InstalledSourceGeneration> generations)
    {
        var retained = new List<InstalledSourceGeneration>();
        foreach (var generation in generations)
        {
            ValidateGeneration(generation);
            if (!generation.Files.Any(HasLiveTrustedLocation)) continue;
            if (!retained.Any(existing => SameGeneration(existing, generation))) retained.Add(generation);
        }
        if (retained.Count > 8)
            throw new InvalidDataException("Too many live source-build provenance generations require recovery.");
        return retained;
    }

    private static bool HasLiveTrustedLocation(InstalledSourceFile file)
    {
        foreach (var path in EnumerateLiveLocations(file.Path))
        {
            if (!File.Exists(path)) continue;
            try
            {
                VerifyHashAsync(path, file.Size, file.Sha256, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (InvalidDataException) { }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateLiveLocations(string canonicalPath)
    {
        yield return canonicalPath;
        var adminRoot = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, "Admin"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (canonicalPath.StartsWith(adminRoot, StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(AppPaths.InstallDirectory, "Admin.previous", canonicalPath[adminRoot.Length..]);

        var guardian = Path.GetFullPath(Path.Combine(
            AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe"));
        if (canonicalPath.Equals(guardian, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(AppPaths.UpdateGuardianInstallDirectory))
        {
            var transactionFiles = Directory.EnumerateFiles(
                    AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe.*", SearchOption.TopDirectoryOnly)
                .Where(path => GuardianTransactionFilePattern.IsMatch(Path.GetFileName(path)))
                .Take(17).ToArray();
            if (transactionFiles.Length > 16)
                throw new InvalidDataException("Too many stable Guardian transaction files require recovery.");
            foreach (var transactionFile in transactionFiles) yield return transactionFile;
        }

        var agentRoot = Path.GetFullPath(AppPaths.AgentInstallDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(agentRoot, StringComparison.OrdinalIgnoreCase)) yield break;
        var relative = canonicalPath[agentRoot.Length..];
        AgentInstallTransactionJournal? setupJournal = null;
        try { setupJournal = AgentInstallTransactionPersistence.Load(); } catch { }
        if (setupJournal is not null)
        {
            yield return Path.Combine(AgentInstallTransactionPersistence.CandidateDirectory(setupJournal.OperationId), relative);
            yield return Path.Combine(AgentInstallTransactionPersistence.RollbackDirectory(setupJournal.OperationId), relative);
            yield return Path.Combine(AgentInstallTransactionPersistence.FailedDirectory(setupJournal.OperationId), relative);
        }
        UpdateJournal? updateJournal = null;
        try { updateJournal = UpdateJournalPersistence.Load(); } catch { }
        if (updateJournal is null || updateJournal.OperationId == Guid.Empty) yield break;
        var suffix = updateJournal.OperationId.ToString("N");
        yield return Path.Combine(AppPaths.AgentInstallDirectory + ".candidate-" + suffix, relative);
        yield return Path.Combine(AppPaths.AgentInstallDirectory + ".rollback-" + suffix, relative);
        yield return Path.Combine(AppPaths.AgentInstallDirectory + ".failed-" + suffix, relative);
    }

    private static async Task ValidatePinsAsync(SourceBuildAttestation attestation, string invitePath, InvitePayload invite,
        CancellationToken cancellationToken)
    {
        if (attestation.SchemaVersion != 3 || invite.SchemaVersion != InvitationPolicy.HostedLinkSchemaVersion
            || !string.Equals(invite.InstallProtocol, InvitationPolicy.SourceInstallProtocol, StringComparison.Ordinal)
            || attestation.ReleaseVersion != invite.ReleaseVersion || attestation.SourceFile != invite.SourceFile
            || attestation.SourceSize != invite.SourceSize || attestation.SdkVersion != invite.SdkVersion
            || attestation.RuntimeVersion != invite.RuntimeVersion || attestation.SourceManifestKeyId != invite.SourceManifestKeyId
            || attestation.SigningProfile != invite.SigningProfile
            || attestation.SigningProfile != BuildSigningTrust.ProfileName
            || !BuildSigningTrust.IsPublishable
            || attestation.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || attestation.ProductSignerThumbprint != invite.ProductSignerThumbprint
            || attestation.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || !invite.TargetRuntimes.Contains(attestation.TargetRuntime, StringComparer.Ordinal)
            || attestation.TargetRuntime != CurrentTargetRuntime()
            || !FixedHash(attestation.SourceSha256, invite.SourceSha256)
            || !FixedHash(attestation.SourceManifestSha256, invite.SourceManifestSha256))
            throw new InvalidDataException("The elevated source-build attestation does not match the signed invitation pins.");
        await using var inviteStream = new FileStream(invitePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        var inviteHash = Convert.ToHexString(await SHA256.HashDataAsync(inviteStream, cancellationToken)).ToLowerInvariant();
        if (!FixedHash(inviteHash, attestation.InviteCiphertextSha256))
            throw new InvalidDataException("The elevated Setup invitation does not match the source-build attestation.");
    }

    private static string CurrentTargetRuntime() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => string.Empty
    };

    private static string? MapInstalledPath(string relative)
    {
        foreach (var mapping in new[]
                 {
                     (Prefix: "Payload/Agent/", Root: AppPaths.AgentInstallDirectory),
                     (Prefix: "Payload/UpdateGuardian/", Root: AppPaths.UpdateGuardianInstallDirectory),
                     (Prefix: "Payload/Admin/", Root: Path.Combine(AppPaths.InstallDirectory, "Admin"))
                 })
        {
            if (!relative.StartsWith(mapping.Prefix, StringComparison.Ordinal)) continue;
            return Path.GetFullPath(Path.Combine(mapping.Root,
                relative[mapping.Prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
        }
        return null;
    }

    private static InstalledSourceFile? ResolveActiveControllerStaging(string path)
    {
        if (_activeControllerFiles is null) return null;
        var parent = Path.GetFullPath(AppPaths.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = Path.GetRelativePath(parent, path).Replace('\\', '/');
        var slash = relative.IndexOf('/');
        if (slash <= 0 || !ControllerTransactionPattern.IsMatch(relative[..slash])) return null;
        return _activeControllerFiles.GetValueOrDefault(relative[(slash + 1)..]);
    }

    private static IEnumerable<string> ResolveCanonicalTrustPaths(string path)
    {
        UpdateJournal? journal = null;
        try { journal = UpdateJournalPersistence.Load(); } catch { }
        foreach (var canonical in ResolveCanonicalTrustPaths(path, journal))
            yield return canonical;
    }

    // Keeping the journal-dependent mapping pure lets the source-update
    // transaction be exercised without reading mutable machine state in a
    // unit test.  The production caller above is still the only entrypoint
    // that obtains its journal from protected storage.
    private static IEnumerable<string> ResolveCanonicalTrustPaths(string path, UpdateJournal? journal)
    {
        yield return path;
        var installRoot = Path.GetFullPath(AppPaths.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        if (path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(installRoot, path).Replace('\\', '/');
            var slash = relative.IndexOf('/');
            if (slash > 0)
            {
                var leaf = relative[..slash];
                if (leaf.Equals("Admin.previous", StringComparison.OrdinalIgnoreCase)
                    || ControllerTransactionPattern.IsMatch(leaf))
                    yield return Path.GetFullPath(Path.Combine(
                        AppPaths.InstallDirectory, "Admin", relative[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        var guardian = Path.GetFullPath(Path.Combine(
            AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe"));
        foreach (var marker in new[] { ".upgrade-", ".backup-", ".failed-" })
        {
            var prefix = guardian + marker;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(path[prefix.Length..], "N", out _))
                yield return guardian;
        }

        AgentInstallTransactionJournal? setupJournal = null;
        try { setupJournal = AgentInstallTransactionPersistence.Load(); } catch { }
        if (setupJournal is not null)
        {
            foreach (var transactionRoot in new[]
                     {
                         AgentInstallTransactionPersistence.CandidateDirectory(setupJournal.OperationId),
                         AgentInstallTransactionPersistence.RollbackDirectory(setupJournal.OperationId),
                         AgentInstallTransactionPersistence.FailedDirectory(setupJournal.OperationId)
                     })
            {
                var prefix = Path.GetFullPath(transactionRoot).TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = path[prefix.Length..];
                yield return Path.GetFullPath(Path.Combine(AppPaths.AgentInstallDirectory, relative));
            }
        }

        if (journal is null || journal.OperationId == Guid.Empty) yield break;
        if (journal.SchemaVersion == 2 && journal.DeliveryMode == UpdateDeliveryMode.SourceArchive)
        {
            var operation = Path.GetFullPath(Path.Combine(
                AppPaths.UpdateDataDirectory, journal.OperationId.ToString("N")));
            var sourceBuild = Path.GetFullPath(Path.Combine(operation, "source-build"));
            if (!string.Equals(Path.GetFullPath(journal.SourceBuildOutputDirectory), sourceBuild,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The source update journal has an unsafe source-build output path.");

            var stagedAgent = Path.Combine(sourceBuild, "Payload", "Agent");
            var stagedAgentPrefix = stagedAgent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(stagedAgentPrefix, StringComparison.OrdinalIgnoreCase))
                yield return Path.GetFullPath(Path.Combine(
                    AppPaths.AgentInstallDirectory, path[stagedAgentPrefix.Length..]));

            var stagedGuardian = Path.Combine(sourceBuild, "Payload", "UpdateGuardian", "Taildesk.UpdateGuardian.exe");
            if (path.Equals(stagedGuardian, StringComparison.OrdinalIgnoreCase))
                yield return guardian;
        }
        var suffix = journal.OperationId.ToString("N");
        foreach (var transactionRoot in new[]
                 {
                     AppPaths.AgentInstallDirectory + ".candidate-" + suffix,
                     AppPaths.AgentInstallDirectory + ".rollback-" + suffix,
                     AppPaths.AgentInstallDirectory + ".failed-" + suffix
                 })
        {
            var prefix = Path.GetFullPath(transactionRoot).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = path[prefix.Length..];
            yield return Path.GetFullPath(Path.Combine(AppPaths.AgentInstallDirectory, relative));
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("The source-build attestation contains an unsafe path.");
        return normalized;
    }

    private static bool FixedHash(string left, string right)
    {
        if (!Sha256Pattern.IsMatch(left) || !Sha256Pattern.IsMatch(right)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static async Task VerifyHashAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        RejectReparsePoints(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size) throw new InvalidDataException("An attested source-build file has the wrong size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(sha256)))
            throw new InvalidDataException("An attested source-build file hash is invalid.");
    }

    private static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The source-build trust path contains a reparse point.");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }

    private static void WriteProtectedStore(InstalledSourceProvenance store)
    {
        ValidateStore(store);
        EnsureStoreDirectory();
        if (File.Exists(ProvenanceFile))
            RequireRestrictedSecurity(
                new FileInfo(ProvenanceFile).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access),
                isDirectory: false);

        var temporary = ProvenanceFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(store, JsonDefaults.Options);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        var fileSecurity = CreateRestrictedFileSecurity();
        new FileInfo(temporary).SetAccessControl(fileSecurity);
        File.Move(temporary, ProvenanceFile, overwrite: true);
    }

    private static InstalledSourceProvenance ReadProtectedStore()
    {
        if (!File.Exists(ProvenanceFile)) return new InstalledSourceProvenance();
        RejectReparsePoints(ProvenanceDirectory);
        var directorySecurity = new DirectoryInfo(ProvenanceDirectory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var fileSecurity = new FileInfo(ProvenanceFile).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        RequireRestrictedSecurity(directorySecurity, isDirectory: true);
        RequireRestrictedSecurity(fileSecurity, isDirectory: false);
        using var stream = new FileStream(
            ProvenanceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The machine source-build provenance store has an invalid size.");
        var store = JsonSerializer.Deserialize<InstalledSourceProvenance>(stream, JsonDefaults.Options)
                    ?? throw new InvalidDataException("The machine source-build provenance store is empty.");
        ValidateStore(store);
        return store;
    }

    private static void ValidateStore(InstalledSourceProvenance store)
    {
        var hasPendingContext = !string.IsNullOrEmpty(store.PendingTransactionId)
                                || store.PendingInviteId != Guid.Empty
                                || !string.IsNullOrEmpty(store.PendingInviteCiphertextSha256);
        if (store.SchemaVersion != 5
            || store.Installed is null
            || store.Installed.Count > 8
            || (store.Installed.Count == 0 && store.Pending is null)
            || (store.Pending is null) != !hasPendingContext
            || (store.Pending is not null
                && (!Regex.IsMatch(store.PendingTransactionId, "^[a-f0-9]{32}$")
                    || store.PendingInviteId == Guid.Empty
                    || !Sha256Pattern.IsMatch(store.PendingInviteCiphertextSha256))))
            throw new InvalidDataException("The machine source-build provenance store is invalid.");
        foreach (var installed in store.Installed) ValidateGeneration(installed);
        if (store.Pending is not null) ValidateGeneration(store.Pending);
        if (store.Installed.Sum(generation => generation.Files.Count) + (store.Pending?.Files.Count ?? 0) > 4096)
            throw new InvalidDataException("The machine source-build provenance store is too large.");
    }

    private static void ValidateGeneration(InstalledSourceGeneration generation)
    {
        if (generation.SigningProfile != BuildSigningTrust.ProfileName
            || !BuildSigningTrust.IsPublishable
            || generation.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || generation.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || !Regex.IsMatch(generation.ReleaseVersion, "^[1-9][0-9]*\\.[0-9]+\\.[0-9]+$")
            || !Sha256Pattern.IsMatch(generation.SourceSha256)
            || !Sha256Pattern.IsMatch(generation.SourceManifestSha256)
            || generation.SdkVersion != DotNetSdkPolicy.SignedPolicy
            || generation.RuntimeVersion != "10.0.10"
            || generation.TargetRuntime is not ("win-x64" or "win-arm64")
            || generation.Files.Count is < 1 or > 512)
            throw new InvalidDataException("A machine source-build provenance generation is invalid.");
        ValidateFileSet(generation.Files);
    }

    private static void EnsureStoreDirectory()
    {
        var directorySecurity = CreateRestrictedDirectorySecurity();
        var directory = new DirectoryInfo(ProvenanceDirectory);
        if (!directory.Exists)
        {
            try { directory.Create(directorySecurity); }
            catch (IOException) when (Directory.Exists(ProvenanceDirectory)) { }
        }
        RejectReparsePoints(ProvenanceDirectory);
        RequireRestrictedSecurity(
            directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            isDirectory: true);
    }

    private static FileStream AcquireStoreLease(CancellationToken cancellationToken)
    {
        EnsureStoreDirectory();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(ProvenanceLockFile))
                    throw new InvalidDataException("The machine source-build provenance lock is a directory.");

                if (!File.Exists(ProvenanceLockFile))
                {
                    try
                    {
                        var created = FileSystemAclExtensions.Create(
                            new FileInfo(ProvenanceLockFile),
                            FileMode.CreateNew,
                            FileSystemRights.FullControl,
                            FileShare.None,
                            1,
                            FileOptions.WriteThrough,
                            CreateStoreLockSecurity());
                        try
                        {
                            RequireStoreLockSecurity();
                            return created;
                        }
                        catch
                        {
                            created.Dispose();
                            throw;
                        }
                    }
                    catch (IOException) when (File.Exists(ProvenanceLockFile))
                    {
                        // Another elevated Setup atomically created the protected lock first.
                    }
                }

                if ((File.GetAttributes(ProvenanceLockFile) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The machine source-build provenance lock is a reparse point.");
                RequireStoreLockSecurity();
                return new FileStream(ProvenanceLockFile, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private static void RequireStoreLockSecurity()
    {
        var security = new FileInfo(ProvenanceLockFile).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException("The source-build provenance lock owner is not SYSTEM or Administrators.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The source-build provenance lock inherits unsafe permissions.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2
            || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasExactRule(rules, SystemSid, FileSystemRights.FullControl, InheritanceFlags.None)
            || !HasExactRule(rules, AdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.None))
            throw new UnauthorizedAccessException(
                "The source-build provenance lock is not restricted to SYSTEM and Administrators.");
    }

    private static void ValidateFileSet(IEnumerable<InstalledSourceFile> files)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string path;
            try { path = Path.GetFullPath(file.Path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException("The machine source-build provenance contains an invalid path.", exception);
            }
            if (!path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(path)
                || file.Size <= 0
                || !Sha256Pattern.IsMatch(file.Sha256)
                || !IsExpectedInstalledPath(path))
                throw new InvalidDataException("The machine source-build provenance contains an invalid file declaration.");
        }
    }

    private static bool IsExpectedInstalledPath(string path)
    {
        foreach (var root in new[]
                 {
                     AppPaths.AgentInstallDirectory,
                     AppPaths.UpdateGuardianInstallDirectory,
                     Path.Combine(AppPaths.InstallDirectory, "Admin")
                 })
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static DirectorySecurity CreateRestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateRestrictedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateStoreLockSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void RequireRestrictedSecurity(FileSystemSecurity security, bool isDirectory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)))
            throw new UnauthorizedAccessException("The machine source-build provenance owner is not SYSTEM or Administrators.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The machine source-build provenance ACL inherits unsafe permissions.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (rules.Length != 3
            || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasExactRule(rules, SystemSid, FileSystemRights.FullControl, inheritance)
            || !HasExactRule(rules, AdministratorsSid, FileSystemRights.FullControl, inheritance)
            || !HasExactRule(rules, UsersSid, FileSystemRights.ReadAndExecute, inheritance))
            throw new UnauthorizedAccessException(
                "The machine source-build provenance ACL is not the exact integrity-protected, read-only-public ACL.");
    }

    private static bool HasExactRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier sid,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        rules.Any(rule => rule.IdentityReference.Equals(sid)
                          && rule.FileSystemRights == rights
                          && rule.InheritanceFlags == inheritance
                          && rule.PropagationFlags == PropagationFlags.None);
}
