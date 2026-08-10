using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taildesk.Shared;

/// <summary>
/// Verifies the single immutable source archive used by the source-update
/// protocol.  This class intentionally does not download or execute anything:
/// the Agent owns the protected download and local build, while Guardian calls
/// the same checks again before it swaps the active Agent directory.
/// </summary>
public static class SourceUpdatePackageVerifier
{
    public const string ManifestEntryName = "source-manifest.json";
    public const string SignatureEntryName = "source-manifest.sig";
    private const int MaximumEntries = 4096;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumSignatureBytes = 16 * 1024;
    private const int MaximumAttestationBytes = 4 * 1024 * 1024;
    private static readonly Regex HashPattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ThumbprintPattern = new("^[A-F0-9]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex CloudFrontHostPattern = new(
        "^[a-z0-9-]+\\.cloudfront\\.net$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static void ValidateRequest(SourceUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != SourceUpdateProtocol.Version || request.OperationId == Guid.Empty)
            throw new InvalidDataException("The source update request uses an unsupported protocol.");
        var version = CanonicalVersion(request.TargetVersion);
        if (!Enum.IsDefined(request.Role))
            throw new InvalidDataException("The source update request has an unsupported device role.");
        if (request.Architecture is not ("x64" or "arm64"))
            throw new InvalidDataException("The source update request has an unsupported architecture.");
        var expectedRuntime = request.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)
            ? "win-x64"
            : "win-arm64";
        if (!request.TargetRuntime.Equals(expectedRuntime, StringComparison.Ordinal))
            throw new InvalidDataException("The source update runtime does not match the requested architecture.");
        if (!request.SourceFile.Equals($"opticon-source-{version}.zip", StringComparison.Ordinal)
            || request.SourceSize is < 1024 or > MaximumArchiveBytes
            || !IsHash(request.SourceSha256)
            || !IsHash(request.SourceManifestSha256)
            || !ThumbprintPattern.IsMatch(request.SourceManifestKeyId)
            || !ThumbprintPattern.IsMatch(request.ProductSignerThumbprint)
            || request.SourceManifestKeyId.Equals(InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
            || request.ProductSignerThumbprint.Equals(InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
            || request.SourceManifestKeyId.Equals(request.ProductSignerThumbprint, StringComparison.OrdinalIgnoreCase)
            || !request.SourceManifestKeyId.Equals(SourceReleaseSigning.KeyId, StringComparison.Ordinal)
            || !request.ProductSignerThumbprint.Equals(ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
            || !request.SigningProfile.Equals(BuildSigningTrust.ProfileName, StringComparison.Ordinal)
            || !BuildSigningTrust.IsPublishable
            || !request.SdkVersion.Equals(SourceUpdateProtocol.RequiredSdkVersion, StringComparison.Ordinal)
            || !request.RuntimeVersion.Equals(SourceUpdateProtocol.RequiredRuntimeVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The source update request has invalid immutable build pins.");
        RequireImmutableCloudFrontUrl(request.DownloadUrl, version, request.SourceFile);
    }

    public static async Task<SourceArchiveManifest> VerifyArchiveAsync(
        string archivePath,
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        RequireExactArchiveFile(archivePath, request);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = CollectEntries(archive);
        var (manifest, declared) = await ReadAndValidateManifestAsync(entries, request, cancellationToken);
        await VerifyDeclaredEntriesAsync(entries, declared, cancellationToken);
        return manifest;
    }

    public static async Task<SourceArchiveManifest> VerifyAndExtractAsync(
        string archivePath,
        string destination,
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        RequireExactArchiveFile(archivePath, request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidDataException("The protected source extraction directory is not empty.");
        if (File.Exists(destination))
            throw new InvalidDataException("The protected source extraction path is a file.");
        Directory.CreateDirectory(destination);
        EnsureNoReparseTraversal(destinationRoot, destinationRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        var entries = CollectEntries(archive);
        var (manifest, declared) = await ReadAndValidateManifestAsync(entries, request, cancellationToken);
        foreach (var file in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = SafeDestination(destinationRoot, file.Path);
            var parent = Path.GetDirectoryName(output)
                         ?? throw new InvalidDataException("A source archive output has no parent directory.");
            Directory.CreateDirectory(parent);
            EnsureNoReparseTraversal(destinationRoot, parent);
            await ExtractEntryAsync(entries[file.Path], output, file.Size, file.Sha256, cancellationToken);
        }
        await VerifyExtractedFileSetAsync(destinationRoot, declared, cancellationToken);
        return manifest;
    }

    /// <summary>
    /// Revalidates a sealed local build record and all output hashes.  The
    /// provenance store is updated only after these checks, so an unsigned
    /// source-built Agent/Guardian can be trusted by its exact protected path.
    /// </summary>
    public static async Task<SourceUpdateBuildAttestation> VerifyBuiltOutputAsync(
        string attestationPath,
        string outputDirectory,
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(attestationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var bytes = MachineStorageSecurity.ReadRestrictedFile(attestationPath, MaximumAttestationBytes);
        SourceUpdateBuildAttestation attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<SourceUpdateBuildAttestation>(bytes, JsonDefaults.Options)
                          ?? throw new InvalidDataException("The source build attestation is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The source build attestation is malformed.", exception);
        }

        ValidateAttestationPins(attestation, request);
        var outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!Directory.Exists(outputRoot))
            throw new DirectoryNotFoundException("The source build output directory is missing.");
        EnsureNoReparseTraversal(outputRoot, outputRoot);
        var files = ValidateBuildFileDeclarations(attestation);
        await VerifyOutputFilesAsync(outputRoot, files, cancellationToken);
        VerifyBuiltExecutableVersion(
            SafeDestination(outputRoot, "Payload/Agent/Taildesk.Agent.exe"), request.TargetVersion, "Agent");
        VerifyBuiltExecutableVersion(
            SafeDestination(outputRoot, "Payload/UpdateGuardian/Taildesk.UpdateGuardian.exe"), request.TargetVersion, "Guardian");
        await SourceBuildProvenance.RegisterVerifiedSourceUpdateAsync(
            attestation, outputRoot, request.OperationId, cancellationToken);
        return attestation;
    }

    /// <summary>
    /// Copies one verified output component to a Guardian-owned candidate
    /// directory.  Copying (rather than moving) makes the candidate independent
    /// of the source build work tree and lets the Guardian rehash each byte.
    /// </summary>
    public static async Task CopyVerifiedComponentAsync(
        string outputDirectory,
        string componentPrefix,
        string destination,
        SourceUpdateBuildAttestation attestation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var normalizedPrefix = NormalizePath(componentPrefix).TrimEnd('/') + "/";
        var files = ValidateBuildFileDeclarations(attestation)
            .Where(file => file.Path.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .Select(file => new SourceUpdateBuildFile
            {
                Path = file.Path[normalizedPrefix.Length..],
                Size = file.Size,
                Sha256 = file.Sha256
            })
            .ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("The source build attestation lacks the requested component.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidDataException("The Guardian candidate destination is already occupied.");

        var sourceRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        EnsureNoReparseTraversal(sourceRoot, sourceRoot);
        Directory.CreateDirectory(destination);
        EnsureNoReparseTraversal(destinationRoot, destinationRoot);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = SafeDestination(sourceRoot, normalizedPrefix + file.Path);
            var target = SafeDestination(destinationRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            EnsureNoReparseTraversal(destinationRoot, Path.GetDirectoryName(target)!);
            await CopyAndVerifyAsync(source, target, file.Size, file.Sha256, cancellationToken);
        }
        await VerifyOutputFilesAsync(destinationRoot, files, cancellationToken);
    }

    public static string ExpectedBuildScriptPath(string extractedSourceDirectory) =>
        SafeDestination(Path.GetFullPath(extractedSourceDirectory).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar, SourceUpdateProtocol.SourceBuildScriptName);

    private static void RequireExactArchiveFile(string archivePath, SourceUpdateRequest request)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The hash-pinned source archive is missing.", archivePath);
        var info = new FileInfo(archivePath);
        if (info.Length != request.SourceSize)
            throw new InvalidDataException("The source archive size does not match the approved release.");
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var actual = SHA256.HashData(stream);
        if (!FixedHash(Convert.ToHexString(actual), request.SourceSha256))
            throw new InvalidDataException("The source archive SHA-256 does not match the approved release.");
    }

    private static Dictionary<string, ZipArchiveEntry> CollectEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is < 3 or > MaximumEntries)
            throw new InvalidDataException("The source archive entry count is invalid.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = NormalizePath(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')
                || !entries.TryAdd(path, entry))
                throw new InvalidDataException("The source archive contains a duplicate or undeclared directory path.");
        }
        return entries;
    }

    private static async Task<(SourceArchiveManifest Manifest, SourceArchiveFile[] Declared)> ReadAndValidateManifestAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        SourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry)
            || !entries.TryGetValue(SignatureEntryName, out var signatureEntry)
            || manifestEntry.Length is <= 0 or > MaximumManifestBytes
            || signatureEntry.Length is <= 0 or > MaximumSignatureBytes)
            throw new InvalidDataException("The source archive lacks bounded signed release metadata.");
        var manifestBytes = await ReadEntryAsync(manifestEntry, MaximumManifestBytes, cancellationToken);
        if (!FixedHash(Convert.ToHexString(SHA256.HashData(manifestBytes)), request.SourceManifestSha256))
            throw new InvalidDataException("The source archive manifest hash does not match the approved release.");
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(
                Encoding.UTF8.GetString(await ReadEntryAsync(signatureEntry, MaximumSignatureBytes, cancellationToken)).Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The source archive manifest signature is malformed.", exception);
        }
        if (!SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The source archive manifest signature is invalid.");
        SourceArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceArchiveManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The source archive manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The source archive manifest is malformed.", exception);
        }
        ValidateManifestPins(manifest, request);
        var declared = new Dictionary<string, SourceArchiveFile>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var file in manifest.Files)
        {
            var normalized = NormalizePath(file.Path);
            if (!declared.TryAdd(normalized, file)
                || !entries.TryGetValue(normalized, out var entry)
                || file.Size <= 0 || entry.Length != file.Size || !IsHash(file.Sha256))
                throw new InvalidDataException("The source archive manifest has an invalid file declaration.");
            expanded = checked(expanded + file.Size);
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The source archive expands beyond its safe limit.");
        }
        if (declared.Count is < 6 or > MaximumEntries - 2
            || !declared.ContainsKey(SourceUpdateProtocol.SourceBuildScriptName)
            || !declared.ContainsKey("global.json")
            || !declared.ContainsKey("NuGet.Config")
            || !declared.ContainsKey("Directory.Build.props")
            || !declared.ContainsKey("Directory.Build.targets")
            || !declared.ContainsKey("src/Taildesk.Agent/Taildesk.Agent.csproj")
            || !declared.ContainsKey("src/Taildesk.UpdateGuardian/Taildesk.UpdateGuardian.csproj"))
            throw new InvalidDataException("The source archive lacks the required offline Agent and Guardian build inputs.");
        if (entries.Count != declared.Count + 2
            || entries.Keys.Any(path => !path.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)
                                      && !path.Equals(SignatureEntryName, StringComparison.OrdinalIgnoreCase)
                                      && !declared.ContainsKey(path)))
            throw new InvalidDataException("The source archive contains undeclared extra files.");
        return (manifest, declared.Values
            .Select(file => new SourceArchiveFile
            {
                Path = NormalizePath(file.Path), Size = file.Size, Sha256 = file.Sha256.ToLowerInvariant()
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateManifestPins(SourceArchiveManifest manifest, SourceUpdateRequest request)
    {
        var version = CanonicalVersion(request.TargetVersion);
        if (manifest.SchemaVersion != 1
            || !CanonicalVersion(manifest.Version).Equals(version, StringComparison.Ordinal)
            || !manifest.SigningProfile.Equals(request.SigningProfile, StringComparison.Ordinal)
            || !manifest.SourceReleaseKeyId.Equals(request.SourceManifestKeyId, StringComparison.Ordinal)
            || !manifest.SourceReleaseKeyId.Equals(SourceReleaseSigning.KeyId, StringComparison.Ordinal)
            || !manifest.ProductSignerThumbprint.Equals(request.ProductSignerThumbprint, StringComparison.Ordinal)
            || !manifest.ProductSignerThumbprint.Equals(ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
            || !manifest.SdkVersion.Equals(request.SdkVersion, StringComparison.Ordinal)
            || !manifest.RuntimeVersion.Equals(request.RuntimeVersion, StringComparison.Ordinal)
            || !manifest.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal)
            || !manifest.TargetRuntimes.Contains(request.TargetRuntime, StringComparer.Ordinal)
            || manifest.Files.Count is < 1 or > MaximumEntries - 2
            || !CertificateBytesMatch(manifest.SourceReleaseCertificateBase64, SourceReleaseSigning.PinnedCertificate)
            || !CertificateBytesMatch(manifest.ProductSigningCertificateBase64, ProductSigning.PinnedCertificate))
            throw new InvalidDataException("The signed source archive manifest does not match the approved source update.");
    }

    private static void ValidateAttestationPins(SourceUpdateBuildAttestation attestation, SourceUpdateRequest request)
    {
        if (attestation.SchemaVersion != 1
            || !CanonicalVersion(attestation.ReleaseVersion).Equals(CanonicalVersion(request.TargetVersion), StringComparison.Ordinal)
            || !attestation.SourceFile.Equals(request.SourceFile, StringComparison.Ordinal)
            || attestation.SourceSize != request.SourceSize
            || !FixedHash(attestation.SourceSha256, request.SourceSha256)
            || !FixedHash(attestation.SourceManifestSha256, request.SourceManifestSha256)
            || !attestation.SourceManifestKeyId.Equals(request.SourceManifestKeyId, StringComparison.Ordinal)
            || !attestation.SigningProfile.Equals(request.SigningProfile, StringComparison.Ordinal)
            || !attestation.ProductSignerThumbprint.Equals(request.ProductSignerThumbprint, StringComparison.Ordinal)
            || !attestation.SdkVersion.Equals(request.SdkVersion, StringComparison.Ordinal)
            || !attestation.RuntimeVersion.Equals(request.RuntimeVersion, StringComparison.Ordinal)
            || !attestation.TargetRuntime.Equals(request.TargetRuntime, StringComparison.Ordinal)
            || attestation.Role != request.Role
            || !attestation.Architecture.Equals(request.Architecture, StringComparison.OrdinalIgnoreCase)
            || attestation.Files.Count is < 2 or > 512)
            throw new InvalidDataException("The local source build attestation does not match the approved source archive.");
    }

    private static SourceUpdateBuildFile[] ValidateBuildFileDeclarations(SourceUpdateBuildAttestation attestation)
    {
        var declared = new Dictionary<string, SourceUpdateBuildFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in attestation.Files)
        {
            var path = NormalizePath(file.Path);
            if (!path.StartsWith("Payload/Agent/", StringComparison.Ordinal)
                && !path.StartsWith("Payload/UpdateGuardian/", StringComparison.Ordinal))
                throw new InvalidDataException("The source build attestation declares an unsupported component path.");
            if (!declared.TryAdd(path, new SourceUpdateBuildFile
                { Path = path, Size = file.Size, Sha256 = file.Sha256.ToLowerInvariant() })
                || file.Size <= 0 || !IsHash(file.Sha256))
                throw new InvalidDataException("The source build attestation has an invalid output declaration.");
            total = checked(total + file.Size);
            if (total > MaximumExpandedBytes)
                throw new InvalidDataException("The source build output exceeds its safe limit.");
        }
        if (!declared.ContainsKey("Payload/Agent/Taildesk.Agent.exe")
            || declared.Keys.Count(path => path.StartsWith("Payload/UpdateGuardian/", StringComparison.Ordinal)) != 1
            || !declared.ContainsKey("Payload/UpdateGuardian/Taildesk.UpdateGuardian.exe"))
            throw new InvalidDataException("The source build output must contain an Agent and exactly one Guardian executable.");
        return declared.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task VerifyDeclaredEntriesAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IEnumerable<SourceArchiveFile> declared,
        CancellationToken cancellationToken)
    {
        foreach (var file in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyEntryAsync(entries[file.Path], file.Size, file.Sha256, cancellationToken);
        }
    }

    private static async Task VerifyExtractedFileSetAsync(
        string destinationRoot,
        IEnumerable<SourceArchiveFile> declared,
        CancellationToken cancellationToken)
    {
        var expected = declared.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = EnumerateRegularFilesNoReparse(destinationRoot)
            .Select(path => NormalizePath(Path.GetRelativePath(destinationRoot, path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException("The source extraction contains missing or undeclared files.");
        foreach (var file in declared)
            await VerifyFileAsync(SafeDestination(destinationRoot, file.Path), file.Size, file.Sha256, cancellationToken);
    }

    private static async Task VerifyOutputFilesAsync(
        string outputRoot,
        IEnumerable<SourceUpdateBuildFile> declared,
        CancellationToken cancellationToken)
    {
        var expected = declared.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = EnumerateRegularFilesNoReparse(outputRoot)
            .Select(path => NormalizePath(Path.GetRelativePath(outputRoot, path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException("The local source build contains missing or unattested files.");
        foreach (var file in declared)
            await VerifyFileAsync(SafeDestination(outputRoot, file.Path), file.Size, file.Sha256, cancellationToken);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string output,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
                throw new InvalidDataException("A source archive entry exceeds its declared size.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
        if (total != expectedSize || !FixedHash(Convert.ToHexString(hash.GetHashAndReset()), expectedHash))
            throw new InvalidDataException("A source archive entry failed its signed hash check.");
    }

    private static async Task VerifyEntryAsync(
        ZipArchiveEntry entry,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (entry.Length != expectedSize)
            throw new InvalidDataException("A source archive entry size does not match its signed declaration.");
        await using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
                throw new InvalidDataException("A source archive entry exceeds its signed size.");
            hash.AppendData(buffer, 0, read);
        }
        if (total != expectedSize || !FixedHash(Convert.ToHexString(hash.GetHashAndReset()), expectedHash))
            throw new InvalidDataException("A source archive entry failed its signed hash check.");
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string target,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A local source build file is missing or a reparse point.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedSize)
            throw new InvalidDataException("A local source build file size changed before Guardian copied it.");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
                throw new InvalidDataException("A local source build file exceeds its attested size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        if (total != expectedSize || !FixedHash(Convert.ToHexString(hash.GetHashAndReset()), expectedHash))
            throw new InvalidDataException("A local source build file changed before Guardian copied it.");
    }

    private static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An expected source build file is missing or unsafe.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedSize)
            throw new InvalidDataException("An expected source build file has the wrong size.");
        var actual = await SHA256.HashDataAsync(input, cancellationToken);
        if (!FixedHash(Convert.ToHexString(actual), expectedHash))
            throw new InvalidDataException("An expected source build file hash is invalid.");
    }

    private static void VerifyBuiltExecutableVersion(string path, string expectedVersion, string component)
    {
        var reported = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty);
        if (!reported.Equals(CanonicalVersion(expectedVersion), StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The locally built {component} reports version {reported}, not {CanonicalVersion(expectedVersion)}.");
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > maximum)
            throw new InvalidDataException("A bounded source archive metadata entry is invalid.");
        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        var buffer = new byte[32 * 1024];
        long remaining = entry.Length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new InvalidDataException("A source archive metadata entry was truncated.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
            throw new InvalidDataException("A source archive metadata entry exceeds its declared size.");
        return output.ToArray();
    }

    private static void RequireImmutableCloudFrontUrl(string value, string version, string file)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !CloudFrontHostPattern.IsMatch(uri.Host)
            || !uri.AbsolutePath.Equals($"/opticon/releases/{version}/{Uri.EscapeDataString(file)}", StringComparison.Ordinal))
            throw new InvalidDataException("The source update request does not use its immutable CloudFront archive URL.");
    }

    private static string CanonicalVersion(string value)
    {
        var normalized = UpdatePackageVerifier.NormalizeVersion(value);
        _ = UpdatePackageVerifier.ParseVersion(normalized);
        return normalized;
    }

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("The source archive or build record contains an unsafe path.");
        return path;
    }

    private static string SafeDestination(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, NormalizePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A source archive path escaped its protected stage.");
        return path;
    }

    private static void EnsureNoReparseTraversal(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!current.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A source build path escaped its protected root.");
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A source build path contains a reparse point.");
            if (current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidDataException("A source build path escaped its protected root.");
        }
    }

    private static IEnumerable<string> EnumerateRegularFilesNoReparse(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        EnsureNoReparseTraversal(rootFull, rootFull);
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A source archive or build output contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                yield return entry;
            }
        }
    }

    private static bool CertificateBytesMatch(string value, X509Certificate2 expected)
    {
        try
        {
            var actual = Convert.FromBase64String(value);
            return actual.Length == expected.RawData.Length
                   && CryptographicOperations.FixedTimeEquals(actual, expected.RawData);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHash(string value) => HashPattern.IsMatch(value);

    private static bool FixedHash(string left, string right) =>
        IsHash(left) && IsHash(right)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}
