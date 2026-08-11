using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Taildesk.Shared;

namespace Taildesk.Admin;

/// <summary>
/// Authenticated, gateway-authoritative state for deploying the source archive
/// used by new invitations. It intentionally contains no invitation ciphertext
/// or enrollment credentials.
/// </summary>
public sealed class ReleaseDeploymentPreflight
{
    public int SchemaVersion { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public string DeployedVersion { get; set; } = string.Empty;
    public bool AlreadyDeployed { get; set; }
    public bool TargetIsOlder { get; set; }
    public bool DeploymentBlocked { get; set; }
    public string DeploymentBlockedReason { get; set; } = string.Empty;
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public string LeaseTokenSha256 { get; set; } = string.Empty;
    public string DeploymentRevision { get; set; } = string.Empty;
    public bool RequiresInvitationRemoval { get; set; }
    public bool CancellationBlocked { get; set; }
    public ArtifactManifestDto Manifest { get; set; } = new();
    public List<ReleaseInvitationSummary> BlockingInvitations { get; set; } = [];
}

public sealed class ReleaseDeploymentLease
{
    public string LeaseToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public List<string> RemovedInviteIds { get; set; } = [];
}

public sealed class ReleaseInvitationSummary
{
    public string IdHash { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string InstallProtocol { get; set; } = string.Empty;
    public bool CanRevoke { get; set; }
    public string BlockedReason { get; set; } = string.Empty;
}

public sealed class ReleaseCancellationResponse
{
    public int RemovedCount { get; set; }
    public List<string> RemovedInviteIds { get; set; } = [];
}

public sealed record DeployedReleaseArtifactRow(
    string Version,
    string File,
    string Size,
    string Sha256,
    string DownloadUrl);

internal sealed record ReleaseDeploymentLeaseRecovery(
    string TargetVersion,
    string DeploymentRevision,
    string LeaseToken,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Calls the gateway's signed preflight/revocation endpoints and delegates the
/// actual S3/CloudFront publish to the existing, audited source-release script.
/// This process never receives AWS credentials or private signing material as
/// command-line arguments; the established publisher obtains them from the
/// operator's normal secure Windows/AWS configuration.
/// </summary>
public sealed class ReleaseDeploymentService
{
    private const string ReleaseScriptRelativePath = "scripts\\Ensure-OpticonTargetRelease.ps1";
    private const string PublisherRelativePath = "fly-headscale\\scripts\\Publish-OpticonSourceRelease.ps1";
    private const string TimestampUrl = "http://timestamp.digicert.com";
    private readonly AdminState _state;
    private readonly HostedInviteClient _hostedInvites;

    public ReleaseDeploymentService(AdminState state)
    {
        _state = state;
        _hostedInvites = new HostedInviteClient(state);
    }

    public async Task<ReleaseDeploymentPreflight> PrepareAsync(
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var preflight = await _hostedInvites.GetReleasePreflightAsync(normalizedTarget, cancellationToken);
        ValidatePreflight(preflight, normalizedTarget);
        return preflight;
    }

    public async Task<ReleaseDeploymentLease> AcquireLeaseAsync(
        ReleaseDeploymentPreflight preflight,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (preflight.AlreadyDeployed || preflight.TargetIsOlder || preflight.DeploymentBlocked)
            throw new InvalidOperationException("The live release state cannot be acquired for deployment.");
        if (preflight.CancellationBlocked)
            throw new InvalidOperationException(
                "An active legacy invitation does not retain a safely revocable network key identity. " +
                "It must be reconciled before a new source release can replace it.");
        if (!IsSha256(preflight.DeploymentRevision) || !IsLeaseToken(leaseToken))
            throw new InvalidDataException("The gateway did not provide a release deployment snapshot token.");
        var lease = await _hostedInvites.AcquireReleaseLeaseAsync(
            preflight.TargetVersion,
            preflight.DeploymentRevision,
            leaseToken,
            cancellationToken);
        if (!string.Equals(lease.LeaseToken, leaseToken, StringComparison.Ordinal) || !IsLeaseToken(lease.LeaseToken))
            throw new InvalidDataException("The gateway did not confirm the requested release deployment lease.");
        return lease;
    }

    public static ReleaseDeploymentLease CreateLeaseCandidate(ReleaseDeploymentPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (!IsSha256(preflight.DeploymentRevision))
            throw new InvalidDataException("The gateway did not provide a release deployment snapshot token.");
        var random = RandomNumberGenerator.GetBytes(32);
        try
        {
            return new ReleaseDeploymentLease
            {
                LeaseToken = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                // The gateway returns the authoritative expiry after acquire.
                // This short local placeholder is persisted before the POST so
                // a lost response can be retried with the exact same token.
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(3)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    /// <summary>
    /// A resume is allowed only when the gateway's authenticated lease
    /// fingerprint matches the protected local bearer token. A stale candidate
    /// must never bypass the operator's confirmation for another administrator's
    /// active deployment.
    /// </summary>
    public static bool RecoveryMatchesLiveLease(
        ReleaseDeploymentPreflight preflight,
        ReleaseDeploymentLease? recovery)
    {
        if (recovery is null
            || !preflight.DeploymentBlocked
            || !IsLeaseToken(recovery.LeaseToken)
            || !IsSha256(preflight.LeaseTokenSha256))
            return false;

        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(recovery.LeaseToken));
        try
        {
            return CryptographicOperations.FixedTimeEquals(tokenHash, Convert.FromHexString(preflight.LeaseTokenSha256));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    public async Task<ReleaseCancellationResponse> RevokeActiveInvitationsAsync(
        ReleaseDeploymentPreflight preflight,
        ReleaseDeploymentLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(lease);
        if (!preflight.RequiresInvitationRemoval && !preflight.DeploymentBlocked)
            return new ReleaseCancellationResponse();
        if (preflight.CancellationBlocked)
            throw new InvalidOperationException(
                "An active legacy invitation does not retain a safely revocable network key identity. " +
                "It must be reconciled before a new source release can replace it.");
        if (!IsLeaseToken(lease.LeaseToken))
            throw new InvalidDataException("The gateway did not provide a valid release deployment lease.");
        return await _hostedInvites.RevokeActiveReleaseInvitationsAsync(
            preflight.TargetVersion,
            lease.LeaseToken,
            cancellationToken);
    }

    public async Task ReleaseLeaseAsync(ReleaseDeploymentLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsLeaseToken(lease.LeaseToken)) return;
        await _hostedInvites.ReleaseDeploymentLeaseAsync(lease.LeaseToken, cancellationToken);
    }

    public async Task FinalizeLeaseAsync(
        string targetVersion,
        ReleaseDeploymentLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsLeaseToken(lease.LeaseToken))
            throw new InvalidDataException("The protected Opticon release deployment lease is invalid.");
        await _hostedInvites.FinalizeDeploymentLeaseAsync(targetVersion, lease.LeaseToken, cancellationToken);
    }

    public async Task SaveLeaseRecoveryAsync(
        ReleaseDeploymentPreflight preflight,
        ReleaseDeploymentLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsLeaseToken(lease.LeaseToken) || !IsSha256(preflight.DeploymentRevision) ||
            lease.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The gateway returned an invalid release deployment lease.");
        var recovery = new ReleaseDeploymentLeaseRecovery(
            preflight.TargetVersion,
            preflight.DeploymentRevision,
            lease.LeaseToken,
            lease.ExpiresAt);
        _state.Config.ReleaseDeploymentLeaseProtected = SecretProtector.Protect(
            JsonSerializer.Serialize(recovery, JsonDefaults.Options));
        await _state.SaveAsync(cancellationToken);
    }

    public ReleaseDeploymentLease? TryGetLeaseRecovery(string targetVersion)
    {
        var protectedValue = _state.Config.ReleaseDeploymentLeaseProtected;
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try
        {
            var recovery = JsonSerializer.Deserialize<ReleaseDeploymentLeaseRecovery>(
                SecretProtector.Unprotect(protectedValue), JsonDefaults.Options);
            if (recovery is null
                || !string.Equals(recovery.TargetVersion, targetVersion, StringComparison.Ordinal)
                || !IsSha256(recovery.DeploymentRevision)
                || !IsLeaseToken(recovery.LeaseToken)
                || recovery.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;
            return new ReleaseDeploymentLease
            {
                LeaseToken = recovery.LeaseToken,
                ExpiresAt = recovery.ExpiresAt
            };
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new InvalidDataException("The protected Opticon release deployment recovery state is invalid.", exception);
        }
    }

    public async Task ClearLeaseRecoveryAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_state.Config.ReleaseDeploymentLeaseProtected)) return;
        _state.Config.ReleaseDeploymentLeaseProtected = string.Empty;
        await _state.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Builds, signs, uploads, and fully verifies the immutable source archive
    /// without changing the live invite manifest. This must finish before any
    /// accepted invitation is revoked, so a build/S3/CloudFront failure leaves
    /// the previous invite release completely usable.
    /// </summary>
    public async Task StageAsync(
        string targetVersion,
        string configuredWorkspace,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var prerequisites = ValidatePublisherPrerequisites(normalizedTarget, configuredWorkspace);
        progress?.Report($"Staging and verifying immutable source release {normalizedTarget}…");
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget).Append("-StageOnly").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The Opticon source archive could not be staged and verified. " + DescribePublisherFailure(result));
        progress?.Report($"Immutable source release {normalizedTarget} is staged and verified.");
    }

    public async Task PublishAsync(
        string targetVersion,
        string configuredWorkspace,
        ReleaseDeploymentLease lease,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var prerequisites = ValidatePublisherPrerequisites(normalizedTarget, configuredWorkspace);
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsLeaseToken(lease.LeaseToken))
            throw new InvalidDataException("A valid release deployment lease is required before the invite manifest can change.");

        progress?.Report($"Committing staged source release {normalizedTarget} to the invite manifest…");
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget).Append("-CommitStaged").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken,
            environment: new Dictionary<string, string?>
            {
                ["OPTICON_RELEASE_LEASE_TOKEN"] = lease.LeaseToken
            });
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The verified Opticon staged-release commit did not complete. " + DescribePublisherFailure(result));
        progress?.Report($"Source release {normalizedTarget} was committed and verified.");
    }

