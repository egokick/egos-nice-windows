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
    public int GatewayReleaseProtocol { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public string DeployedVersion { get; set; } = string.Empty;
    public bool AlreadyDeployed { get; set; }
    public bool ForceRedeploy { get; set; }
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
    [System.Text.Json.Serialization.JsonIgnore]
    public ClientInstallValidationPolicy OperatorValidationPolicy { get; set; } = new();
}

public sealed class ReleaseDeploymentLease
{
    public string LeaseToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public List<string> RemovedInviteIds { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ForceRedeploy { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ClientInstallValidationPolicy ValidationPolicy { get; set; } = new();
}

public sealed class ReleaseInvitationSummary
{
    public string IdHash { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
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
    DateTimeOffset ExpiresAt,
    bool ForceRedeploy,
    ClientInstallValidationPolicy ValidationPolicy);

/// <summary>
/// Calls the gateway's signed preflight/revocation endpoints and delegates the
/// actual S3/CloudFront publish to the existing, audited source-release script.
/// This process never receives AWS credentials or private signing material as
/// command-line arguments; the established publisher obtains them from the
/// operator's normal secure Windows/AWS configuration.
/// </summary>
public sealed class ReleaseDeploymentService
{
    public const int RequiredGatewayReleaseProtocol = 2;
    private const string ReleaseScriptRelativePath = "scripts\\Ensure-OpticonTargetRelease.ps1";
    private const string PublisherRelativePath = "fly-headscale\\scripts\\Publish-OpticonSourceRelease.ps1";
    private const string BundlePublisherRelativePath = "fly-headscale\\scripts\\Publish-OpticonBundles.ps1";
    private const string TimestampUrl = "http://timestamp.digicert.com";
    private const string ExpectedWorkspaceDirectoryName = "opticon";
    private const string ExpectedGitRemote = "https://github.com/egokick/egos-nice-windows.git";
    private const string GatewayAppName = "taildesk-egokick-control";
    private const string GatewayDirectoryName = "fly-headscale";
    private readonly AdminState _state;
    private readonly HostedInviteClient _hostedInvites;

    public ReleaseDeploymentService(AdminState state)
    {
        _state = state;
        _hostedInvites = new HostedInviteClient(state);
    }

    public async Task<ReleaseDeploymentPreflight> PrepareAsync(
        string targetVersion,
        bool forceRedeploy = false,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var preflight = await _hostedInvites.GetReleasePreflightAsync(normalizedTarget, forceRedeploy, cancellationToken);
        ValidatePreflight(preflight, normalizedTarget);
        return preflight;
    }

    public async Task<ReleaseDeploymentPreflight> EnsureGatewayCompatibilityAsync(
        string targetVersion,
        ReleaseDeploymentPreflight preflight,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (preflight.GatewayReleaseProtocol >= RequiredGatewayReleaseProtocol)
            return preflight;

        var prerequisites = await ResolvePublisherPrerequisitesAsync(targetVersion, cancellationToken);
        var gatewayDirectory = RequireRegularDirectory(
            Path.Combine(prerequisites.Workspace, GatewayDirectoryName), "Opticon Fly gateway source directory");
        var flyConfig = RequireRegularFile(Path.Combine(gatewayDirectory, "fly.toml"), "Opticon Fly gateway configuration");
        var dockerfile = RequireRegularFile(Path.Combine(gatewayDirectory, "Dockerfile"), "Opticon Fly gateway Dockerfile");
        _ = RequireRegularFile(Path.Combine(gatewayDirectory, "gateway", "main.go"), "Opticon Fly gateway source");
        var configText = await File.ReadAllTextAsync(flyConfig, cancellationToken);
        if (!configText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Trim().Equals($"app = \"{GatewayAppName}\"", StringComparison.Ordinal)))
            throw new InvalidDataException("The verified Opticon Fly configuration does not target the fixed production gateway app.");
        if (!File.ReadAllText(dockerfile).Contains("/opt/opticon/gateway", StringComparison.Ordinal))
            throw new InvalidDataException("The verified Opticon Fly Dockerfile does not contain the expected gateway entrypoint.");

        var fly = FindFlyCtl();
        progress?.Report("Updating the Opticon Fly gateway required by this release…");
        var result = await ProcessRunner.RunAsync(
            fly,
            ["deploy", "--app", GatewayAppName, "--remote-only", "--ha=false"],
            TimeSpan.FromMinutes(20),
            cancellationToken,
            workingDirectory: gatewayDirectory);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"The Opticon Fly gateway update failed ({result.ExitCode}). {DescribeProcessFailure(result)}");

        progress?.Report("Waiting for the updated Opticon Fly gateway protocol…");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (attempt != 0) await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var updated = await PrepareAsync(targetVersion, forceRedeploy: true, cancellationToken);
            if (updated.GatewayReleaseProtocol >= RequiredGatewayReleaseProtocol)
                return updated;
        }
        throw new InvalidOperationException(
            "Fly reported a healthy deployment, but the Opticon gateway did not expose the required release protocol.");
    }

