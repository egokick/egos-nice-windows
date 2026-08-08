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
    bool RequiresMaintenanceBootstrap);

public sealed class OpticonReleaseClient
{
    // Agent and the separately installed stable Guardian share the SSH
    // supervisor diagnostic contract. Crossing this boundary must use attended
    // maintenance so both binaries advance together.
    private static readonly Version GuardianSshMaintenanceVersion = new(1, 1, 31);

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
        var requiresGuardianMaintenance = installedGuardian < GuardianSshMaintenanceVersion;
        var candidates = manifest.Artifacts
            .Where(artifact => artifact.Product.Equals("OpticonBundle", StringComparison.Ordinal)
                               && artifact.Role == device.Role
                               && artifact.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .Select(artifact => (Artifact: artifact, Version: ParseArtifactVersion(artifact)))
            .Where(candidate => candidate.Version > current
                                || (candidate.Version == current
                                    && candidate.Version >= GuardianSshMaintenanceVersion
                                    && requiresGuardianMaintenance))
            .OrderByDescending(candidate => candidate.Version)
            .ToArray();
        if (candidates.Length == 0) return null;

        var selectedCandidate = candidates[0];
        var selected = selectedCandidate.Artifact;
        if (selected.Size is < 1024 or > 1024L * 1024 * 1024
            || selected.Sha256.Length != 64 || selected.Sha256.Any(character => !Uri.IsHexDigit(character))
            || Path.GetFileName(selected.File) != selected.File || !selected.File.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Opticon release record has invalid immutable artifact metadata.");
        var download = ResolveDownloadUri(controlOrigin, selected);
        return new OpticonUpdateRelease(
            selected.Version,
            device.Role,
            architecture,
            download,
            selected.Size,
            selected.Sha256.ToLowerInvariant(),
            device.UpdateProtocolVersion < RemoteAdministrationProtocol.UpdateVersion
            || (selectedCandidate.Version >= GuardianSshMaintenanceVersion && requiresGuardianMaintenance));
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
