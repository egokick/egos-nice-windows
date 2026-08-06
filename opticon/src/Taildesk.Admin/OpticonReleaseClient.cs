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
        var candidates = manifest.Artifacts
            .Where(artifact => artifact.Product.Equals("OpticonBundle", StringComparison.Ordinal)
                               && artifact.Role == device.Role
                               && artifact.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .Select(artifact => (Artifact: artifact, Version: ParseArtifactVersion(artifact)))
            .Where(candidate => candidate.Version > current)
            .OrderByDescending(candidate => candidate.Version)
            .ToArray();
        if (candidates.Length == 0) return null;

        var selected = candidates[0].Artifact;
        if (selected.Size is < 1024 or > 1024L * 1024 * 1024
            || selected.Sha256.Length != 64 || selected.Sha256.Any(character => !Uri.IsHexDigit(character))
            || Path.GetFileName(selected.File) != selected.File || !selected.File.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Opticon release record has invalid immutable artifact metadata.");
        var download = new Uri(controlOrigin, "/opticon/artifacts/v1/" + Uri.EscapeDataString(selected.File));
        return new OpticonUpdateRelease(
            selected.Version,
            device.Role,
            architecture,
            download,
            selected.Size,
            selected.Sha256.ToLowerInvariant(),
            device.UpdateProtocolVersion < RemoteAdministrationProtocol.UpdateVersion);
    }

    private static Version ParseArtifactVersion(ArtifactRecordDto artifact)
    {
        try { return UpdatePackageVerifier.ParseVersion(artifact.Version); }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"The release manifest contains an invalid Opticon version for {artifact.File}.", exception);
        }
    }
}
