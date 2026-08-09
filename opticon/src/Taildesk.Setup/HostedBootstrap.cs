using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal sealed record HostedBootstrap(string PublicId, string PrivateKey, string BootstrapSha256);

internal static class HostedBootstrapper
{
    private const string Origin = "https://taildesk-egokick-control.fly.dev";
    internal const string InvitePathEnvironmentVariable = "OPTICON_HOSTED_INVITE_PATH";
    internal const string InviteKeyEnvironmentVariable = "OPTICON_HOSTED_INVITE_KEY";
    private static readonly Regex NamePattern = new(
        "^Install-Opticon-(?<id>[A-Za-z0-9_-]{32})--(?<key>[A-Za-z0-9_-]{43})--(?<sha>[a-f0-9]{64})$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PublishedNamePattern = new(
        "^opticon-bootstrap-[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool TryParse(string? executablePath, out HostedBootstrap bootstrap)
    {
        bootstrap = default!;
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty);
        if (!match.Success) return false;
        bootstrap = new HostedBootstrap(match.Groups["id"].Value, match.Groups["key"].Value, match.Groups["sha"].Value);
        return true;
    }

    internal static bool IsPublishedBootstrap(string? executablePath) =>
        string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
        && PublishedNamePattern.IsMatch(Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty);

    internal static async Task LaunchSetupAsync(HostedBootstrap bootstrap, Action<string> report)
    {
        var bootstrapPath = Environment.ProcessPath
                            ?? throw new InvalidOperationException("The signed Opticon bootstrap executable path is unavailable.");
        await VerifyFileHashAsync(bootstrapPath, bootstrap.BootstrapSha256);
        await ProductSigning.VerifyAuthenticodeAsync(bootstrapPath);
        await SourceBootstrapInstaller.RunAsync(bootstrap, bootstrapPath, report);
        return;
    }

    private static async Task VerifyFileHashAsync(string path, string expectedHash)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("The running bootstrap does not match the SHA-256 embedded in its invitation filename.");
    }

    internal static void RequireProtectedHandoff(string invitePath, string releaseDirectory)
    {
        var root = Path.GetFullPath(AppPaths.BootstrapHandoffDirectory);
        var programData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        var invite = Path.GetFullPath(invitePath);
        var release = Path.TrimEndingDirectorySeparator(Path.GetFullPath(releaseDirectory));
        var handoff = Path.GetDirectoryName(invite)
                      ?? throw new InvalidDataException("The hosted invitation has no handoff directory.");
        var releaseParent = Path.GetDirectoryName(release)
                            ?? throw new InvalidDataException("The hosted release has no handoff directory.");
        if (!string.Equals(Path.GetDirectoryName(root), programData, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(handoff), root, StringComparison.OrdinalIgnoreCase)
            || !IsDescendant(root, invite) || !IsDescendant(root, release)
            || !handoff.Equals(releaseParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Setup was not launched from its protected hosted-invitation handoff.");
        RequireNoReparseTraversal(programData, root);
        RequireNoReparseTraversal(root, handoff);
        RequireNoReparseTraversal(handoff, invite);
        RequireNoReparseTraversal(handoff, release);
        RequireRestrictedAcl(root);
        RequireRestrictedAcl(handoff);
    }

    internal static string CreateProtectedHandoffDirectory()
    {
        var root = CreateOrRequireProtectedHandoffRoot();
        var restricted = CreateRestrictedDirectorySecurity();
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        new DirectoryInfo(directory).Create(restricted);
        RequireNoReparseTraversal(root, directory);
        RequireRestrictedAcl(directory);
        return directory;
    }

    internal static string CreateOrRequireProtectedHandoffRoot()
    {
        var root = Path.GetFullPath(AppPaths.BootstrapHandoffDirectory);
        var programData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        if (!string.Equals(Path.GetDirectoryName(root), programData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bootstrap handoff root is not a direct child of ProgramData.");
        var restricted = CreateRestrictedDirectorySecurity();
        if (!Directory.Exists(root))
        {
            try { new DirectoryInfo(root).Create(restricted); }
            catch (IOException) when (Directory.Exists(root)) { }
        }
        RequireNoReparseTraversal(programData, root);
        RequireRestrictedAcl(root);
        return root;
    }

    internal static string CreateOrRequireRestrictedChildDirectory(string parent, string name)
    {
        parent = Path.GetFullPath(parent);
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new ArgumentException("The protected child directory name is invalid.", nameof(name));
        RequireRestrictedAcl(parent);
        var directory = Path.Combine(parent, name);
        if (!Directory.Exists(directory))
        {
            try { new DirectoryInfo(directory).Create(CreateRestrictedDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        RequireNoReparseTraversal(parent, directory);
        RequireRestrictedAcl(directory);
        return directory;
    }

    private static DirectorySecurity CreateRestrictedDirectorySecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void RequireRestrictedAcl(string directory)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectoryInfo(directory).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The hosted-invitation handoff inherits unsafe permissions.");
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || (!owner.Equals(administrators) && !owner.Equals(system)))
            throw new UnauthorizedAccessException("The hosted-invitation handoff has an untrusted owner.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow
                                                   || (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl
                                                   || (!rule.IdentityReference.Equals(administrators)
                                                       && !rule.IdentityReference.Equals(system)))
            || !rules.Any(rule => rule.IdentityReference.Equals(administrators))
            || !rules.Any(rule => rule.IdentityReference.Equals(system)))
            throw new UnauthorizedAccessException("The hosted-invitation handoff does not have the exact restricted ACL.");
    }

    internal static void RequireNoReparseTraversal(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !IsDescendant(root, path))
            throw new InvalidDataException("A bootstrap handoff path escaped its protected root.");
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The bootstrap handoff root is a link or junction.");
        foreach (var segment in Path.GetRelativePath(root, path)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The bootstrap handoff contains a link or junction.");
        }
    }

    private static bool IsDescendant(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveBundleUrl(ArtifactRecordDto bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.DownloadUrl))
            return $"{Origin}/opticon/artifacts/v1/{Uri.EscapeDataString(bundle.File)}";
        if (!Uri.TryCreate(bundle.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.IsDefaultPort is false
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host[..^".cloudfront.net".Length].Length == 0
            || uri.AbsolutePath != $"/opticon/releases/{Uri.EscapeDataString(bundle.Version)}/{Uri.EscapeDataString(bundle.File)}")
            throw new InvalidDataException("The Opticon release manifest contains an unsafe CloudFront download URL.");
        return uri.AbsoluteUri;
    }

    internal static async Task DownloadAsync(HttpClient client, string url, string destination, long? expectedSize,
        long maximumSize, string? expectedHash)
    {
        if (maximumSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSize));
        if (expectedSize.HasValue && (expectedSize.Value <= 0 || expectedSize.Value > maximumSize))
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("Encoded Opticon downloads are not accepted.");
            if (response.Content.Headers.ContentLength is long declared
                && (declared > maximumSize || (expectedSize.HasValue && declared != expectedSize.Value)))
                throw new InvalidDataException("The Opticon download size did not match its signed release record.");
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumSize) throw new InvalidDataException("The Opticon download exceeded its size limit.");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
            await output.FlushAsync();
            if (expectedSize.HasValue && total != expectedSize.Value)
                throw new InvalidDataException("The Opticon download is incomplete.");
            if (expectedHash is not null
                && !CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("The Opticon download did not match its signed release record.");
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
    }
}
