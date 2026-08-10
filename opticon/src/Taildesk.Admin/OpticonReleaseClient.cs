using System.Net.Http.Headers;
using System.Text.Json;
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
    // A pre-source Agent or Guardian cannot understand the source transaction
    // journal.  Returning a release with this bit lets the UI give an explicit
    // attended clean-reinstall instruction without ever attempting a stage.
    public bool RequiresCleanReinstall { get; init; }

    // The source archive carries the immutable pins used to construct the
    // authenticated SourceUpdateRequest.  It is deliberately separate from
    // the outer display fields above so callers cannot confuse it with a
    // legacy executable bundle.
    public OpticonSourceRelease? SourceRelease { get; init; }
}

public sealed class OpticonReleaseClient
{
    private static readonly Version SourceUpdateFloor =
        UpdatePackageVerifier.ParseVersion(SourceUpdateProtocol.MinimumGuardianVersion);

    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(45));

    public async Task<OpticonUpdateRelease?> FindUpdateAsync(
        AdminConfig config,
        DeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        if (!Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var controlOrigin)
            || controlOrigin.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Opticon HTTPS control origin is not configured.");

        var manifestUri = new Uri(controlOrigin, "/opticon/artifacts/v1/manifest.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifestBytes = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        var manifest = JsonSerializer.Deserialize<ArtifactManifestDto>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release server returned an empty manifest.");
        if (manifest.SchemaVersion != 2)
            throw new InvalidDataException("The Opticon remote-update manifest must use the source-only schema.");
        if (manifest.Artifacts.Count != 1)
            throw new InvalidDataException("The source-only remote-update manifest must contain exactly one OpticonSource archive.");

        var source = manifest.Artifacts[0];
        var sourceVersion = ValidateSourceArtifact(source);
        var architecture = ResolveArchitecture(device.Architecture);
        var sourceRelease = new OpticonSourceRelease(
            UpdatePackageVerifier.NormalizeVersion(source.Version),
            source.File,
            source.Size,
            source.Sha256.ToLowerInvariant(),
            source.SdkVersion,
            source.RuntimeVersion,
            source.SourceManifestSha256.ToLowerInvariant(),
            source.SourceManifestKeyId,
            source.SigningProfile,
            source.ProductSignerThumbprint,
            source.TargetRuntimes.ToArray(),
            RequireImmutableCloudFrontDownload(source),
            OpticonSourceReleaseClient.SourceInstallProtocol);

        var installedAgent = UpdatePackageVerifier.ParseVersion(device.AgentVersion);
        var installedGuardian = ParseInstalledGuardianVersion(device.GuardianVersion);
        var requiresCleanReinstall = installedAgent < SourceUpdateFloor
                                     || installedGuardian < SourceUpdateFloor;
        if (requiresCleanReinstall || sourceVersion > installedAgent)
        {
            return new OpticonUpdateRelease(
                sourceRelease.Version,
                device.Role,
                architecture,
                sourceRelease.DownloadUri,
                sourceRelease.Size,
                sourceRelease.Sha256,
                RequiresMaintenanceBootstrap: false)
            {
                RequiresCleanReinstall = requiresCleanReinstall,
                SourceRelease = sourceRelease
            };
        }

        return null;
    }

    private static Version ValidateSourceArtifact(ArtifactRecordDto source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Version))
            throw new InvalidDataException("The source-only remote-update manifest has no valid source version.");
        var normalizedVersion = UpdatePackageVerifier.NormalizeVersion(source.Version);
        var version = UpdatePackageVerifier.ParseVersion(source.Version);
        if (!string.Equals(source.Product, "OpticonSource", StringComparison.Ordinal)
            || source.Role is not null
            || !string.Equals(source.Architecture, "source", StringComparison.Ordinal)
            || !string.Equals(source.Version, normalizedVersion, StringComparison.Ordinal)
            || version < SourceUpdateFloor
            || !string.Equals(source.File, $"opticon-source-{normalizedVersion}.zip", StringComparison.Ordinal)
            || source.Size is < 1024 or > 256L * 1024 * 1024
            || !IsSha256(source.Sha256)
            || !string.Equals(source.SdkVersion, SourceUpdateProtocol.RequiredSdkVersion, StringComparison.Ordinal)
            || !string.Equals(source.RuntimeVersion, SourceUpdateProtocol.RequiredRuntimeVersion, StringComparison.Ordinal)
            || !IsSha256(source.SourceManifestSha256)
            || !string.Equals(source.SourceManifestKeyId, SourceReleaseSigning.KeyId, StringComparison.Ordinal)
            || !string.Equals(source.SigningProfile, BuildSigningTrust.ProfileName, StringComparison.Ordinal)
            || !BuildSigningTrust.IsPublishable
            || !string.Equals(source.ProductSignerThumbprint, ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
            || string.Equals(source.SourceManifestKeyId, InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.ProductSignerThumbprint, InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.SourceManifestKeyId, source.ProductSignerThumbprint, StringComparison.OrdinalIgnoreCase)
            || source.TargetRuntimes is null
            || !source.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal)
            || !string.IsNullOrEmpty(source.SignerThumbprint)
            || !string.IsNullOrEmpty(source.LegacyMigrationSignerThumbprint)
            || !string.IsNullOrEmpty(source.TargetRuntime))
            throw new InvalidDataException("The source-only remote-update manifest has invalid immutable source metadata.");
        return version;
    }

    private static Uri RequireImmutableCloudFrontDownload(ArtifactRecordDto source)
    {
        if (!Uri.TryCreate(source.DownloadUrl, UriKind.Absolute, out var download)
            || download.Scheme != Uri.UriSchemeHttps
            || download.Port != 443
            || !string.IsNullOrEmpty(download.UserInfo)
            || !string.IsNullOrEmpty(download.Query)
            || !string.IsNullOrEmpty(download.Fragment)
            || !download.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || !download.Host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
            || !download.AbsolutePath.Equals(
                $"/opticon/releases/{source.Version}/{Uri.EscapeDataString(source.File)}",
                StringComparison.Ordinal))
            throw new InvalidDataException("The source-only remote-update manifest has an unsafe immutable CloudFront URL.");
        return download;
    }

    private static string ResolveArchitecture(string value)
    {
        var architecture = string.IsNullOrWhiteSpace(value) ? "x64" : value.ToLowerInvariant();
        return architecture is "x64" or "arm64"
            ? architecture
            : throw new InvalidDataException("The selected device has an unsupported source-update architecture.");
    }

    private static Version ParseInstalledGuardianVersion(string value)
    {
        try { return UpdatePackageVerifier.ParseVersion(value); }
        catch (InvalidDataException) { return new Version(0, 0, 0); }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Compressed Opticon release metadata is not accepted.");
        if (response.Content.Headers.ContentLength is long declared && (declared <= 0 || declared > maximum))
            throw new InvalidDataException("Opticon release metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximum)
                throw new InvalidDataException("Opticon release metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new InvalidDataException("Opticon release metadata is empty.");
        return output.ToArray();
    }
}
