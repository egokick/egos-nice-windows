using System.Net.Http.Headers;
using System.Net.Http.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record OpticonUpdateRelease(
    string Version,
    DeviceRole Role,
    string Architecture,
    Uri DownloadUri,
    long Size,
    string Sha256,
    bool RequiresMaintenanceBootstrap)
{
    public bool RequiresGuardianReconciliation { get; init; }
    public bool RequiresLegacyMachineStateMigration { get; init; }
    public bool IsLegacyMachineStateMigrationBridge { get; init; }
    public string LegacyMigrationSignerThumbprint { get; init; } = string.Empty;
}

public sealed class OpticonReleaseClient
{
    // Agent and the separately installed stable Guardian share the SSH
    // supervisor diagnostic contract. Crossing this boundary must use attended
    // maintenance so both binaries advance together.
    private static readonly Version GuardianApiBootstrapVersion =
        UpdatePackageVerifier.ParseVersion(RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion);

    private readonly HttpClient _http = new(new HttpClientHandler { CheckCertificateRevocationList = true })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public async Task<OpticonUpdateRelease?> FindUpdateAsync(
        AdminConfig config,
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var controlOrigin)
            || controlOrigin.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Opticon HTTPS control origin is not configured.");
        var manifestUri = new Uri(controlOrigin, "/opticon/artifacts/v1/manifest.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<ArtifactManifestDto>(JsonDefaults.Options, cancellationToken)
                       ?? throw new InvalidDataException("The Opticon release server returned an empty manifest.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("The Opticon release manifest schema is unsupported.");

        var architecture = string.IsNullOrWhiteSpace(device.Architecture) ? "x64" : device.Architecture.ToLowerInvariant();
        var current = UpdatePackageVerifier.ParseVersion(device.AgentVersion);
        var installedGuardian = ParseInstalledGuardianVersion(device.GuardianVersion);
        var matchingArtifacts = manifest.Artifacts
            .Where(artifact => artifact.Product.Equals("OpticonBundle", StringComparison.Ordinal)
                               && artifact.Role == device.Role
                               && artifact.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .Select(artifact => (Artifact: artifact, Version: ParseArtifactVersion(artifact)))
            .ToArray();

        var requiresLegacyMachineStateMigration = RemoteAdministrationProtocol.RequiresLegacyMachineStateMigration(
            current,
            UpdatePackageVerifier.ParseVersion(RemoteAdministrationProtocol.MinimumProtectedMachineStateAgentVersion));
        var isSupportedLegacySource = current == new Version(1, 1, 38);
        (ArtifactRecordDto Artifact, Version Version) selectedCandidate;
        var isLegacyMachineStateMigrationBridge = false;
        if (requiresLegacyMachineStateMigration)
        {
            if (!isSupportedLegacySource)
                throw new InvalidOperationException(
                    $"Opticon Agent {device.AgentVersion} uses an older legacy machine-state layout. " +
                    "The signed in-place bridge is supported only from Opticon Agent 1.1.38; no candidate was staged.");

            var bridges = matchingArtifacts
                .Where(candidate => RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
                    current, candidate.Version, candidate.Artifact.LegacyMigrationSignerThumbprint))
                .ToArray();
            if (bridges.Length != 1)
                throw new InvalidDataException(
                    "The release manifest must contain exactly one trusted Opticon 1.1.41 legacy machine-state bridge for this device role and architecture.");
            selectedCandidate = bridges[0];
            isLegacyMachineStateMigrationBridge = true;
        }
        else
        {
            var candidates = matchingArtifacts
                // A migration marker is never a normal release channel. Newer
                // Agents must ignore it even if the record is otherwise valid.
                .Where(candidate => string.IsNullOrEmpty(candidate.Artifact.LegacyMigrationSignerThumbprint))
                .Where(candidate => candidate.Version > current
                                    || (candidate.Version == current
                                        && installedGuardian < candidate.Version))
                .OrderByDescending(candidate => candidate.Version)
                .ToArray();
            if (candidates.Length == 0) return null;
            selectedCandidate = candidates[0];
        }

        var selected = selectedCandidate.Artifact;
        if (selected.Size is < 1024 or > 1024L * 1024 * 1024
            || selected.Sha256.Length != 64 || selected.Sha256.Any(character => !Uri.IsHexDigit(character))
            || Path.GetFileName(selected.File) != selected.File || !selected.File.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Opticon release record has invalid immutable artifact metadata.");
        if (isLegacyMachineStateMigrationBridge && !HasCanonicalLegacyBridgeOuterTrust(selected))
            throw new InvalidDataException(
                "The selected legacy machine-state bridge is missing its required OwnerManaged release trust metadata.");
        var download = ResolveDownloadUri(controlOrigin, selected);
        var requiresMaintenanceBootstrap = device.UpdateProtocolVersion < RemoteAdministrationProtocol.UpdateVersion
                                          || installedGuardian < GuardianApiBootstrapVersion;
        if (isLegacyMachineStateMigrationBridge && requiresMaintenanceBootstrap)
            throw new InvalidOperationException(
                "The signed legacy machine-state bridge requires the guarded Opticon update API and stable Guardian already present on Agent 1.1.38. " +
                "The retired maintenance bootstrap cannot launch this bridge.");

        return new OpticonUpdateRelease(
            selected.Version,
            device.Role,
            architecture,
            download,
            selected.Size,
            selected.Sha256.ToLowerInvariant(),
            requiresMaintenanceBootstrap)
        {
            RequiresGuardianReconciliation = installedGuardian < selectedCandidate.Version,
            RequiresLegacyMachineStateMigration = requiresLegacyMachineStateMigration
                                                   && !isLegacyMachineStateMigrationBridge,
            IsLegacyMachineStateMigrationBridge = isLegacyMachineStateMigrationBridge,
            LegacyMigrationSignerThumbprint = selected.LegacyMigrationSignerThumbprint
        };
    }

    private static Version ParseInstalledGuardianVersion(string value)
    {
        try { return UpdatePackageVerifier.ParseVersion(value); }
        catch (InvalidDataException) { return new Version(0, 0, 0); }
    }

    private static Version ParseArtifactVersion(ArtifactRecordDto artifact)
    {
        try { return UpdatePackageVerifier.ParseVersion(artifact.Version); }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"The release manifest contains an invalid Opticon version for {artifact.File}.", exception);
        }
    }

