using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal sealed record SourceBootstrapRequest(
    string PublicId,
    string PrivateKey,
    string? SourceArchivePath,
    string? LauncherSha256);

internal static class HostedBootstrapper
{
    private const string Origin = "https://taildesk-egokick-control.fly.dev";
    internal const string InvitePathEnvironmentVariable = "OPTICON_HOSTED_INVITE_PATH";
    internal const string InviteKeyEnvironmentVariable = "OPTICON_HOSTED_INVITE_KEY";
    internal const string TailscaleAuthorizationEnvironmentVariable =
        "OPTICON_TAILSCALE_AUTHORIZATION_APPROVED";
    private static readonly Regex PublicIdPattern = new("^[A-Za-z0-9_-]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKeyPattern = new("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);
    private static readonly Regex BoundSourceLauncherPattern = new(
        "^Install-Opticon-(?<public>[A-Za-z0-9_-]{32})--(?<key>[A-Za-z0-9_-]{43})--(?<hash>[a-f0-9]{64})(?: \\([1-9][0-9]{0,2}\\))?\\.exe$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The source archive contains this publisher-signed launcher. The invite
    /// page may also serve the exact same bytes under an invitation-bound local
    /// filename. The fragment key is placed into that filename by browser-side
    /// JavaScript and is never sent to the gateway.
    /// </summary>
    internal static bool IsSourceLauncher(string? executablePath)
    {
        var name = Path.GetFileName(executablePath) ?? string.Empty;
        return string.Equals(name, "OpticonSourceLauncher.exe", StringComparison.OrdinalIgnoreCase)
               || BoundSourceLauncherPattern.IsMatch(name);
    }

    internal static SourceBootstrapRequest ParseSourceLaunch(
        IReadOnlyList<string> arguments,
        string? executablePath)
    {
        if (!IsSourceLauncher(executablePath))
            throw new InvalidDataException("The source-only installer must be launched from OpticonSourceLauncher.exe.");
        if (arguments.Any(argument => !argument.StartsWith("--invite-url=", StringComparison.OrdinalIgnoreCase)
                                      && !argument.StartsWith("--source-archive=", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The source-only launcher accepts only --invite-url and --source-archive.");
        var bound = BoundSourceLauncherPattern.Match(Path.GetFileName(executablePath) ?? string.Empty);
        var invitationUrl = ReadOptionalArgument(arguments, "--invite-url=")
                            ?? (bound.Success
                                ? $"{Origin}/opticon/i/{bound.Groups["public"].Value}#{bound.Groups["key"].Value}"
                                : SourceLauncherPrompt.ReadInvitationUrl());
        var sourceArchive = ReadOptionalArgument(arguments, "--source-archive=");
        if (!Uri.TryCreate(invitationUrl, UriKind.Absolute, out var invitation)
            || invitation.Scheme != Uri.UriSchemeHttps || invitation.Port != 443
            || invitation.UserInfo.Length != 0 || invitation.Query.Length != 0
            || !string.Equals(invitation.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase)
            || !invitation.AbsolutePath.StartsWith("/opticon/i/", StringComparison.Ordinal)
            || invitation.AbsolutePath["/opticon/i/".Length..].Contains('/')
            || invitation.Fragment.Length <= 1)
            throw new InvalidDataException("The source-only launcher was not given a canonical Opticon invitation URL.");
        var publicId = invitation.AbsolutePath["/opticon/i/".Length..];
        var privateKey = invitation.Fragment[1..];
        if (!PublicIdPattern.IsMatch(publicId) || !PrivateKeyPattern.IsMatch(privateKey))
            throw new InvalidDataException("The source-only launcher invitation URL is malformed.");
        if (sourceArchive is not null)
        {
            sourceArchive = Path.GetFullPath(sourceArchive);
            if (!string.Equals(Path.GetExtension(sourceArchive), ".zip", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(sourceArchive))
                throw new InvalidDataException("The selected source archive must be a regular local .zip file.");
        }
        return new SourceBootstrapRequest(publicId, privateKey, sourceArchive,
            bound.Success ? bound.Groups["hash"].Value.ToLowerInvariant() : null);
    }

    internal static async Task<int> LaunchSourceOnlyAsync(SourceBootstrapRequest bootstrap, Action<string> report)
    {
        var launcherPath = Environment.ProcessPath
                           ?? throw new InvalidOperationException("The Opticon source launcher path is unavailable.");
        return await SourceBootstrapInstaller.RunAsync(bootstrap, launcherPath, report);
    }

    private static string? ReadOptionalArgument(IReadOnlyList<string> arguments, string prefix)
    {
        var matches = arguments.Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || (matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0][prefix.Length..])))
            throw new InvalidDataException($"The source-only launcher accepts at most one {prefix[..^1]} argument.");
        return matches.Length == 0 ? null : matches[0][prefix.Length..].Trim('"');
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

    internal static async Task DownloadAsync(HttpClient client, string url, string destination, long? expectedSize,
        long maximumSize, string? expectedHash, bool validateTransport = true)
    {
        if (maximumSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSize));
        if (expectedSize.HasValue && (expectedSize.Value <= 0 || expectedSize.Value > maximumSize))
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (validateTransport && response.Content.Headers.ContentEncoding.Count != 0)
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
