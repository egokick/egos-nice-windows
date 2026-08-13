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
    public bool RequiresInvitationRemoval { get; set; }
    public bool CancellationBlocked { get; set; }
    public ArtifactManifestDto Manifest { get; set; } = new();
    public List<ReleaseInvitationSummary> BlockingInvitations { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public ClientInstallValidationPolicy OperatorValidationPolicy { get; set; } = new();
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

/// <summary>
/// Calls the gateway's signed preflight/revocation endpoints and delegates the
/// actual S3/CloudFront publish to the existing, audited source-release script.
/// This process never receives AWS credentials or private signing material as
/// command-line arguments; the established publisher obtains them from the
/// operator's normal secure Windows/AWS configuration.
/// </summary>
public sealed class ReleaseDeploymentService
{
    public const int RequiredGatewayReleaseProtocol = 3;
    private const string ReleaseScriptRelativePath = "scripts\\Ensure-OpticonTargetRelease.ps1";
    private const string PublisherRelativePath = "fly-headscale\\scripts\\Publish-OpticonSourceRelease.ps1";
    private const string BundlePublisherRelativePath = "fly-headscale\\scripts\\Publish-OpticonBundles.ps1";
    private const string TimestampUrl = "http://timestamp.digicert.com";
    private const string ExpectedWorkspaceDirectoryName = "opticon";
    private const string GatewayAppName = "taildesk-egokick-control";
    private const string GatewayDirectoryName = "fly-headscale";
    private readonly AdminState _state;
    private readonly HostedInviteClient _hostedInvites;
    private readonly HttpClient _dependencyHttp = DirectHttp.CreateClient(TimeSpan.FromMinutes(10));

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
        await DeployGatewayAsync(prerequisites, "Updating the Opticon Fly gateway required by this release…", progress, cancellationToken);

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

    /// <summary>
    /// Publishes the gateway image after a source archive has been staged. The
    /// source-only invitation's signed launcher is a sidecar copied into that
    /// image, so a source release cannot become live until this deployment has
    /// completed successfully.
    /// </summary>
    public async Task DeployGatewayForStagedReleaseAsync(
        string targetVersion,
        ReleasePublisherPrerequisites prerequisites,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        ValidatePublisherPrerequisites(prerequisites, normalizedTarget);
        var gatewayDirectory = RequireRegularDirectory(
            Path.Combine(prerequisites.Workspace, GatewayDirectoryName), "Opticon Fly gateway source directory");
        progress?.Report("Verifying the pinned Tailscale and RustDesk installers included with the gateway…");
        await VerifyGatewayDependencyInputsAsync(gatewayDirectory, cancellationToken);
        await DeployGatewayAsync(
            prerequisites,
            $"Updating the Opticon Fly gateway with the staged {normalizedTarget} signed installer…",
            progress,
            cancellationToken);
        progress?.Report("Verifying the deployed gateway serves every pinned Tailscale and RustDesk installer…");
        await VerifyHostedDependenciesAsync(cancellationToken);
    }

    public Task<ReleaseCancellationResponse> RevokeActiveInvitationsAsync(
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        return _hostedInvites.RevokeActiveReleaseInvitationsAsync(normalizedTarget, cancellationToken);
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
        progress?.Report($"Staging and verifying immutable source release {normalizedTarget}…");
        var publisherOutput = progress is null ? null : new Progress<string>(line => progress.Report("[publisher] " + line));
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget, validationPolicy, forceRedeploy).Append("-StageOnly").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken,
            environment: PublisherEnvironment(prerequisites),
            outputProgress: publisherOutput);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "The Opticon source archive could not be staged and verified. " + DescribePublisherFailure(result));
        progress?.Report($"Immutable source release {normalizedTarget} is staged and verified.");
    }

    public async Task PublishAsync(
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
        await VerifyStagedPublisherAsync(prerequisites, normalizedTarget, cancellationToken);

        progress?.Report($"Committing staged source release {normalizedTarget} to the invite manifest…");
        var environment = PublisherEnvironment(prerequisites);
        var publisherOutput = progress is null ? null : new Progress<string>(line => progress.Report("[publisher] " + line));
        var result = await ProcessRunner.RunAsync(
            prerequisites.PowerShell,
            PublisherArguments(prerequisites, normalizedTarget, validationPolicy, forceRedeploy).Append("-CommitStaged").ToArray(),
            TimeSpan.FromMinutes(45),
            cancellationToken,
            environment: environment,
            outputProgress: publisherOutput);
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
    /// path: a candidate must have the expected layout and no reparse-backed
    /// critical paths. Deployment intentionally does not inspect or modify Git.
    /// </summary>
    public async Task<ReleasePublisherPrerequisites> ResolvePublisherPrerequisitesAsync(
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        var normalizedTarget = NormalizeStableVersion(targetVersion);
        var canonical = GetCanonicalWorkspacePath();
        Exception? canonicalFailure = null;
        var matches = new List<string>();
        foreach (var candidate in EnumerateAutomaticWorkspaceCandidates())
        {
            if (!HasReleaseLayout(candidate)) continue;
            try
            {
                if (!string.Equals(ReadWorkspaceVersion(candidate), normalizedTarget, StringComparison.Ordinal))
                    continue;
                VerifyPublisherWorkspace(candidate, normalizedTarget);
                matches.Add(candidate);
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
        var canonicalMatch = matches.SingleOrDefault(match => SamePath(match, canonical));
        if (!string.IsNullOrEmpty(canonicalMatch))
            return CreatePublisherPrerequisites(canonicalMatch);

        if (matches.Count != 1)
            throw new InvalidOperationException(
                "More than one verified local Opticon publisher was found automatically. " +
                "No invitation or release file was changed.");

        return CreatePublisherPrerequisites(matches[0]);
    }

    private ReleasePublisherPrerequisites CreatePublisherPrerequisites(string workspace)
    {
        return new ReleasePublisherPrerequisites(
            workspace,
            RequireRegularFile(Path.Combine(workspace, ReleaseScriptRelativePath), "Opticon target release script"),
            FindPowerShell7(),
            FindSignTool(),
            RequireControlOrigin(_state.Config.HeadscaleControlUrl),
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
            // verified for the required Opticon source layout before use.
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

    private static void VerifyPublisherWorkspace(string workspace, string targetVersion)
    {
        var safeWorkspace = RequireRegularDirectory(workspace, "Opticon source workspace");
        if (!string.Equals(Path.GetFileName(safeWorkspace), ExpectedWorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The automatic Opticon publisher candidate is not the opticon source directory.");

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

    }

    private static Task VerifyStagedPublisherAsync(
        ReleasePublisherPrerequisites prerequisites,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        // This runs after the gateway may have removed invitations. It
        // deliberately performs no fetch/network synchronization: the exact
        // publisher selected before staging must remain byte-for-byte intact.
        ValidatePublisherPrerequisites(prerequisites, targetVersion);
        var workspace = prerequisites.Workspace;
        if (!string.Equals(GetFileSha256(Path.Combine(workspace, ReleaseScriptRelativePath)), prerequisites.ReleaseScriptSha256, StringComparison.Ordinal)
            || !string.Equals(GetFileSha256(Path.Combine(workspace, PublisherRelativePath)), prerequisites.SourcePublisherSha256, StringComparison.Ordinal)
            || !string.Equals(GetFileSha256(Path.Combine(workspace, BundlePublisherRelativePath)), prerequisites.BundlePublisherSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The verified Opticon publisher changed after the source archive was staged.");

        return Task.CompletedTask;
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
        _ = RequireRegularFile(prerequisites.SignTool, "Windows SignTool");
        if (!IsSha256(prerequisites.ReleaseScriptSha256)
            || !IsSha256(prerequisites.SourcePublisherSha256)
            || !IsSha256(prerequisites.BundlePublisherSha256))
            throw new InvalidDataException("The automatic Opticon publisher integrity record is invalid.");
    }

    private static async Task DeployGatewayAsync(
        ReleasePublisherPrerequisites prerequisites,
        string progressMessage,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var gatewayDirectory = RequireRegularDirectory(
            Path.Combine(prerequisites.Workspace, GatewayDirectoryName), "Opticon Fly gateway source directory");
        var flyConfig = RequireRegularFile(Path.Combine(gatewayDirectory, "fly.toml"), "Opticon Fly gateway configuration");
        var dockerfile = RequireRegularFile(Path.Combine(gatewayDirectory, "Dockerfile"), "Opticon Fly gateway Dockerfile");
        _ = RequireRegularFile(Path.Combine(gatewayDirectory, "gateway", "main.go"), "Opticon Fly gateway source");
        var configText = await File.ReadAllTextAsync(flyConfig, cancellationToken);
        if (!configText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Trim().Equals($"app = \"{GatewayAppName}\"", StringComparison.Ordinal)))
            throw new InvalidDataException("The verified Opticon Fly configuration does not target the fixed production gateway app.");
        var dockerfileText = File.ReadAllText(dockerfile);
        if (!dockerfileText.Contains("/opt/opticon/gateway", StringComparison.Ordinal))
            throw new InvalidDataException("The verified Opticon Fly Dockerfile does not contain the expected gateway entrypoint.");
        if (!dockerfileText.Contains("artifacts/ /opt/opticon/artifacts/", StringComparison.Ordinal))
            throw new InvalidDataException("The verified Opticon Fly Dockerfile does not copy the pinned dependency installers into the gateway image.");

        var fly = FindFlyCtl();
        var deployIgnoreFile = CreateGatewayDeployIgnoreFile(gatewayDirectory);
        progress?.Report(progressMessage);
        var flyOutput = progress is null
            ? null
            : new Progress<string>(line => progress.Report("[fly] " + line));
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                fly,
                ["deploy", "--app", GatewayAppName, "--remote-only", "--ha=false", "--yes", "--ignorefile", deployIgnoreFile],
                TimeSpan.FromMinutes(20),
                cancellationToken,
                workingDirectory: gatewayDirectory,
                outputProgress: flyOutput);
        }
        finally
        {
            try { File.Delete(deployIgnoreFile); } catch { }
        }
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"The Opticon Fly gateway update failed ({result.ExitCode}). {DescribeProcessFailure(result)}");
    }

    private static string CreateGatewayDeployIgnoreFile(string gatewayDirectory)
    {
        var manifestPath = RequireRegularFile(
            Path.Combine(gatewayDirectory, "artifacts", "manifest.json"), "Opticon gateway release manifest");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.GetInt32() != 2
            || !root.TryGetProperty("artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The gateway release manifest is not a source-only manifest.");

        var sourceLaunchers = artifacts.EnumerateArray()
            .Where(artifact => artifact.TryGetProperty("product", out var product)
                               && product.GetString() == "OpticonSource")
            .Select(artifact => artifact.TryGetProperty("sourceLauncherFile", out var launcher)
                ? launcher.GetString() ?? string.Empty
                : string.Empty)
            .ToArray();
        if (sourceLaunchers.Length != 1
            || !System.Text.RegularExpressions.Regex.IsMatch(
                sourceLaunchers[0], @"^opticon-source-launcher-[0-9]+\.[0-9]+\.[0-9]+\.exe$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("The source-only manifest does not declare exactly one safe gateway launcher.");
        _ = RequireRegularFile(
            Path.Combine(gatewayDirectory, "artifacts", sourceLaunchers[0]), "active Opticon source launcher");

        var baseIgnore = RequireRegularFile(Path.Combine(gatewayDirectory, ".dockerignore"), "Opticon gateway Docker ignore file");
        var temporary = Path.Combine(Path.GetTempPath(), $"opticon-fly-{Guid.NewGuid():N}.dockerignore");
        var lines = new[]
        {
            File.ReadAllText(baseIgnore).TrimEnd(),
            "# Include only the launcher selected by the current source-only manifest.",
            "artifacts/opticon-source-launcher-*.exe",
            $"!artifacts/{sourceLaunchers[0]}"
        };
        File.WriteAllText(temporary, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
        return temporary;
    }

    /// <summary>
    /// Source-only manifests intentionally omit dependency records, so prove
    /// that the exact MSI bytes which Docker will copy into the gateway image
    /// are present before Fly is allowed to replace the running gateway.
    /// </summary>
    private static async Task VerifyGatewayDependencyInputsAsync(
        string gatewayDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in DependencyArtifacts.All)
        {
            var path = RequireRegularFile(
                Path.Combine(gatewayDirectory, "artifacts", artifact.FileName),
                $"pinned {artifact.Product} gateway installer");
            var info = new FileInfo(path);
            if (info.Length != artifact.Size)
                throw new InvalidDataException(
                    $"The pinned {artifact.Product} gateway installer has an unexpected size: {artifact.FileName}.");

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyDependencyBytesAsync(stream, artifact, cancellationToken);
        }
    }

    /// <summary>
    /// The post-deploy check is deliberately a full byte-stream verification,
    /// not merely a health check or HEAD request. It is the final boundary
    /// before the caller can revoke any still-active invitations.
    /// </summary>
    private async Task VerifyHostedDependenciesAsync(CancellationToken cancellationToken)
    {
        foreach (var artifact in DependencyArtifacts.All)
        {
            if (!Uri.TryCreate(artifact.PrimaryUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("The pinned gateway dependency URL is invalid.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Clear();
            using var response = await _dependencyHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new InvalidDataException(
                    $"The deployed gateway did not serve pinned {artifact.Product} {artifact.Version} ({(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength != artifact.Size
                || response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException(
                    $"The deployed gateway returned invalid transport metadata for pinned {artifact.Product} {artifact.Version}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await VerifyDependencyBytesAsync(stream, artifact, cancellationToken);
        }
    }

    private static async Task VerifyDependencyBytesAsync(
        Stream stream,
        DependencyArtifact artifact,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > artifact.Size)
                throw new InvalidDataException($"Pinned {artifact.Product} {artifact.Version} exceeded its expected size.");
            hasher.AppendData(buffer, 0, read);
        }
        if (total != artifact.Size
            || !CryptographicOperations.FixedTimeEquals(
                hasher.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
            throw new InvalidDataException(
                $"Pinned {artifact.Product} {artifact.Version} does not match its required SHA-256.");
    }

    private static Dictionary<string, string?> PublisherEnvironment(ReleasePublisherPrerequisites prerequisites)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(windows, "System32");
        environment["PATH"] = system32;
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

    private static string GetFileSha256(string path)
    {
        var safePath = RequireRegularFile(path, "Opticon publisher file");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(safePath))).ToLowerInvariant();
    }

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
        var error = TailPublisherOutput(CleanPublisherOutput(result.StandardError), 900);
        var output = TailPublisherOutput(
            CleanPublisherOutput(result.StandardOutput), string.IsNullOrEmpty(error) ? 1500 : 500);
        var detail = string.Join(Environment.NewLine, new[]
        {
            string.IsNullOrEmpty(error) ? "" : "Publisher error:" + Environment.NewLine + error,
            string.IsNullOrEmpty(output) ? "" : "Recent publisher output:" + Environment.NewLine + output
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(detail)
            ? $"Publisher exit code: {result.ExitCode}."
            : $"Publisher exit code: {result.ExitCode}. {detail}";
    }

    private static string CleanPublisherOutput(string value)
    {
        var withoutAnsi = System.Text.RegularExpressions.Regex.Replace(
            value ?? string.Empty, "\u001B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        return new string(withoutAnsi
            .Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .ToArray()).Trim();
    }

    private static string TailPublisherOutput(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[^maximumLength..];

}

public sealed record ReleasePublisherPrerequisites(
    string Workspace,
    string Script,
    string PowerShell,
    string SignTool,
    string ControlOrigin,
    string ReleaseScriptSha256,
    string SourcePublisherSha256,
    string BundlePublisherSha256);