    public async Task VerifyPublisherReadinessAsync(
        string targetVersion,
        string configuredWorkspace,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var prerequisites = ValidatePublisherPrerequisites(normalizedTarget, configuredWorkspace);
        var arguments = PublisherArguments(prerequisites, normalizedTarget).Append("-CheckOnly").ToArray();
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            arguments,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The verified Opticon publisher is not ready. " + DescribePublisherFailure(result));
    }

    public ReleasePublisherPrerequisites ValidatePublisherPrerequisites(string targetVersion, string configuredWorkspace)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var workspace = ResolveWorkspace(string.IsNullOrWhiteSpace(configuredWorkspace)
            ? _state.Config.ReleaseWorkspacePath
            : configuredWorkspace);
        var sourceVersion = ReadWorkspaceVersion(workspace);
        if (!string.Equals(sourceVersion, normalizedTarget, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The trusted source workspace is version {sourceVersion}, but this Command Center is {normalizedTarget}. " +
                "Build and open the matching Command Center before publishing invitations.");
        return new ReleasePublisherPrerequisites(
            workspace,
            RequireRegularFile(Path.Combine(workspace, ReleaseScriptRelativePath), "Opticon target release script"),
            FindPowerShell7(),
            FindSignTool(),
            RequireControlOrigin(_state.Config.HeadscaleControlUrl));
    }

