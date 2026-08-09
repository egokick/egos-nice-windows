using System.Diagnostics;
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

    internal static async Task RunAsync(HostedBootstrap bootstrap, string bootstrapPath, Action<string> report)
    {
        var directory = HostedBootstrapper.CreateProtectedHandoffDirectory();
        var invitePath = Path.Combine(directory, "invite.tdinvite");
        using var client = DirectHttp.CreateClient(TimeSpan.FromMinutes(10));
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
        if (!FixedHash(bootstrap.BootstrapSha256, invite.BootstrapSha256))
            throw new InvalidDataException("The bootstrap filename does not match the signed invitation bootstrap pin.");
        await VerifyBootstrapIdentityAsync(bootstrapPath, invite);

        report("Finding this invitation's exact authenticated source release...");
        using var manifestResponse = await client.GetAsync($"{Origin}/opticon/artifacts/v1/manifest.json",
            HttpCompletionOption.ResponseHeadersRead);
        manifestResponse.EnsureSuccessStatusCode();
        var manifestBytes = await ReadBoundedResponseAsync(manifestResponse, 1024 * 1024);
        var outer = JsonSerializer.Deserialize<ArtifactManifestDto>(manifestBytes, JsonDefaults.Options)
                    ?? throw new InvalidDataException("The Opticon release manifest is empty.");
        if (outer.SchemaVersion != 1) throw new InvalidDataException("The Opticon release manifest schema is unsupported.");
        var sources = outer.Artifacts.Where(item => Matches(item, invite)).ToArray();
        if (sources.Length != 1) throw new InvalidDataException("The invitation's exact immutable source release is not published.");
        var source = sources[0];
        var bootstraps = outer.Artifacts.Where(item => MatchesBootstrap(item, invite)).ToArray();
        if (bootstraps.Length != 1) throw new InvalidDataException("The invitation's exact immutable bootstrap release is not published.");

        report("Copying or downloading source into a protected elevated stage...");
        var sourceArchive = Path.Combine(directory, invite.SourceFile);
        var adjacent = Path.Combine(Path.GetDirectoryName(bootstrapPath)!, invite.SourceFile);
        if (File.Exists(adjacent)) await CopyAndVerifyAsync(adjacent, sourceArchive, invite.SourceSize, invite.SourceSha256);
        else await HostedBootstrapper.DownloadAsync(client, HostedBootstrapper.ResolveBundleUrl(source),
            sourceArchive, invite.SourceSize, invite.SourceSize, invite.SourceSha256);

        report("Verifying the signed source allowlist and every source file...");
        var sourceDirectory = HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(directory, "source");
        var sourceManifest = await ExtractVerifiedAsync(sourceArchive, sourceDirectory, invite);
        var targetRuntime = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Opticon source installation supports only x64 and ARM64 Windows.")
        };
        if (!invite.TargetRuntimes.Contains(targetRuntime, StringComparer.Ordinal))
            throw new PlatformNotSupportedException("The signed source release does not support this Windows architecture.");
        var dotnet = await RequireSdkAsync(invite.SdkVersion, directory, targetRuntime);

        report($"Building Opticon {invite.ReleaseVersion} locally with .NET SDK {invite.SdkVersion}...");
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
                "-BootstrapVersion", invite.BootstrapVersion, "-BootstrapFile", invite.BootstrapFile,
                "-BootstrapSize", invite.BootstrapSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-BootstrapSha256", invite.BootstrapSha256,
                "-BootstrapSignerThumbprint", invite.BootstrapSignerThumbprint,
                "-Role", invite.Role.ToString(),
                "-InvitePath", invitePath, "-InviteKey", bootstrap.PrivateKey, "-DotnetPath", dotnet],
            TimeSpan.FromMinutes(45), environment: BuildSanitizedEnvironment(directory, dotnet), clearEnvironment: true);
        if (!result.Succeeded)
            throw new InvalidOperationException("The authenticated local source build failed: " +
                                                (result.StandardError + Environment.NewLine + result.StandardOutput).Trim());
    }

    private static void ValidateInvitation(InvitePayload invite)
    {
        if (invite.SchemaVersion != InvitationPolicy.HostedLinkSchemaVersion || invite.ExpiresAt <= DateTimeOffset.UtcNow
            || !Regex.IsMatch(invite.ReleaseVersion, "^[1-9][0-9]*\\.[0-9]+\\.[0-9]+$")
            || invite.SourceFile != $"opticon-source-{invite.ReleaseVersion}.zip"
            || invite.SourceSize is < 1024 or > 256L * 1024 * 1024 || !HashPattern.IsMatch(invite.SourceSha256)
            || !HashPattern.IsMatch(invite.SourceManifestSha256)
            || invite.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || invite.SigningProfile != OpticonSigningProfile.Production.ToString()
            || invite.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || invite.SdkVersion != "10.0.302" || invite.RuntimeVersion != "10.0.10"
            || !invite.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal)
            || invite.BootstrapVersion != invite.ReleaseVersion
            || invite.BootstrapFile != $"opticon-bootstrap-{invite.ReleaseVersion}.exe"
            || invite.BootstrapSize is < 1024 or > 128L * 1024 * 1024
            || !HashPattern.IsMatch(invite.BootstrapSha256)
            || !invite.BootstrapSignerThumbprint.Equals(
                ProductSigning.CertificateThumbprint, StringComparison.Ordinal))
            throw new InvalidDataException("The signed invitation has invalid or unsupported source-build pins.");
    }

    private static bool Matches(ArtifactRecordDto item, InvitePayload invite) =>
        item.Product == "OpticonSource" && item.Architecture == "source" && item.Version == invite.ReleaseVersion
        && item.File == invite.SourceFile && item.Size == invite.SourceSize && item.SdkVersion == invite.SdkVersion
        && item.RuntimeVersion == invite.RuntimeVersion && item.TargetRuntimes.SequenceEqual(invite.TargetRuntimes, StringComparer.Ordinal) && item.SourceManifestKeyId == invite.SourceManifestKeyId
        && item.SigningProfile == invite.SigningProfile
        && item.ProductSignerThumbprint == invite.ProductSignerThumbprint
        && FixedHash(item.Sha256, invite.SourceSha256)
        && FixedHash(item.SourceManifestSha256, invite.SourceManifestSha256)
        && IsSafeDownload(item);

    private static bool MatchesBootstrap(ArtifactRecordDto item, InvitePayload invite) =>
        item.Product == "OpticonBootstrap" && item.Architecture == "x64"
        && item.Version == invite.BootstrapVersion && item.File == invite.BootstrapFile
        && item.Size == invite.BootstrapSize && FixedHash(item.Sha256, invite.BootstrapSha256)
        && item.SignerThumbprint.Equals(invite.BootstrapSignerThumbprint, StringComparison.OrdinalIgnoreCase)
        && item.SigningProfile == invite.SigningProfile
        && item.SourceManifestKeyId == invite.SourceManifestKeyId
        && item.ProductSignerThumbprint == invite.ProductSignerThumbprint
        && IsSafeDownload(item);

    private static async Task VerifyBootstrapIdentityAsync(string path, InvitePayload invite)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != invite.BootstrapSize)
            throw new InvalidDataException("The running bootstrap size does not match the signed invitation.");
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        if (!FixedHash(actualHash, invite.BootstrapSha256))
            throw new InvalidDataException("The running bootstrap hash does not match the signed invitation.");
        var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (!string.Equals(productVersion, invite.BootstrapVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The running bootstrap version does not match the signed invitation.");
        await ProductSigning.VerifyAuthenticodeAsync(path);
    }

    private static bool IsSafeDownload(ArtifactRecordDto item)
    {
        try { return HostedBootstrapper.ResolveBundleUrl(item).Length > 0; }
        catch (InvalidDataException) { return false; }
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, long size, string hash)
    {
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
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
            || manifest.SigningProfile != OpticonSigningProfile.Production.ToString()
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

    private static async Task<string> RequireSdkAsync(string version, string protectedRoot, string targetRuntime)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var dotnet = Path.GetFullPath(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        if (!dotnet.StartsWith(Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixed .NET SDK host escaped Program Files.");
        HostedBootstrapper.RequireNoReparseTraversal(programFiles, dotnet);
        while (true)
        {
            if (File.Exists(dotnet))
            {
                var environment = BuildSanitizedEnvironment(protectedRoot, dotnet);
                var result = await ProcessRunner.RunAsync(dotnet, ["--list-sdks"], TimeSpan.FromSeconds(30),
                    environment: environment, clearEnvironment: true);
                var runtimes = await ProcessRunner.RunAsync(dotnet, ["--list-runtimes"], TimeSpan.FromSeconds(30),
                    environment: environment, clearEnvironment: true);
                var host = await ProcessRunner.RunAsync(dotnet, ["--info"], TimeSpan.FromSeconds(30),
                    environment: environment, clearEnvironment: true);
                if (result.Succeeded && runtimes.Succeeded && host.Succeeded
                    && result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => line.StartsWith(version + " ", StringComparison.Ordinal))
                    && RequiredRuntimesPresent(runtimes.StandardOutput, "10.0.10")
                    && SdkHostMatchesTarget(host.StandardOutput, targetRuntime)) return dotnet;
            }
            var architecture = targetRuntime == "win-arm64" ? "arm64" : "x64";
            var sdkUrl = $"https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-{version}-windows-{architecture}-installer";
            if (!SdkRequirementDialog.Show(version, architecture, sdkUrl))
                throw new OperationCanceledException($"Exact .NET SDK {version} is required.");
        }
    }

    private static bool RequiredRuntimesPresent(string output, string version)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App" }
            .All(runtime => lines.Any(line => line.StartsWith(runtime + " " + version + " ", StringComparison.Ordinal)));
    }

    private static bool SdkHostMatchesTarget(string output, string targetRuntime)
    {
        var expectedArchitecture = targetRuntime == "win-arm64" ? "arm64" : "x64";
        var architecture = Regex.Matches(
                output, "^\\s*Architecture:\\s*(x64|arm64)\\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var rids = Regex.Matches(
                output, "^\\s*RID:\\s*(win-(?:x64|arm64))\\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return architecture.Length == 1
               && architecture[0] == expectedArchitecture
               && rids.Length == 1
               && rids[0] == targetRuntime;
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

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpResponseMessage response, int maximum)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Compressed release metadata is not accepted.");
        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > maximum))
            throw new InvalidDataException("Release metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new InvalidDataException("Release metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static IReadOnlyDictionary<string, string?> BuildSanitizedEnvironment(string protectedRoot, string dotnet)
    {
        var systemRoot = Path.GetFullPath(Environment.SystemDirectory + "\\..");
        var temp = Path.Combine(protectedRoot, "temp");
        var cliHome = Path.Combine(protectedRoot, "dotnet-home");
        var packages = Path.Combine(protectedRoot, "nuget-packages");
        var httpCache = Path.Combine(protectedRoot, "nuget-http-cache");
        var msbuildUserExtensions = Path.Combine(protectedRoot, "msbuild-user-extensions");
        foreach (var directory in new[] { temp, cliHome, packages, httpCache, msbuildUserExtensions })
            HostedBootstrapper.CreateOrRequireRestrictedChildDirectory(protectedRoot, Path.GetFileName(directory));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["COMSPEC"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PATH"] = string.Join(Path.PathSeparator,
                Environment.SystemDirectory,
                Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"),
                Path.GetDirectoryName(dotnet)!),
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["DOTNET_CLI_HOME"] = cliHome,
            ["NUGET_PACKAGES"] = packages,
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["MSBuildUserExtensionsPath"] = msbuildUserExtensions,
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
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
