using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taildesk.Shared;

public static partial class UpdatePackageVerifier
{
    public const string ManifestEntryName = "release-manifest.json";
    public const string SignatureEntryName = "release-manifest.sig";
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<OpticonReleaseManifest> VerifyAndExtractAgentAsync(
        string packagePath,
        string destination,
        OpticonUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The staged Opticon package is missing.", packagePath);
        if (new FileInfo(packagePath).Length != request.PackageSize)
            throw new InvalidDataException("The staged Opticon package size does not match the release manifest.");

        await using (var stream = File.OpenRead(packagePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged Opticon package SHA-256 does not match the release manifest.");
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = FindUniqueEntry(archive, ManifestEntryName);
        var signatureEntry = FindUniqueEntry(archive, SignatureEntryName);
        if (manifestEntry.Length is <= 0 or > 1024 * 1024 || signatureEntry.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The Opticon release metadata has an invalid size.");

        var manifestBytes = await ReadEntryAsync(manifestEntry, 1024 * 1024, cancellationToken);
        var signatureText = System.Text.Encoding.UTF8.GetString(
            await ReadEntryAsync(signatureEntry, 64 * 1024, cancellationToken)).Trim();
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException exception) { throw new InvalidDataException("The Opticon release signature is malformed.", exception); }
        if (!InvitationSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The Opticon release manifest signature is invalid.");

        var manifest = JsonSerializer.Deserialize<OpticonReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release manifest is empty.");
        ValidateManifest(manifest, request);

        var agentFiles = manifest.Files
            .Where(file => NormalizeEntry(file.Path).StartsWith("Payload/Agent/", StringComparison.Ordinal))
            .ToArray();
        if (agentFiles.Length == 0 || !agentFiles.Any(file => NormalizeEntry(file.Path) == "Payload/Agent/Taildesk.Agent.exe"))
            throw new InvalidDataException("The Opticon release has no signed Agent payload.");

        var declared = agentFiles.Select(file => NormalizeEntry(file.Path)).ToHashSet(StringComparer.Ordinal);
        var packaged = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) && NormalizeEntry(entry.FullName).StartsWith("Payload/Agent/", StringComparison.Ordinal))
            .Select(entry => NormalizeEntry(entry.FullName)).ToArray();
        if (packaged.Length != packaged.Distinct(StringComparer.Ordinal).Count() || !declared.SetEquals(packaged))
            throw new InvalidDataException("The Agent payload contains missing, duplicate, or undeclared files.");
        if (agentFiles.Sum(file => file.Size) > MaximumExpandedBytes)
            throw new InvalidDataException("The Agent payload exceeds the safe expanded-size limit.");

        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var file in agentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizeEntry(file.Path);
                ValidateReleaseFile(file, normalized);
                var entry = FindUniqueEntry(archive, normalized);
                if (entry.Length != file.Size) throw new InvalidDataException($"Release file size mismatch: {normalized}");
                var relative = normalized["Payload/Agent/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var output = Path.GetFullPath(Path.Combine(destination, relative));
                EnsureDescendant(destination, output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var source = entry.Open();
                await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                using var sha = SHA256.Create();
                var buffer = new byte[1024 * 1024];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > file.Size) throw new InvalidDataException($"Release file expanded beyond its declared size: {normalized}");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (written != file.Size || !Convert.ToHexString(sha.Hash!).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Release file hash mismatch: {normalized}");
            }

            var executable = Path.Combine(destination, "Taildesk.Agent.exe");
            await InvitationSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
            var reported = NormalizeVersion(FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty);
            if (!reported.Equals(NormalizeVersion(request.TargetVersion), StringComparison.Ordinal))
                throw new InvalidDataException($"The signed Agent binary reports version {reported}, not {request.TargetVersion}.");
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            throw;
        }
    }

    public static async Task<OpticonReleaseManifest> VerifyAndExtractGuardianAsync(
        string packagePath,
        string destination,
        OpticonUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The staged Opticon package is missing.", packagePath);
        if (new FileInfo(packagePath).Length != request.PackageSize)
            throw new InvalidDataException("The staged Opticon package size does not match the release manifest.");

        await using (var stream = File.OpenRead(packagePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged Opticon package SHA-256 does not match the release manifest.");
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = FindUniqueEntry(archive, ManifestEntryName);
        var signatureEntry = FindUniqueEntry(archive, SignatureEntryName);
        if (manifestEntry.Length is <= 0 or > 1024 * 1024 || signatureEntry.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The Opticon release metadata has an invalid size.");

        var manifestBytes = await ReadEntryAsync(manifestEntry, 1024 * 1024, cancellationToken);
        var signatureText = System.Text.Encoding.UTF8.GetString(
            await ReadEntryAsync(signatureEntry, 64 * 1024, cancellationToken)).Trim();
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException exception) { throw new InvalidDataException("The Opticon release signature is malformed.", exception); }
        if (!InvitationSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The Opticon release manifest signature is invalid.");

        var manifest = JsonSerializer.Deserialize<OpticonReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release manifest is empty.");
        ValidateManifest(manifest, request);

        const string prefix = "Payload/UpdateGuardian/";
        var guardianFiles = manifest.Files
            .Where(file => NormalizeEntry(file.Path).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        if (guardianFiles.Length != 1
            || NormalizeEntry(guardianFiles[0].Path) != prefix + "Taildesk.UpdateGuardian.exe")
            throw new InvalidDataException("The Opticon release must contain exactly one signed Guardian executable.");

        var declared = guardianFiles.Select(file => NormalizeEntry(file.Path)).ToHashSet(StringComparer.Ordinal);
        var packaged = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) && NormalizeEntry(entry.FullName).StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => NormalizeEntry(entry.FullName)).ToArray();
        if (packaged.Length != packaged.Distinct(StringComparer.Ordinal).Count() || !declared.SetEquals(packaged))
            throw new InvalidDataException("The Guardian payload contains missing, duplicate, or undeclared files.");

        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        try
        {
            var file = guardianFiles[0];
            var normalized = NormalizeEntry(file.Path);
            ValidateReleaseFile(file, normalized);
            var entry = FindUniqueEntry(archive, normalized);
            if (entry.Length != file.Size) throw new InvalidDataException($"Release file size mismatch: {normalized}");
            var output = Path.Combine(destination, "Taildesk.UpdateGuardian.exe");
            await using var source = entry.Open();
            await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            using var sha = SHA256.Create();
            var buffer = new byte[1024 * 1024];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > file.Size) throw new InvalidDataException("The Guardian expanded beyond its declared size.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            if (written != file.Size || !Convert.ToHexString(sha.Hash!).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Guardian release file hash does not match its signed declaration.");
            await target.FlushAsync(cancellationToken);
            await InvitationSigning.VerifyAuthenticodeAsync(output, cancellationToken);
            var reported = NormalizeVersion(FileVersionInfo.GetVersionInfo(output).ProductVersion ?? string.Empty);
            if (!reported.Equals(NormalizeVersion(request.TargetVersion), StringComparison.Ordinal))
                throw new InvalidDataException($"The signed Guardian binary reports version {reported}, not {request.TargetVersion}.");
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            throw;
        }
    }

    public static void ValidateRequest(OpticonUpdateRequest request)
    {
        if (request.ProtocolVersion != RemoteAdministrationProtocol.UpdateVersion || request.OperationId == Guid.Empty)
            throw new InvalidDataException("The update request uses an unsupported protocol.");
        _ = ParseVersion(request.TargetVersion);
        if (request.PackageSize is < 1024 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("The update package size is outside the supported range.");
        if (request.PackageSha256.Length != 64 || request.PackageSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The update package has no valid SHA-256 pin.");
        if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Opticon updates must use an HTTPS package URL without embedded credentials.");
        if (!request.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)
            && !request.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update request has an unsupported architecture.");
    }

    public static Version ParseVersion(string value)
    {
        var normalized = NormalizeVersion(value);
        return Version.TryParse(normalized, out var version) && version.Major >= 1
            ? version
            : throw new InvalidDataException($"'{value}' is not a supported Opticon version.");
    }

    public static string NormalizeVersion(string value)
    {
        var match = NumericVersion().Match(value.Trim());
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) return string.Empty;
        return version.Build < 0
            ? $"{version.Major}.{version.Minor}.0"
            : version.Revision > 0
                ? version.ToString(4)
                : version.ToString(3);
    }

    private static void ValidateManifest(OpticonReleaseManifest manifest, OpticonUpdateRequest request)
    {
        if (manifest.SchemaVersion != 1 || manifest.UpdateProtocolVersion != RemoteAdministrationProtocol.UpdateVersion)
            throw new InvalidDataException("The signed release requires an unsupported update protocol.");
        if (!NormalizeVersion(manifest.Version).Equals(NormalizeVersion(request.TargetVersion), StringComparison.Ordinal)
            || manifest.Role != request.Role
            || !manifest.Architecture.Equals(request.Architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The signed release identity does not match the requested version, role, and architecture.");
        if (manifest.Files.Count is < 2 or > 2000)
            throw new InvalidDataException("The signed release file list is invalid.");
        if (manifest.Files.Select(file => NormalizeEntry(file.Path)).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
            throw new InvalidDataException("The signed release contains duplicate paths.");
        _ = ParseVersion(manifest.MinimumGuardianVersion);
    }

    private static void ValidateReleaseFile(OpticonReleaseFile file, string normalized)
    {
        if (normalized.Length == 0 || !file.Path.Equals(normalized, StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("The signed release contains an unsafe path.");
        if (file.Size is < 0 or > MaximumExpandedBytes || file.Sha256.Length != 64 || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"The signed release metadata is invalid for {normalized}.");
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !file.SignerThumbprint.Equals(InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The executable signer pin is invalid for {normalized}.");
    }

    private static ZipArchiveEntry FindUniqueEntry(ZipArchive archive, string name)
    {
        var normalized = NormalizeEntry(name);
        var matches = archive.Entries.Where(entry => NormalizeEntry(entry.FullName) == normalized).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"The release must contain exactly one {name} entry.");
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, maximumBytes));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Release metadata exceeded its safe size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static string NormalizeEntry(string value) => value.Replace('\\', '/').TrimStart('/');

    private static void EnsureDescendant(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release attempted to escape its staging directory.");
    }

    [GeneratedRegex(@"^(\d+\.\d+\.\d+(?:\.\d+)?)(?:[-+].*)?$")]
    private static partial Regex NumericVersion();
}
