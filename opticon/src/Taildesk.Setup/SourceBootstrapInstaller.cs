using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal sealed class SourceReleaseManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string SourceReleaseKeyId { get; set; } = string.Empty;
    public string SourceReleaseCertificateBase64 { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string ProductSigningCertificateBase64 { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public List<string> TargetRuntimes { get; set; } = [];
    public List<SourceReleaseFile> Files { get; set; } = [];
}

internal sealed class SourceReleaseFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal static class SourceBootstrapInstaller
{
    private const string Origin = "https://taildesk-egokick-control.fly.dev";
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    internal static async Task<int> RunAsync(
        SourceBootstrapRequest bootstrap,
        string launcherPath,
        Action<string> report)
    {
        string directory;
        Exception? protectedHandoffFailure = null;
        try
        {
            directory = HostedBootstrapper.CreateProtectedHandoffDirectory();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException or IOException)
        {
            // The policy is inside the encrypted invitation, so an emergency release
            // must be able to retrieve that invitation before deciding whether a
            // broken protected-path check is fatal.
            protectedHandoffFailure = exception;
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OpticonBootstrapUnvalidated",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        var invitePath = Path.Combine(directory, "invite.tdinvite");
        using var client = DirectHttp.CreateClient(TimeSpan.FromMinutes(30));
        report("Downloading the encrypted Opticon invitation...");
        await HostedBootstrapper.DownloadAsync(client,
            $"{Origin}/opticon/i/{Uri.EscapeDataString(bootstrap.PublicId)}/invite.tdinvite", invitePath,
            expectedSize: null, maximumSize: 64 * 1024, expectedHash: null);
        var encryptedInvite = await File.ReadAllBytesAsync(invitePath);
        var signedEnvelope = HostedInviteFile.Decrypt(bootstrap.PrivateKey, encryptedInvite);
        InvitePayload invite;
        try { invite = HostedInviteFile.ReadWithEmbeddedValidationPolicy(signedEnvelope); }
        finally { CryptographicOperations.ZeroMemory(signedEnvelope); }
        var validation = ClientInstallValidationPolicy.Normalize(invite.ClientInstallValidation);
        invite.ClientInstallValidation = validation;
        if (protectedHandoffFailure is not null
            && validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
            throw new UnauthorizedAccessException(
                "The protected bootstrap handoff could not be created.", protectedHandoffFailure);
        if (validation.IsEnabled(ClientInstallValidationStep.InvitationConstraints))
            ValidateInvitation(invite);
        if (validation.IsEnabled(ClientInstallValidationStep.LauncherBinding))
        {
            if (!HostedBootstrapper.IsSourceLauncher(launcherPath))
                throw new InvalidDataException("The source-only installer was not started by the fixed Opticon source launcher.");
            if (bootstrap.LauncherSha256 is not null)
            {
                await using var launcherStream = new FileStream(
                    launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actual = await SHA256.HashDataAsync(launcherStream);
                if (!CryptographicOperations.FixedTimeEquals(
                        actual, Convert.FromHexString(bootstrap.LauncherSha256)))
                    throw new InvalidDataException("The downloaded Opticon launcher does not match its invitation filename.");
            }
        }
        if (validation.IsEnabled(ClientInstallValidationStep.PayloadAuthenticity))
            await ProductSigning.VerifyAuthenticodeAsync(launcherPath);

        // Starting this signed requireAdministrator launcher and accepting its
        // Windows UAC prompt authorizes the complete Opticon installation,
        // including conditional Tailscale replacement or reauthentication.
        // Resolve prerequisites before the long download/build so no separate
        // routine application-consent prompt is needed later.
        var dotnet = await RequireSdkAsync(invite.SdkVersion, directory, validation, report);

        var sourceArchive = Path.Combine(directory, invite.SourceFile);
        var selectedSourceArchive = ResolveSourceArchive(bootstrap, launcherPath, invite);
        if (selectedSourceArchive is null)
        {
            report("Downloading the hash-pinned Opticon source archive...");
            await DownloadPresignedSourceAsync(client, bootstrap.PublicId, invite, sourceArchive, validation);
        }
        else
        {
            report("Copying the hash-pinned source archive into a protected elevated stage...");
            if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths)
                && (File.GetAttributes(selectedSourceArchive) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The selected source archive must not be a reparse point.");
            if (validation.IsEnabled(ClientInstallValidationStep.DownloadIntegrity))
                await CopyAndVerifyAsync(selectedSourceArchive, sourceArchive, invite.SourceSize, invite.SourceSha256);
            else
                File.Copy(selectedSourceArchive, sourceArchive, overwrite: false);
        }

        report("Verifying the signed source allowlist and every source file...");
        var sourceDirectory = validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths)
            ? HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(directory, "source")
            : Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
        var sourceManifest = await ExtractVerifiedAsync(sourceArchive, sourceDirectory, invite, validation);
        if (validation.IsEnabled(ClientInstallValidationStep.LauncherBinding))
            await VerifyLauncherMatchesArchiveAsync(launcherPath, sourceManifest);
        // Keep the installed generation running until every replacement
        // executable has been built and attested.  The final elevated Setup
        // handoff performs the fixed-root replacement immediately before it
        // starts convergence; an SDK/restore/publish failure therefore cannot
        // strand a machine by deleting its existing remote-access lifelines.
        var targetRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Opticon source installation supports only x64 and ARM64 Windows.")
        };
        if (validation.IsEnabled(ClientInstallValidationStep.InvitationConstraints)
            && !invite.TargetRuntimes.Contains(targetRuntime, StringComparer.Ordinal))
            throw new PlatformNotSupportedException("The signed source release does not support this Windows architecture.");
        report($"Building Opticon {invite.ReleaseVersion} locally with an approved .NET 10 SDK...");
        var installer = Path.Combine(sourceDirectory, "Install-OpticonFromSource.ps1");
        var childEnvironment = BuildSanitizedEnvironment(directory, dotnet, validation);
        var result = await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", installer,
                "-SourceRoot", sourceDirectory, "-SourceArchive", sourceArchive,
                "-SourceVersion", invite.ReleaseVersion, "-SourceSha256", invite.SourceSha256,
                "-SourceManifestSha256", invite.SourceManifestSha256, "-SourceManifestKeyId", invite.SourceManifestKeyId,
                "-SigningProfile", invite.SigningProfile,
                "-SourceReleaseCertificateBase64", sourceManifest.SourceReleaseCertificateBase64,
                "-ProductSignerThumbprint", invite.ProductSignerThumbprint,
                "-ProductSigningCertificateBase64", sourceManifest.ProductSigningCertificateBase64,
                "-SdkVersion", invite.SdkVersion, "-RuntimeVersion", invite.RuntimeVersion,
                "-TargetRuntime", targetRuntime,
                 "-ClientInstallValidationBase64", Convert.ToBase64String(
                     JsonSerializer.SerializeToUtf8Bytes(validation, JsonDefaults.Options)),
                 "-Role", invite.Role.ToString(),
                 "-InvitePath", invitePath, "-DotnetPath", dotnet],
            TimeSpan.FromMinutes(45),
            environment: childEnvironment, clearEnvironment: true);
        if (!result.Succeeded)
            throw new InvalidOperationException("The authenticated local source build failed: " +
                                                (result.StandardError + Environment.NewLine + result.StandardOutput).Trim());

        // The bounded process above performs only restore, publish, hashing,
        // and attestation. Never let its build deadline own the destructive
        // Setup process: timing out that outer process after legacy removal
        // would kill Setup and strand the machine between generations.
        var releaseDirectory = Path.Combine(directory, "release");
        var setup = Path.Combine(releaseDirectory, "Taildesk.Setup.exe");
        var attestation = Path.Combine(releaseDirectory, "source-build-attestation.json");
        if (!File.Exists(setup) || !File.Exists(attestation))
            throw new FileNotFoundException(
                "The authenticated source build did not produce its Setup handoff and attestation.");
        if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
        {
            HostedBootstrapper.RequireNoReparseTraversal(directory, releaseDirectory);
            HostedBootstrapper.RequireNoReparseTraversal(releaseDirectory, setup);
            HostedBootstrapper.RequireNoReparseTraversal(releaseDirectory, attestation);
        }

        report("The local build is attested. Running read-only elevated replacement checks before cleanup...");
        return await RunAttestedSetupOutsideBuildDeadlineAsync(
            setup,
            attestation,
            invitePath,
            bootstrap.PrivateKey,
            childEnvironment,
            tailscaleAuthorizationApproved: true,
            report);
    }

    private static async Task<int> RunAttestedSetupOutsideBuildDeadlineAsync(
        string setup,
        string attestation,
        string invitePath,
        string inviteKey,
        IReadOnlyDictionary<string, string?> childEnvironment,
        bool tailscaleAuthorizationApproved,
        Action<string> report)
    {
        var setupEnvironment = new Dictionary<string, string?>(
            childEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            [HostedBootstrapper.InvitePathEnvironmentVariable] = invitePath,
            [HostedBootstrapper.InviteKeyEnvironmentVariable] = inviteKey,
            [HostedBootstrapper.TailscaleAuthorizationEnvironmentVariable] =
                tailscaleAuthorizationApproved ? "1" : null
        };
        report("The elevated Opticon Setup is continuing in a second visible window...");
        var result = await ProcessRunner.RunAsync(
            setup,
            [$"--source-attestation={attestation}", "--replace-existing"],
            timeout: null,
            cancellationToken: CancellationToken.None,
            showWindow: true,
            environment: setupEnvironment,
            clearEnvironment: true);
        if (!IsSuccessfulSetupHandoffExitCode(result.ExitCode))
        {
            var detail = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"The attested Opticon Setup returned {result.ExitCode}. Review its protected Setup log."
                    : $"The attested Opticon Setup returned {result.ExitCode}: {detail}");
        }
        if (result.ExitCode == 3010)
            report("Windows restart is required; protected Setup state will resume automatically after logon.");
        return result.ExitCode;
    }

    private static bool IsSuccessfulSetupHandoffExitCode(int exitCode) =>
        exitCode is 0 or 3010;

    private static void ValidateInvitation(InvitePayload invite)
    {
        if (invite.SchemaVersion != InvitationPolicy.HostedLinkSchemaVersion
            || !string.Equals(invite.InstallProtocol, InvitationPolicy.SourceInstallProtocol, StringComparison.Ordinal)
            || invite.ExpiresAt <= DateTimeOffset.UtcNow
            || !Regex.IsMatch(invite.ReleaseVersion, "^[1-9][0-9]*\\.[0-9]+\\.[0-9]+$")
            || invite.SourceFile != $"opticon-source-{invite.ReleaseVersion}.zip"
            || invite.SourceSize is < 1024 or > 256L * 1024 * 1024 || !HashPattern.IsMatch(invite.SourceSha256)
            || !HashPattern.IsMatch(invite.SourceManifestSha256)
            || invite.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || invite.SigningProfile != BuildSigningTrust.ProfileName
            || !BuildSigningTrust.IsPublishable
            || invite.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || invite.SdkVersion != DotNetSdkPolicy.SignedPolicy || invite.RuntimeVersion != "10.0.10"
            || !invite.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal))
            throw new InvalidDataException("The signed invitation has invalid or unsupported source-build pins.");
    }

    private static async Task VerifyLauncherMatchesArchiveAsync(string launcherPath, SourceReleaseManifest manifest)
    {
        launcherPath = Path.GetFullPath(launcherPath);
        if (!HostedBootstrapper.IsSourceLauncher(launcherPath)
            || !File.Exists(launcherPath) || (File.GetAttributes(launcherPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The source-only launcher path is invalid.");
        var declared = manifest.Files.Where(file =>
                string.Equals(Normalize(file.Path), "OpticonSourceLauncher.exe", StringComparison.Ordinal))
            .ToArray();
        if (declared.Length != 1 || declared[0].Size <= 0 || !HashPattern.IsMatch(declared[0].Sha256))
            throw new InvalidDataException("The signed source archive does not declare exactly one fixed source launcher.");
        var info = new FileInfo(launcherPath);
        if (info.Length != declared[0].Size)
            throw new InvalidDataException("The running source launcher does not match the signed source archive size.");
        await using var stream = new FileStream(launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        if (!FixedHash(actual, declared[0].Sha256))
            throw new InvalidDataException("The running source launcher does not match the signed source archive.");
    }

    private static string? ResolveSourceArchive(
        SourceBootstrapRequest bootstrap,
        string launcherPath,
        InvitePayload invite)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(bootstrap.SourceArchivePath))
        {
            candidates.Add(Path.GetFullPath(bootstrap.SourceArchivePath));
        }
        else
        {
            var launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherPath))
                                    ?? throw new InvalidDataException("The source launcher has no parent directory.");
            candidates.Add(Path.Combine(launcherDirectory, invite.SourceFile));
            var extractionParent = Directory.GetParent(launcherDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(extractionParent))
                candidates.Add(Path.Combine(extractionParent, invite.SourceFile));
        }
        var existing = candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path)
                           && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                           && string.Equals(Path.GetFileName(path), invite.SourceFile, StringComparison.Ordinal))
            .ToArray();
        if (existing.Length > 1)
            throw new InvalidDataException("More than one adjacent Opticon source archive matched the signed invitation.");
        return existing.Length == 1 ? existing[0] : null;
    }

    private static async Task DownloadPresignedSourceAsync(
        HttpClient client,
        string publicId,
        InvitePayload invite,
        string destination,
        ClientInstallValidationPolicy validation)
    {
        var authorizationUrl = $"{Origin}/opticon/i/{Uri.EscapeDataString(publicId)}/source";
        using var response = await client.GetAsync(authorizationUrl, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != System.Net.HttpStatusCode.TemporaryRedirect || response.Headers.Location is null)
            throw new InvalidDataException("The Opticon source download authorization did not return a private S3 link.");
        var location = response.Headers.Location;
        if (validation.IsEnabled(ClientInstallValidationStep.DownloadIntegrity)
            && (!location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || location.Port != 443
            || location.UserInfo.Length != 0 || location.Fragment.Length != 0
            || !string.Equals(location.Host, "opticon-053663732727.s3.us-east-1.amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(location.AbsolutePath,
                $"/opticon/releases/{Uri.EscapeDataString(invite.ReleaseVersion)}/{Uri.EscapeDataString(invite.SourceFile)}",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(location.Query)))
            throw new InvalidDataException("The Opticon source authorization returned an unexpected download location.");
        await HostedBootstrapper.DownloadAsync(client, location.AbsoluteUri, destination,
            validation.IsEnabled(ClientInstallValidationStep.DownloadIntegrity) ? invite.SourceSize : null,
            256L * 1024 * 1024,
            validation.IsEnabled(ClientInstallValidationStep.DownloadIntegrity) ? invite.SourceSha256 : null);
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, long size, string hash)
    {
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            if (input.Length != size) throw new InvalidDataException("The adjacent source archive size does not match the signed invitation.");
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer);
                if (read == 0) break;
                total = checked(total + read);
                if (total > size) throw new InvalidDataException("The adjacent source archive exceeds its signed size.");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
            await output.FlushAsync();
            if (total != size || !CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), Convert.FromHexString(hash)))
                throw new InvalidDataException("The adjacent source archive does not match the signed invitation.");
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }

    private static async Task<SourceReleaseManifest> ExtractVerifiedAsync(
        string archivePath,
        string destination,
        InvitePayload invite,
        ClientInstallValidationPolicy validation)
    {
        if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
            HostedBootstrapper.RequireNoReparseTraversal(Path.GetDirectoryName(destination)!, destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 3 or > 4096) throw new InvalidDataException("The source archive entry count is invalid.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = Normalize(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                throw new InvalidDataException("The source archive contains an undeclared directory entry.");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("The source archive contains a duplicate path.");
        }
        if (!entries.TryGetValue("source-manifest.json", out var manifestEntry)
            || !entries.TryGetValue("source-manifest.sig", out var signatureEntry)
            || manifestEntry.Length is <= 0 or > 1024 * 1024 || signatureEntry.Length is <= 0 or > 16 * 1024)
            throw new InvalidDataException("The source archive lacks its bounded signed inner manifest.");
        var manifestBytes = await ReadEntryAsync(manifestEntry, 1024 * 1024);
        if (validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity)
            && !FixedHash(Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), invite.SourceManifestSha256))
            throw new InvalidDataException("The source inner manifest hash does not match the signed invitation.");
        byte[] signature;
        try { signature = Convert.FromBase64String(Encoding.UTF8.GetString(await ReadEntryAsync(signatureEntry, 16 * 1024)).Trim()); }
        catch (FormatException exception) { throw new InvalidDataException("The source inner-manifest signature is malformed.", exception); }
        if (validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity)
            && !SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The source inner-manifest RSA-PSS signature is invalid.");
        var manifest = JsonSerializer.Deserialize<SourceReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The source inner manifest is empty.");
        if (validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity)
            && (manifest.SchemaVersion != 1 || manifest.Version != invite.ReleaseVersion
            || manifest.SigningProfile != invite.SigningProfile
            || manifest.SigningProfile != BuildSigningTrust.ProfileName
            || manifest.SourceReleaseKeyId != invite.SourceManifestKeyId
            || manifest.SourceReleaseKeyId != SourceReleaseSigning.KeyId
            || manifest.ProductSignerThumbprint != invite.ProductSignerThumbprint
            || manifest.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || !CertificateBytesMatch(manifest.SourceReleaseCertificateBase64, SourceReleaseSigning.PinnedCertificate)
            || !CertificateBytesMatch(manifest.ProductSigningCertificateBase64, ProductSigning.PinnedCertificate)
            || manifest.SdkVersion != invite.SdkVersion || manifest.RuntimeVersion != invite.RuntimeVersion
            || !manifest.TargetRuntimes.SequenceEqual(invite.TargetRuntimes, StringComparer.Ordinal)
            || manifest.Files.Count is < 1 or > 4094))
            throw new InvalidDataException("The source inner manifest metadata is invalid.");

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "source-manifest.json", "source-manifest.sig" };
        long expanded = 0;
        foreach (var file in manifest.Files)
        {
            var relative = Normalize(file.Path);
            if (!declared.Add(relative) || !entries.TryGetValue(relative, out var entry)
                || (validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity)
                    && (file.Size <= 0 || file.Size != entry.Length || !HashPattern.IsMatch(file.Sha256))))
                throw new InvalidDataException($"The source inner manifest has an invalid declaration for {relative}.");
            expanded = checked(expanded + file.Size);
            if (expanded > 512L * 1024 * 1024) throw new InvalidDataException("The source archive expands beyond its limit.");
            var output = SafeDestination(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
                HostedBootstrapper.RequireNoReparseTraversal(destination, Path.GetDirectoryName(output)!);
            await ExtractEntryAsync(entry, output,
                validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity) ? file.Size : entry.Length,
                validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity) ? file.Sha256 : null);
        }
        if (validation.IsEnabled(ClientInstallValidationStep.SourceArchiveAuthenticity)
            && (declared.Count != entries.Count || entries.Keys.Except(declared, StringComparer.OrdinalIgnoreCase).Any()))
            throw new InvalidDataException("The source archive contains undeclared extra files.");
        return manifest;
    }

    private static async Task<string> RequireSdkAsync(
        string sdkPolicy,
        string protectedRoot,
        ClientInstallValidationPolicy validation,
        Action<string> report)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var dotnet = Path.GetFullPath(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        if (!dotnet.StartsWith(Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed .NET SDK host escaped Program Files.");
        if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
            HostedBootstrapper.RequireNoReparseTraversal(programFiles, dotnet);
        var attemptedAutomaticRepair = false;
        while (true)
        {
            if (await CompatibleSdkIsReadyAsync(protectedRoot, dotnet, validation)) return dotnet;
            if (!attemptedAutomaticRepair)
            {
                attemptedAutomaticRepair = true;
                var artifact = DependencyArtifacts.DotNetSdk(RuntimeInformation.OSArchitecture);
                report($"Installing the pinned stable .NET SDK {artifact.Version} required for this Opticon source build…");
                await InstallPinnedSdkAsync(artifact, protectedRoot, validation);
                report("The .NET SDK installer completed; rechecking the isolated Opticon build environment…");
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (await CompatibleSdkIsReadyAsync(protectedRoot, dotnet, validation)) return dotnet;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
                continue;
            }
            const string sdkUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0";
            if (!SdkRequirementDialog.Show(
                    sdkPolicy,
                    sdkUrl,
                    cancellationToken => CompatibleSdkIsReadyAsync(
                        protectedRoot, dotnet, validation, cancellationToken)))
                throw new OperationCanceledException($"A stable .NET SDK matching {sdkPolicy} is required.");
        }
    }

    private static async Task InstallPinnedSdkAsync(
        DotNetSdkArtifact artifact,
        string protectedRoot,
        ClientInstallValidationPolicy validation)
    {
        if (validation.IsEnabled(ClientInstallValidationStep.DependencyIntegrity)
            && (!Regex.IsMatch(artifact.Version, "^10\\.[0-9]+\\.[0-9]+$")
            || !Regex.IsMatch(artifact.FileName, "^dotnet-sdk-10\\.[0-9]+\\.[0-9]+-win-(?:x64|arm64)\\.exe$")
            || !Regex.IsMatch(artifact.Sha512, "^[a-f0-9]{128}$")
            || !Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || !uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The pinned .NET SDK repair artifact is invalid.");
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var downloadUri))
            throw new InvalidDataException("The .NET SDK repair URL is invalid.");

        var destination = Path.Combine(protectedRoot, artifact.FileName);
        if (File.Exists(destination))
        {
            if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
                HostedBootstrapper.RequireNoReparseTraversal(protectedRoot, destination);
            try { File.Delete(destination); } catch (Exception exception)
            {
                throw new IOException("A previous protected .NET SDK repair download could not be replaced.", exception);
            }
        }
        try
        {
            using var client = DirectHttp.CreateClient(TimeSpan.FromMinutes(30));
            using var response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Count != 0
                || response.Content.Headers.ContentLength is > 1024L * 1024 * 1024)
                throw new InvalidDataException("The pinned .NET SDK download has invalid transport metadata.");
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0) break;
                total = checked(total + read);
                if (total > 1024L * 1024 * 1024)
                    throw new InvalidDataException("The pinned .NET SDK download exceeded its size limit.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read));
            }
            await target.FlushAsync();
            target.Flush(flushToDisk: true);
            if (validation.IsEnabled(ClientInstallValidationStep.DependencyIntegrity)
                && !CryptographicOperations.FixedTimeEquals(
                    hash.GetHashAndReset(), Convert.FromHexString(artifact.Sha512)))
                throw new InvalidDataException("The pinned .NET SDK installer digest did not match its release pin.");

            var install = await ProcessRunner.RunAsync(
                destination,
                ["/install", "/quiet", "/norestart"],
                TimeSpan.FromMinutes(20));
            if (!install.Succeeded && install.ExitCode != 3010)
                throw new InvalidOperationException(
                    "The pinned .NET SDK installer failed: " +
                    (install.StandardError + " " + install.StandardOutput).Trim());
        }
        finally
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
        }
    }

    private static async Task<bool> CompatibleSdkIsReadyAsync(
        string protectedRoot,
        string dotnet,
        ClientInstallValidationPolicy validation,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dotnet)) return false;
        var environment = BuildSanitizedEnvironment(protectedRoot, dotnet, validation);
        var sdks = await ProcessRunner.RunAsync(dotnet, ["--list-sdks"], TimeSpan.FromSeconds(30),
            cancellationToken, environment: environment, clearEnvironment: true);
        return sdks.Succeeded && DotNetSdkPolicy.InventoryContainsAcceptedSdk(sdks.StandardOutput);
    }

    private static bool CertificateBytesMatch(string base64, X509Certificate2 expected)
    {
        try
        {
            var raw = Convert.FromBase64String(base64);
            return raw.Length == expected.RawData.Length
                   && CryptographicOperations.FixedTimeEquals(raw, expected.RawData);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string?> BuildSanitizedEnvironment(
        string protectedRoot,
        string dotnet,
        ClientInstallValidationPolicy validation)
    {
        var system32 = Path.GetFullPath(Environment.SystemDirectory);
        var systemRoot = Path.GetFullPath(system32 + "\\..");
        var systemDrive = Path.GetPathRoot(systemRoot)?.TrimEnd(Path.DirectorySeparatorChar)
                          ?? throw new InvalidOperationException("Windows has no fixed system drive.");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dotnetRoot = Path.GetDirectoryName(dotnet)!;
        var temp = Path.Combine(protectedRoot, "temp");
        var cliHome = Path.Combine(protectedRoot, "dotnet-home");
        var appDataRoaming = Path.Combine(protectedRoot, "appdata-roaming");
        var appDataLocal = Path.Combine(protectedRoot, "appdata-local");
        var packages = Path.Combine(protectedRoot, "nuget-packages");
        var httpCache = Path.Combine(protectedRoot, "nuget-http-cache");
        var pluginsCache = Path.Combine(protectedRoot, "nuget-plugins-cache");
        var msbuildUserExtensions = Path.Combine(protectedRoot, "msbuild-user-extensions");
        foreach (var directory in new[]
                 {
                     temp, cliHome, appDataRoaming, appDataLocal, packages, httpCache,
                     pluginsCache, msbuildUserExtensions
                 })
        {
            if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
                HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(protectedRoot, Path.GetFileName(directory));
            else
                Directory.CreateDirectory(directory);
        }
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["SystemDrive"] = systemDrive,
            ["ProgramData"] = programData,
            ["ALLUSERSPROFILE"] = programData,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["COMSPEC"] = Path.Combine(system32, "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PATH"] = string.Join(Path.PathSeparator,
                system32,
                Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"),
                dotnetRoot),
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["DOTNET_ROOT"] = dotnetRoot,
            ["DOTNET_CLI_HOME"] = cliHome,
            ["USERPROFILE"] = cliHome,
            ["HOME"] = cliHome,
            ["HOMEDRIVE"] = systemDrive,
            ["HOMEPATH"] = cliHome[systemDrive.Length..],
            ["APPDATA"] = appDataRoaming,
            ["LOCALAPPDATA"] = appDataLocal,
            ["NUGET_PACKAGES"] = packages,
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache,
            ["NUGET_AUDIT"] = "false",
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["NUGET_CERT_REVOCATION_MODE"] = "online",
            ["MSBuildUserExtensionsPath"] = msbuildUserExtensions,
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "1",
            ["MSBuildEnableWorkloadResolver"] = "false",
            ["PSModulePath"] = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "Modules"),
            ["POWERSHELL_TELEMETRY_OPTOUT"] = "1"
        };
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("The source archive contains an unsafe path.");
        return normalized;
    }

    private static string SafeDestination(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source archive path escaped its protected stage.");
        return path;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length is < 0 || entry.Length > maximum)
            throw new InvalidDataException("A source metadata entry exceeds its limit.");
        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        var buffer = new byte[32 * 1024];
        long remaining = entry.Length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new InvalidDataException("A source metadata entry was truncated.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1)) != 0)
            throw new InvalidDataException("A source metadata entry exceeds its declared size.");
        return output.ToArray();
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string path,
        long size,
        string? expectedHash)
    {
        if (entry.Length != size) throw new InvalidDataException("A source file size declaration changed.");
        try
        {
            await using var input = entry.Open();
            await using var target = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long remaining = size;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) throw new InvalidDataException("A source file was truncated.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1)) != 0)
                throw new InvalidDataException("A source file exceeds its signed size.");
            await target.FlushAsync();
            target.Flush(flushToDisk: true);
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedHash is not null && !FixedHash(actual, expectedHash))
                throw new InvalidDataException("A source file SHA-256 check failed.");
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private static bool FixedHash(string left, string right)
    {
        if (!Regex.IsMatch(left, "^[a-fA-F0-9]{64}$") || !Regex.IsMatch(right, "^[a-fA-F0-9]{64}$")) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }
}
