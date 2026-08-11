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

    internal static async Task RunAsync(SourceBootstrapRequest bootstrap, string launcherPath, Action<string> report)
    {
        var directory = HostedBootstrapper.CreateProtectedHandoffDirectory();
        var invitePath = Path.Combine(directory, "invite.tdinvite");
        using var client = DirectHttp.CreateClient(TimeSpan.FromMinutes(30));
        report("Downloading the encrypted Opticon invitation...");
        await HostedBootstrapper.DownloadAsync(client,
            $"{Origin}/opticon/i/{Uri.EscapeDataString(bootstrap.PublicId)}/invite.tdinvite", invitePath,
            expectedSize: null, maximumSize: 64 * 1024, expectedHash: null);
        var encryptedInvite = await File.ReadAllBytesAsync(invitePath);
        var signedEnvelope = HostedInviteFile.Decrypt(bootstrap.PrivateKey, encryptedInvite);
        InvitePayload invite;
        try { invite = HostedInviteFile.ReadSigned(signedEnvelope); }
        finally { CryptographicOperations.ZeroMemory(signedEnvelope); }
        ValidateInvitation(invite);

        var sourceArchive = Path.Combine(directory, invite.SourceFile);
        var selectedSourceArchive = ResolveSourceArchive(bootstrap, launcherPath, invite);
        if (selectedSourceArchive is null)
        {
            report("Downloading the hash-pinned Opticon source archive...");
            await DownloadPresignedSourceAsync(client, bootstrap.PublicId, invite, sourceArchive);
        }
        else
        {
            report("Copying the hash-pinned source archive into a protected elevated stage...");
            await CopyAndVerifyAsync(selectedSourceArchive, sourceArchive, invite.SourceSize, invite.SourceSha256);
        }

        report("Verifying the signed source allowlist and every source file...");
        var sourceDirectory = HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(directory, "source");
        var sourceManifest = await ExtractVerifiedAsync(sourceArchive, sourceDirectory, invite);
        await VerifyLauncherMatchesArchiveAsync(launcherPath, sourceManifest);
        // The signed launcher/archive pair may deliberately replace only the
        // pre-protected 1.1.38 installation.  The remover presents a typed
        // destructive confirmation and proves every target before Setup later
        // enforces the strict machine-state ACL contract.
        await LegacyOpticonRemoval.RemoveLegacyInstallationIfPresentAsync(report);
        var targetRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Opticon source installation supports only x64 and ARM64 Windows.")
        };
        if (!invite.TargetRuntimes.Contains(targetRuntime, StringComparer.Ordinal))
            throw new PlatformNotSupportedException("The signed source release does not support this Windows architecture.");
        var dotnet = await RequireSdkAsync(invite.SdkVersion, directory);

        report($"Building Opticon {invite.ReleaseVersion} locally with an approved .NET 10 SDK...");
        var installer = Path.Combine(sourceDirectory, "Install-OpticonFromSource.ps1");
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
                "-Role", invite.Role.ToString(),
                "-InvitePath", invitePath, "-InviteKey", bootstrap.PrivateKey, "-DotnetPath", dotnet],
            TimeSpan.FromMinutes(45), environment: BuildSanitizedEnvironment(directory, dotnet), clearEnvironment: true);
        if (!result.Succeeded)
            throw new InvalidOperationException("The authenticated local source build failed: " +
                                                (result.StandardError + Environment.NewLine + result.StandardOutput).Trim());
    }

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
        string destination)
    {
        var authorizationUrl = $"{Origin}/opticon/i/{Uri.EscapeDataString(publicId)}/source";
        using var response = await client.GetAsync(authorizationUrl, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != System.Net.HttpStatusCode.TemporaryRedirect || response.Headers.Location is null)
            throw new InvalidDataException("The Opticon source download authorization did not return a private S3 link.");
        var location = response.Headers.Location;
        if (!location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || location.Port != 443
            || location.UserInfo.Length != 0 || location.Fragment.Length != 0
            || !string.Equals(location.Host, "opticon-053663732727.s3.us-east-1.amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(location.AbsolutePath,
                $"/opticon/releases/{Uri.EscapeDataString(invite.ReleaseVersion)}/{Uri.EscapeDataString(invite.SourceFile)}",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(location.Query))
            throw new InvalidDataException("The Opticon source authorization returned an unexpected download location.");
        await HostedBootstrapper.DownloadAsync(client, location.AbsoluteUri, destination,
            invite.SourceSize, 256L * 1024 * 1024, invite.SourceSha256);
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

    private static async Task<SourceReleaseManifest> ExtractVerifiedAsync(string archivePath, string destination, InvitePayload invite)
    {
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
        if (!FixedHash(Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), invite.SourceManifestSha256))
            throw new InvalidDataException("The source inner manifest hash does not match the signed invitation.");
        byte[] signature;
        try { signature = Convert.FromBase64String(Encoding.UTF8.GetString(await ReadEntryAsync(signatureEntry, 16 * 1024)).Trim()); }
        catch (FormatException exception) { throw new InvalidDataException("The source inner-manifest signature is malformed.", exception); }
        if (!SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The source inner-manifest RSA-PSS signature is invalid.");
        var manifest = JsonSerializer.Deserialize<SourceReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The source inner manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Version != invite.ReleaseVersion
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
            || manifest.Files.Count is < 1 or > 4094)
            throw new InvalidDataException("The source inner manifest metadata is invalid.");

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "source-manifest.json", "source-manifest.sig" };
        long expanded = 0;
        foreach (var file in manifest.Files)
        {
            var relative = Normalize(file.Path);
            if (!declared.Add(relative) || !entries.TryGetValue(relative, out var entry)
                || file.Size <= 0 || file.Size != entry.Length || !HashPattern.IsMatch(file.Sha256))
                throw new InvalidDataException($"The source inner manifest has an invalid declaration for {relative}.");
            expanded = checked(expanded + file.Size);
            if (expanded > 512L * 1024 * 1024) throw new InvalidDataException("The source archive expands beyond its limit.");
            var output = SafeDestination(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            HostedBootstrapper.RequireNoReparseTraversal(destination, Path.GetDirectoryName(output)!);
            await ExtractEntryAsync(entry, output, file.Size, file.Sha256);
        }
        if (declared.Count != entries.Count || entries.Keys.Except(declared, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The source archive contains undeclared extra files.");
        return manifest;
    }

    private static async Task<string> RequireSdkAsync(string sdkPolicy, string protectedRoot)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var dotnet = Path.GetFullPath(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        if (!dotnet.StartsWith(Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed .NET SDK host escaped Program Files.");
        HostedBootstrapper.RequireNoReparseTraversal(programFiles, dotnet);
        while (true)
        {
            if (await CompatibleSdkIsReadyAsync(protectedRoot, dotnet)) return dotnet;
            const string sdkUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0";
            if (!SdkRequirementDialog.Show(
                    sdkPolicy,
                    sdkUrl,
                    cancellationToken => CompatibleSdkIsReadyAsync(
                        protectedRoot, dotnet, cancellationToken)))
                throw new OperationCanceledException($"A stable .NET SDK matching {sdkPolicy} is required.");
        }
    }

    private static async Task<bool> CompatibleSdkIsReadyAsync(
        string protectedRoot,
        string dotnet,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dotnet)) return false;
        var environment = BuildSanitizedEnvironment(protectedRoot, dotnet);
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

    private static IReadOnlyDictionary<string, string?> BuildSanitizedEnvironment(string protectedRoot, string dotnet)
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
            HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(protectedRoot, Path.GetFileName(directory));
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
        string expectedHash)
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
            if (!FixedHash(actual, expectedHash))
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