    private static bool HasCanonicalLegacyBridgeOuterTrust(ArtifactRecordDto artifact) =>
        artifact.SigningProfile.Equals("OwnerManaged", StringComparison.Ordinal)
        && IsThumbprint(artifact.SourceManifestKeyId)
        && IsThumbprint(artifact.ProductSignerThumbprint)
        && !artifact.SourceManifestKeyId.Equals(
            InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
        && !artifact.ProductSignerThumbprint.Equals(
            InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
        && !artifact.SourceManifestKeyId.Equals(
            artifact.ProductSignerThumbprint, StringComparison.OrdinalIgnoreCase);

    private static bool IsThumbprint(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static Uri ResolveDownloadUri(Uri controlOrigin, ArtifactRecordDto artifact)
    {
        // Empty remains the deliberately supported Fly-relative migration path.
        if (string.IsNullOrWhiteSpace(artifact.DownloadUrl))
            return new Uri(controlOrigin, "/opticon/artifacts/v1/" + Uri.EscapeDataString(artifact.File));

        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            throw new InvalidDataException("The Opticon release manifest contains an unsafe CloudFront download URL.");

        var expectedPath = "/opticon/releases/" + artifact.Version + "/" + Uri.EscapeDataString(artifact.File);
        if (!uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidDataException("The Opticon release manifest CloudFront URL does not match its immutable artifact record.");
        return uri;
    }
}