    public static string FindWorkspaceCandidate(AdminConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configured = config.ReleaseWorkspacePath?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var environment = Environment.GetEnvironmentVariable("OPTICON_RELEASE_WORKSPACE")?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(environment)) return environment;

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            {
                if (HasReleaseLayout(directory.FullName)) return directory.FullName;
            }
        }
        return string.Empty;
    }

    public static string ResolveWorkspace(string configuredWorkspace)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredWorkspace)
            ? FindWorkspaceCandidate(new AdminConfig())
            : configuredWorkspace.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException(
                "Choose the trusted Opticon source workspace before publishing. It must contain scripts\\Ensure-OpticonTargetRelease.ps1.");
        var workspace = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
        if (!HasReleaseLayout(workspace))
            throw new InvalidOperationException(
                "The release workspace must be the Opticon source root containing Directory.Build.props, " +
                "scripts\\Ensure-OpticonTargetRelease.ps1, and fly-headscale\\scripts\\Publish-OpticonSourceRelease.ps1.");
        return workspace;
    }

    public static string ReadWorkspaceVersion(string workspace)
    {
        var properties = RequireRegularFile(Path.Combine(workspace, "Directory.Build.props"), "Opticon source version file");
        try
        {
            var document = XDocument.Load(properties, LoadOptions.None);
            var value = document.Descendants("Version").Select(element => element.Value.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return NormalizeStableVersion(value ?? string.Empty);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidDataException("The Opticon source version file is not valid XML.", exception);
        }
    }

    public static IReadOnlyList<DeployedReleaseArtifactRow> ToArtifactRows(ArtifactManifestDto manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Artifacts
            .OrderByDescending(item => ParseStableVersion(item.Version))
            .ThenBy(item => item.File, StringComparer.Ordinal)
            .Select(item => new DeployedReleaseArtifactRow(
                item.Version,
                item.File,
                FormatBytes(item.Size),
                item.Sha256.ToLowerInvariant(),
                item.DownloadUrl))
            .ToArray();
    }

    private static void ValidatePreflight(ReleaseDeploymentPreflight preflight, string targetVersion)
    {
        if (preflight.SchemaVersion != 1
            || !string.Equals(preflight.TargetVersion, targetVersion, StringComparison.Ordinal)
            || !IsSha256(preflight.DeploymentRevision)
            || preflight.Manifest is null
            || preflight.Manifest.SchemaVersion != 2
            || preflight.Manifest.Artifacts.Count == 0)
            throw new InvalidDataException("The Opticon release gateway returned an unsupported deployment preflight.");

        var deployed = NormalizeStableVersion(preflight.DeployedVersion);
        if (!preflight.Manifest.Artifacts.Any(item => string.Equals(item.Version, deployed, StringComparison.Ordinal)))
            throw new InvalidDataException("The Opticon release gateway preflight has no deployed artifact for its reported version.");

        foreach (var artifact in preflight.Manifest.Artifacts)
        {
            var version = NormalizeStableVersion(artifact.Version);
            if (!string.Equals(artifact.Product, "OpticonSource", StringComparison.Ordinal)
                || !string.Equals(artifact.Architecture, "source", StringComparison.Ordinal)
                || !string.Equals(artifact.File, $"opticon-source-{version}.zip", StringComparison.Ordinal)
                || artifact.Size is < 1024 or > 256L * 1024 * 1024
                || !IsSha256(artifact.Sha256)
                || !IsSha256(artifact.SourceManifestSha256)
                || !string.Equals(artifact.SdkVersion, OpticonSourceReleaseClient.SupportedSdkVersion, StringComparison.Ordinal)
                || !string.Equals(artifact.RuntimeVersion, OpticonSourceReleaseClient.SupportedRuntimeVersion, StringComparison.Ordinal)
                || !string.Equals(artifact.SourceManifestKeyId, SourceReleaseSigning.KeyId, StringComparison.Ordinal)
                || !string.Equals(artifact.SigningProfile, BuildSigningTrust.ProfileName, StringComparison.Ordinal)
                || !string.Equals(artifact.ProductSignerThumbprint, ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
                || artifact.TargetRuntimes is null
                || !artifact.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal)
                || !IsImmutableCloudFrontUrl(artifact.DownloadUrl, version, artifact.File))
                throw new InvalidDataException("The Opticon release gateway preflight contains invalid source artifact metadata.");
        }

        if (preflight.AlreadyDeployed != string.Equals(deployed, targetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The Opticon release gateway preflight has inconsistent deployed-version state.");
        if (preflight.TargetIsOlder && preflight.RequiresInvitationRemoval)
            throw new InvalidDataException("The Opticon release gateway preflight requested invitation removal for a refused downgrade.");
        if (preflight.DeploymentBlocked && string.IsNullOrWhiteSpace(preflight.DeploymentBlockedReason))
            throw new InvalidDataException("The Opticon release gateway returned an incomplete deployment lock state.");
        if (!string.IsNullOrEmpty(preflight.LeaseTokenSha256) && !IsSha256(preflight.LeaseTokenSha256))
            throw new InvalidDataException("The Opticon release gateway returned an invalid deployment lease fingerprint.");
        if (preflight.RequiresInvitationRemoval)
        {
            if (preflight.BlockingInvitations.Count == 0)
                throw new InvalidDataException("The Opticon release gateway preflight omitted its active invitation snapshot.");
            if (preflight.CancellationBlocked != preflight.BlockingInvitations.Any(item => !item.CanRevoke))
                throw new InvalidDataException("The Opticon release gateway preflight has inconsistent invitation-revocation state.");
        }
        else if (preflight.BlockingInvitations.Count != 0)
        {
            throw new InvalidDataException("The Opticon release gateway preflight returned an unexpected invitation-removal plan.");
        }
    }

    private static bool HasReleaseLayout(string root)
    {
        try
        {
            return Directory.Exists(root)
                   && File.Exists(Path.Combine(root, "Directory.Build.props"))
                   && File.Exists(Path.Combine(root, ReleaseScriptRelativePath))
                   && File.Exists(Path.Combine(root, PublisherRelativePath));
        }
        catch (Exception) when (root.Length != 0)
        {
            return false;
        }
    }

    private static string RequireRegularFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException($"The {description} is unavailable or unsafe.", fullPath);
        return fullPath;
    }

    private static string RequireControlOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.AbsolutePath.TrimEnd('/'), string.Empty, StringComparison.Ordinal))
            throw new InvalidOperationException("The Opticon HTTPS control origin is not configured for release publishing.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string FindSignTool()
    {
        var kitsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "bin");
        if (!Directory.Exists(kitsRoot))
            throw new FileNotFoundException("Windows SignTool is required to publish an Opticon source release.", kitsRoot);
        var candidates = Directory.GetDirectories(kitsRoot)
            .Select(directory => new { Directory = directory, Version = Version.TryParse(Path.GetFileName(directory), out var version) ? version : null })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .Select(item => Path.Combine(item.Directory, "x64", "signtool.exe"));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)
                && (File.GetAttributes(candidate) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("The fixed x64 Windows SignTool is unavailable under Windows Kits.", kitsRoot);
    }

    private static string FindPowerShell7()
    {
        // The release scripts rely on modern .NET cryptography and filesystem
        // APIs unavailable in Windows PowerShell 5.1. Restrict discovery to
        // the official machine-wide installation or Microsoft Store package;
        // never resolve an arbitrary pwsh.exe from PATH/current directory.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var conventionalRoot = Path.GetFullPath(Path.Combine(programFiles, "PowerShell", "7"));
        var candidates = new List<string> { Path.Combine(conventionalRoot, "pwsh.exe") };
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        try
        {
            if (Directory.Exists(windowsApps))
            {
                candidates.AddRange(Directory.EnumerateDirectories(
                        windowsApps,
                        $"Microsoft.PowerShell_*_{architecture}__8wekyb3d8bbwe",
                        SearchOption.TopDirectoryOnly)
                    .Select(directory => Path.Combine(directory, "pwsh.exe")));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A conventional PowerShell 7 install remains sufficient. If it is
            // absent, report a precise prerequisite rather than probing PATH.
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var full = RequireRegularFile(candidate, "PowerShell 7");
                var inConventionalRoot = full.StartsWith(conventionalRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
                var parent = Directory.GetParent(full);
                var inStorePackage = parent is not null
                    && parent.FullName.StartsWith(Path.GetFullPath(windowsApps).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    && parent.Name.StartsWith("Microsoft.PowerShell_", StringComparison.OrdinalIgnoreCase)
                    && parent.Name.EndsWith($"_{architecture}__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase);
                if (!inConventionalRoot && !inStorePackage) continue;
                var version = FileVersionInfo.GetVersionInfo(full);
                // PowerShell 7.0 still runs on .NET Core 3.1, whose Convert
                // type does not expose FromHexString used by the guarded
                // publisher. PowerShell 7.1 moved to .NET 5 and is the oldest
                // trusted runtime which can execute that publisher contract.
                if (version.FileMajorPart > 7 || (version.FileMajorPart == 7 && version.FileMinorPart >= 1))
                    return full;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the next fixed trusted location.
            }
        }
        throw new FileNotFoundException(
            "PowerShell 7.1 or later is required in Program Files\\PowerShell\\7 or the official Microsoft.PowerShell Store package to publish an Opticon source release.");
    }

    private static string NormalizeStableVersion(string value)
    {
        var normalized = UpdatePackageVerifier.NormalizeVersion(value);
        _ = ParseStableVersion(normalized);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            throw new InvalidDataException("Opticon release versions must use exact stable major.minor.patch form.");
        return normalized;
    }

    private static Version ParseStableVersion(string value)
    {
        if (!Version.TryParse(value, out var parsed)
            || parsed.Major < 1
            || parsed.Minor < 0
            || parsed.Build < 0
            || parsed.Revision >= 0
            || !string.Equals(value, $"{parsed.Major}.{parsed.Minor}.{parsed.Build}", StringComparison.Ordinal))
            throw new InvalidDataException("Opticon release versions must use stable major.minor.patch form.");
        return parsed;
    }

    private static bool IsImmutableCloudFrontUrl(string value, string version, string file) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
        && uri.Host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
        && string.Equals(uri.AbsolutePath, $"/opticon/releases/{version}/{Uri.EscapeDataString(file)}", StringComparison.Ordinal);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsLeaseToken(string? value) => value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string[] PublisherArguments(ReleasePublisherPrerequisites prerequisites, string targetVersion) =>
    [
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-File", prerequisites.Script,
        "-Version", targetVersion,
        "-ControlOrigin", prerequisites.ControlOrigin,
        "-SigningProfile", BuildSigningTrust.ProfileName,
        "-SourceReleaseCertificateThumbprint", SourceReleaseSigning.KeyId,
        "-ProductCertificateThumbprint", ProductSigning.CertificateThumbprint,
        "-Rfc3161TimestampUrl", TimestampUrl,
        "-SignToolPath", prerequisites.SignTool
    ];

    private static string FormatBytes(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(value >= 100 || unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string DescribePublisherFailure(ProcessResult result)
    {
        var detail = string.Join(Environment.NewLine, new[] { result.StandardError, result.StandardOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (detail.Length > 1600) detail = detail[^1600..];
        return string.IsNullOrWhiteSpace(detail)
            ? $"Publisher exit code: {result.ExitCode}."
            : $"Publisher exit code: {result.ExitCode}. {detail}";
    }
}

public sealed record ReleasePublisherPrerequisites(
    string Workspace,
    string Script,
    string PowerShell,
    string SignTool,
    string ControlOrigin);