    public async Task<ReleaseDeploymentLease> AcquireLeaseAsync(
        ReleaseDeploymentPreflight preflight,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (preflight.AlreadyDeployed || preflight.TargetIsOlder || preflight.DeploymentBlocked)
            throw new InvalidOperationException("The live release state cannot be acquired for deployment.");
        if (!IsSha256(preflight.DeploymentRevision) || !IsLeaseToken(leaseToken))
            throw new InvalidDataException("The gateway did not provide a release deployment snapshot token.");
        var lease = await _hostedInvites.AcquireReleaseLeaseAsync(
            preflight.TargetVersion,
            preflight.DeploymentRevision,
            leaseToken,
            preflight.ForceRedeploy,
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
            lease.ExpiresAt,
            preflight.ForceRedeploy,
            ClientInstallValidationPolicy.Normalize(preflight.OperatorValidationPolicy));
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
                ExpiresAt = recovery.ExpiresAt,
                ForceRedeploy = recovery.ForceRedeploy,
                ValidationPolicy = ClientInstallValidationPolicy.Normalize(recovery.ValidationPolicy)
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
        ReleasePublisherPrerequisites prerequisites,
        ClientInstallValidationPolicy validationPolicy,
        bool forceRedeploy,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        ValidatePublisherPrerequisites(prerequisites, normalizedTarget);
        await VerifyTrustedWorkspaceAsync(
            prerequisites.Workspace, normalizedTarget, prerequisites.SourceCommit, refreshOrigin: true, cancellationToken);
        progress?.Report($"Staging and verifying immutable source release {normalizedTarget}…");
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget, validationPolicy, forceRedeploy).Append("-StageOnly").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken,
            environment: PublisherEnvironment(prerequisites));
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The Opticon source archive could not be staged and verified. " + DescribePublisherFailure(result));
        progress?.Report($"Immutable source release {normalizedTarget} is staged and verified.");
    }

    public async Task PublishAsync(
        string targetVersion,
        ReleasePublisherPrerequisites prerequisites,
        ReleaseDeploymentLease lease,
        ClientInstallValidationPolicy validationPolicy,
        bool forceRedeploy,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        ValidatePublisherPrerequisites(prerequisites, normalizedTarget);
        await VerifyStagedPublisherAsync(prerequisites, normalizedTarget, cancellationToken);
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsLeaseToken(lease.LeaseToken))
            throw new InvalidDataException("A valid release deployment lease is required before the invite manifest can change.");

        progress?.Report($"Committing staged source release {normalizedTarget} to the invite manifest…");
        var environment = PublisherEnvironment(prerequisites);
        environment["OPTICON_RELEASE_LEASE_TOKEN"] = lease.LeaseToken;
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget, validationPolicy, forceRedeploy).Append("-CommitStaged").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken,
            environment: environment);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The verified Opticon staged-release commit did not complete. " + DescribePublisherFailure(result));
        progress?.Report($"Source release {normalizedTarget} was committed and verified.");
    }

    public async Task VerifyPublisherReadinessAsync(
        string targetVersion,
        ReleasePublisherPrerequisites prerequisites,
        ClientInstallValidationPolicy validationPolicy,
        bool forceRedeploy,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        ValidatePublisherPrerequisites(prerequisites, normalizedTarget);
        await VerifyTrustedWorkspaceAsync(
            prerequisites.Workspace, normalizedTarget, prerequisites.SourceCommit, refreshOrigin: true, cancellationToken);
        var arguments = PublisherArguments(prerequisites, normalizedTarget, validationPolicy, forceRedeploy).Append("-CheckOnly").ToArray();
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            arguments,
            TimeSpan.FromMinutes(5),
            cancellationToken,
            environment: PublisherEnvironment(prerequisites));
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The verified Opticon publisher is not ready. " + DescribePublisherFailure(result));
    }

    /// <summary>
    /// Finds the canonical local Opticon source checkout eligible to publish
    /// this Command Center's exact version. The UI never accepts a user supplied
    /// path: a candidate must have the expected layout, no reparse-backed
    /// critical paths, the official origin, and a clean main checkout. When
    /// local main is a clean fast-forward of origin/main, one-click deployment
    /// synchronizes that exact commit automatically before selecting it.
    /// </summary>
    public async Task<ReleasePublisherPrerequisites> ResolvePublisherPrerequisitesAsync(
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var canonical = GetCanonicalWorkspacePath();
        Exception? canonicalFailure = null;
        var matches = new List<(string Workspace, string Commit)>();
        foreach (var candidate in EnumerateAutomaticWorkspaceCandidates())
        {
            if (!HasReleaseLayout(candidate)) continue;
            try
            {
                if (!string.Equals(ReadWorkspaceVersion(candidate), normalizedTarget, StringComparison.Ordinal))
                    continue;
                var commit = await VerifyTrustedWorkspaceAsync(
                    candidate, normalizedTarget, expectedCommit: null, refreshOrigin: true, cancellationToken);
                matches.Add((candidate, commit));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A source-looking folder is never enough to trust. Keep
                // evaluating the bounded candidates, then fail closed below.
                if (SamePath(candidate, canonical)) canonicalFailure = exception;
            }
        }

        if (matches.Count == 0)
        {
            if (canonicalFailure is not null)
                throw new InvalidOperationException(
                    "The canonical Opticon publisher could not be prepared automatically. " +
                    canonicalFailure.GetBaseException().Message +
                    " No invitation or release file was changed.", canonicalFailure);
            throw new InvalidOperationException(
                $"No verified local Opticon publisher matching Command Center {normalizedTarget} was found automatically. " +
                "No invitation or release file was changed.");
        }
        // The standard checkout is deterministic and wins when present. Other
        // bounded candidates are transition fallbacks only; if the canonical
        // location is absent, ambiguity fails closed below.
        var canonicalMatch = matches.SingleOrDefault(match => SamePath(match.Workspace, canonical));
        if (!string.IsNullOrEmpty(canonicalMatch.Workspace))
            return CreatePublisherPrerequisites(canonicalMatch.Workspace, canonicalMatch.Commit);

        if (matches.Count != 1)
            throw new InvalidOperationException(
                "More than one verified local Opticon publisher was found automatically. " +
                "No invitation or release file was changed.");

        return CreatePublisherPrerequisites(matches[0].Workspace, matches[0].Commit);
    }

    private ReleasePublisherPrerequisites CreatePublisherPrerequisites(string workspace, string sourceCommit)
    {
        var git = FindGit();
        return new ReleasePublisherPrerequisites(
            workspace,
            RequireRegularFile(Path.Combine(workspace, ReleaseScriptRelativePath), "Opticon target release script"),
            FindPowerShell7(),
            git,
            FindSignTool(),
            RequireControlOrigin(_state.Config.HeadscaleControlUrl),
            sourceCommit,
            GetFileSha256(Path.Combine(workspace, ReleaseScriptRelativePath)),
            GetFileSha256(Path.Combine(workspace, PublisherRelativePath)),
            GetFileSha256(Path.Combine(workspace, BundlePublisherRelativePath)));
    }

    private static IEnumerable<string> EnumerateAutomaticWorkspaceCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Candidates()
        {
            var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)
                ?? Path.GetPathRoot(Environment.CurrentDirectory)
                ?? @"C:\\";
            var sourceRoot = Path.Combine(driveRoot, "source");
            yield return GetCanonicalWorkspacePath();

            // Developers normally keep checked-out repositories immediately
            // below C:\\source. This is deliberately one directory deep rather
            // than a recursive disk search; every candidate is subsequently
            // proven against the official Git remote before use.
            string[] repositories;
            try { repositories = Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The canonical candidate or another bounded location may still
                // be available; inaccessible folders are not trusted guesses.
                repositories = [];
            }
            foreach (var repository in repositories)
                yield return Path.Combine(repository, ExpectedWorkspaceDirectoryName);

            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                DirectoryInfo? directory;
                try { directory = new DirectoryInfo(Path.GetFullPath(start)); }
                catch (Exception) { continue; }
                for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
                {
                    if (string.Equals(directory.Name, ExpectedWorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
                        yield return directory.FullName;
                }
            }
        }

        foreach (var candidate in Candidates())
        {
            string fullPath;
            try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)); }
            catch (Exception) { continue; }
            if (seen.Add(fullPath)) yield return fullPath;
        }
    }

    private static string GetCanonicalWorkspacePath()
    {
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)
            ?? Path.GetPathRoot(Environment.CurrentDirectory)
            ?? @"C:\\";
        return Path.Combine(driveRoot, "source", "egos-nice-windows", ExpectedWorkspaceDirectoryName);
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

    private static async Task<string> VerifyTrustedWorkspaceAsync(
        string workspace,
        string targetVersion,
        string? expectedCommit,
        bool refreshOrigin,
        CancellationToken cancellationToken)
    {
        var safeWorkspace = RequireRegularDirectory(workspace, "Opticon source workspace");
        if (!string.Equals(Path.GetFileName(safeWorkspace), ExpectedWorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The automatic Opticon publisher candidate is not the opticon source directory.");

        var repository = Directory.GetParent(safeWorkspace)?.FullName
            ?? throw new InvalidDataException("The automatic Opticon publisher candidate has no repository root.");
        repository = RequireRegularDirectory(repository, "Opticon source repository root");
        RequireRegularFile(Path.Combine(safeWorkspace, "Directory.Build.props"), "Opticon source version file");
        RequireRegularFile(Path.Combine(safeWorkspace, "Taildesk.sln"), "Opticon solution file");
        RequireRegularDirectory(Path.Combine(safeWorkspace, "scripts"), "Opticon source scripts directory");
        RequireRegularDirectory(Path.Combine(safeWorkspace, "fly-headscale"), "Opticon gateway source directory");
        RequireRegularDirectory(Path.Combine(safeWorkspace, "fly-headscale", "scripts"), "Opticon publisher scripts directory");
        RequireRegularFile(Path.Combine(safeWorkspace, ReleaseScriptRelativePath), "Opticon target release script");
        RequireRegularFile(Path.Combine(safeWorkspace, PublisherRelativePath), "Opticon source publisher script");
        RequireRegularFile(Path.Combine(safeWorkspace, BundlePublisherRelativePath), "Opticon source bundle publisher script");

        if (!string.Equals(ReadWorkspaceVersion(safeWorkspace), targetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The automatic Opticon publisher candidate does not match this Command Center version.");

        var git = FindGit();
        var topLevel = await ReadGitValueAsync(git, safeWorkspace, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (!SamePath(topLevel, repository))
            throw new InvalidDataException("The automatic Opticon publisher candidate is not rooted in its expected Git repository.");

        var remote = await ReadGitValueAsync(git, safeWorkspace, ["remote", "get-url", "origin"], cancellationToken);
        if (!string.Equals(remote.Trim().TrimEnd('/'), ExpectedGitRemote, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The automatic Opticon publisher candidate does not use the official Opticon Git origin.");

        var branch = await ReadGitValueAsync(git, safeWorkspace, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        if (!string.Equals(branch, "main", StringComparison.Ordinal))
            throw new InvalidDataException("The automatic Opticon publisher candidate is not on the main branch.");

        var status = await ReadGitValueAsync(git, safeWorkspace,
            ["status", "--porcelain", "--untracked-files=all", "--", "."], cancellationToken);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidDataException("The automatic Opticon publisher candidate has uncommitted source changes.");

        if (refreshOrigin)
            await RunGitCommandAsync(git, safeWorkspace,
                ["fetch", "--quiet", "--no-tags", ExpectedGitRemote, "refs/heads/main:refs/remotes/origin/main"], cancellationToken);
        var head = await ReadGitValueAsync(git, safeWorkspace, ["rev-parse", "HEAD"], cancellationToken);
        var originMain = await ReadGitValueAsync(git, safeWorkspace, ["rev-parse", "refs/remotes/origin/main"], cancellationToken);
        if (!string.Equals(head, originMain, StringComparison.OrdinalIgnoreCase))
        {
            var mergeBase = await ReadGitValueAsync(
                git, safeWorkspace, ["merge-base", head, originMain], cancellationToken);
            if (!string.Equals(mergeBase, originMain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The automatic Opticon publisher cannot synchronize because local main and origin/main have diverged.");
            await RunGitCommandAsync(git, safeWorkspace,
                ["push", "--porcelain", ExpectedGitRemote, $"{head}:refs/heads/main"], cancellationToken);
            await RunGitCommandAsync(git, safeWorkspace,
                ["fetch", "--quiet", "--no-tags", ExpectedGitRemote, "refs/heads/main:refs/remotes/origin/main"], cancellationToken);
            originMain = await ReadGitValueAsync(
                git, safeWorkspace, ["rev-parse", "refs/remotes/origin/main"], cancellationToken);
            if (!string.Equals(head, originMain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The automatic Opticon publisher push completed without synchronizing origin/main to the selected commit.");
        }
        if (expectedCommit is not null && !string.Equals(head, expectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The automatic Opticon publisher source changed after it was selected.");
        return head;
    }

    private static async Task VerifyStagedPublisherAsync(
        ReleasePublisherPrerequisites prerequisites,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        // This runs after the release lease may have removed invitations. It
        // deliberately performs no fetch/network synchronization: the exact
        // publisher selected before staging must remain byte-for-byte intact.
        ValidatePublisherPrerequisites(prerequisites, targetVersion);
        var workspace = prerequisites.Workspace;
        if (!string.Equals(GetFileSha256(Path.Combine(workspace, ReleaseScriptRelativePath)), prerequisites.ReleaseScriptSha256, StringComparison.Ordinal)
            || !string.Equals(GetFileSha256(Path.Combine(workspace, PublisherRelativePath)), prerequisites.SourcePublisherSha256, StringComparison.Ordinal)
            || !string.Equals(GetFileSha256(Path.Combine(workspace, BundlePublisherRelativePath)), prerequisites.BundlePublisherSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The verified Opticon publisher changed after the source archive was staged.");

        var git = FindGit();
        var head = await ReadGitValueAsync(git, workspace, ["rev-parse", "HEAD"], cancellationToken);
        if (!string.Equals(head, prerequisites.SourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The verified Opticon publisher commit changed after the source archive was staged.");
        var status = await ReadGitValueAsync(git, workspace,
            ["status", "--porcelain", "--untracked-files=all", "--", "."], cancellationToken);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidDataException("The verified Opticon publisher has source changes after the source archive was staged.");
    }

    private static void ValidatePublisherPrerequisites(ReleasePublisherPrerequisites prerequisites, string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        var workspace = RequireRegularDirectory(prerequisites.Workspace, "Opticon source workspace");
        if (!SamePath(workspace, prerequisites.Workspace)
            || !string.Equals(ReadWorkspaceVersion(workspace), targetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The automatic Opticon publisher changed after it was verified.");
        if (!SamePath(RequireRegularFile(Path.Combine(workspace, ReleaseScriptRelativePath), "Opticon target release script"), prerequisites.Script))
            throw new InvalidDataException("The automatic Opticon target release script changed after it was verified.");
        _ = RequireRegularFile(prerequisites.PowerShell, "PowerShell 7");
        if (!SamePath(RequireRegularFile(prerequisites.Git, "Git"), FindGit()))
            throw new InvalidDataException("The fixed Git executable changed after the Opticon publisher was selected.");
        _ = RequireRegularFile(prerequisites.SignTool, "Windows SignTool");
        if (!IsGitCommit(prerequisites.SourceCommit)
            || !IsSha256(prerequisites.ReleaseScriptSha256)
            || !IsSha256(prerequisites.SourcePublisherSha256)
            || !IsSha256(prerequisites.BundlePublisherSha256))
            throw new InvalidDataException("The automatic Opticon publisher integrity record is invalid.");
    }

    private static async Task<string> ReadGitValueAsync(
        string git,
        string workspace,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(git, workspace, command, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidDataException("The automatic Opticon publisher candidate could not be verified with Git. " + DescribeGitFailure(result));
        return result.StandardOutput.Trim();
    }

    private static async Task RunGitCommandAsync(
        string git,
        string workspace,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(git, workspace, command, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidDataException("The automatic Opticon publisher candidate could not be synchronized with Git. " + DescribeGitFailure(result));
    }

    private static Task<ProcessResult> RunGitAsync(
        string git,
        string workspace,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var repository = Directory.GetParent(workspace)?.FullName
            ?? throw new InvalidDataException("The automatic Opticon publisher candidate has no repository root.");
        var trustedRepository = Path.GetFullPath(repository).Replace('\\', '/');
        var arguments = new List<string>
        {
            "-c", $"safe.directory={trustedRepository}",
            "-c", "core.hooksPath=NUL",
            "-c", "core.fsmonitor=false",
            "-c", command.Count != 0 && command[0].Equals("push", StringComparison.Ordinal)
                ? "credential.helper=manager"
                : "credential.helper=",
            "-C", workspace
        };
        arguments.AddRange(command);
        return ProcessRunner.RunAsync(
            git,
            arguments,
            TimeSpan.FromMinutes(2),
            cancellationToken,
            environment: GitEnvironment());
    }

    private static IReadOnlyDictionary<string, string?> GitEnvironment() => new Dictionary<string, string?>
    {
        ["GIT_DIR"] = null,
        ["GIT_WORK_TREE"] = null,
        ["GIT_INDEX_FILE"] = null,
        ["GIT_PREFIX"] = null,
        ["GIT_CONFIG_COUNT"] = "0",
        ["GIT_CONFIG_GLOBAL"] = "NUL",
        ["GIT_CONFIG_SYSTEM"] = "NUL",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = null,
        ["SSH_ASKPASS"] = null
    };

    private static Dictionary<string, string?> PublisherEnvironment(ReleasePublisherPrerequisites prerequisites)
    {
        var environment = new Dictionary<string, string?>(GitEnvironment(), StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(windows, "System32");
        environment["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(prerequisites.Git), system32);
        environment["PATHEXT"] = ".COM;.EXE";
        return environment;
    }

    private static string FindFlyCtl()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(programFiles, "flyctl", "flyctl.exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages",
                "Fly-io.flyctl_Microsoft.Winget.Source_8wekyb3d8bbwe", "flyctl.exe")
        };
        foreach (var candidate in candidates)
        {
            try { return RequireRegularFile(candidate, "Fly CLI"); }
            catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException) { }
        }
        throw new FileNotFoundException(
            "The official Fly CLI is required to update Opticon's deployment gateway automatically. Install Fly-io.flyctl with WinGet, then retry Redeploy.");
    }

    private static string DescribeProcessFailure(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        detail = detail.Trim();
        return detail.Length <= 4096 ? detail : detail[^4096..];
    }

    private static string FindGit()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var gitRoot = Path.GetFullPath(Path.Combine(programFiles, "Git"));
        foreach (var candidate in new[]
                 {
                     Path.Combine(gitRoot, "cmd", "git.exe"),
                     Path.Combine(gitRoot, "bin", "git.exe")
                 })
        {
            try
            {
                var git = RequireRegularFile(candidate, "Git");
                if (IsChildPath(git, gitRoot)) return git;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the other fixed Program Files Git path. Never use PATH.
            }
        }
        throw new FileNotFoundException("Git is required under Program Files\\Git to verify the automatic Opticon publisher.", gitRoot);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsChildPath(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
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
            || preflight.GatewayReleaseProtocol < 0
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

        if (preflight.AlreadyDeployed != (!preflight.ForceRedeploy
                                          && string.Equals(deployed, targetVersion, StringComparison.Ordinal)))
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
            if (preflight.CancellationBlocked)
                throw new InvalidDataException("The Opticon release gateway returned an obsolete blocked cancellation plan.");
            if (preflight.BlockingInvitations.Any(item =>
                    !item.CanRevoke && string.IsNullOrWhiteSpace(item.BlockedReason)))
                throw new InvalidDataException("The Opticon release gateway omitted the legacy invitation abandonment warning.");
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
                   && File.Exists(Path.Combine(root, "Taildesk.sln"))
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

    private static string RequireRegularDirectory(string path, string description)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException($"The {description} is unavailable or unsafe: {fullPath}");
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
            // WindowsApps normally denies directory enumeration to an
            // unelevated desktop process. Resolve packages registered for the
            // current user first so an official Store/MSIX installation remains
            // usable without granting Opticon access to the package repository.
            var packageManager = new Windows.Management.Deployment.PackageManager();
            candidates.AddRange(packageManager.FindPackagesForUser(string.Empty)
                .Where(package => string.Equals(package.Id.Name, "Microsoft.PowerShell", StringComparison.Ordinal)
                    && string.Equals(package.Id.PublisherId, "8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
                    && package.Id.Architecture.ToString().Equals(architecture, StringComparison.OrdinalIgnoreCase))
                .Select(package => Path.Combine(package.InstalledPath, "pwsh.exe")));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
        {
            // Fall back to the fixed conventional and package-repository paths.
        }
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

    private static bool IsGitCommit(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static string GetFileSha256(string path)
    {
        var safePath = RequireRegularFile(path, "Opticon publisher file");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(safePath))).ToLowerInvariant();
    }

    private static bool IsLeaseToken(string? value) => value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string[] PublisherArguments(
        ReleasePublisherPrerequisites prerequisites,
        string targetVersion,
        ClientInstallValidationPolicy validationPolicy,
        bool forceRedeploy)
    {
        var policy = ClientInstallValidationPolicy.Normalize(validationPolicy);
        var encodedPolicy = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(policy, JsonDefaults.Options));
        var arguments = new List<string>
        {
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
        "-SignToolPath", prerequisites.SignTool,
        "-ClientInstallValidationBase64", encodedPolicy
        };
        if (forceRedeploy) arguments.Add("-ForceRedeploy");
        return [.. arguments];
    }

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

    private static string DescribeGitFailure(ProcessResult result)
    {
        var detail = string.Join(Environment.NewLine, new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (detail.Length > 500) detail = detail[^500..];
        return string.IsNullOrWhiteSpace(detail)
            ? $"Git exit code: {result.ExitCode}."
            : $"Git exit code: {result.ExitCode}. {detail}";
    }
}

public sealed record ReleasePublisherPrerequisites(
    string Workspace,
    string Script,
    string PowerShell,
    string Git,
    string SignTool,
    string ControlOrigin,
    string SourceCommit,
    string ReleaseScriptSha256,
    string SourcePublisherSha256,
    string BundlePublisherSha256);
