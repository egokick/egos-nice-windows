using System.Net.Http.Headers;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record OpticonSourceRelease(
    string Version,
    string File,
    long Size,
    string Sha256,
    string SdkVersion,
    string RuntimeVersion,
    string SourceManifestSha256,
    string SourceManifestKeyId,
    string SigningProfile,
    string ProductSignerThumbprint,
    IReadOnlyList<string> TargetRuntimes,
    Uri DownloadUri,
    string BootstrapVersion,
    string BootstrapFile,
    long BootstrapSize,
    string BootstrapSha256,
    string BootstrapSignerThumbprint,
    Uri BootstrapDownloadUri);

public sealed class OpticonSourceReleaseClient
{
    public const string SupportedSdkVersion = "10.0.302";
    public const string SupportedRuntimeVersion = "10.0.10";
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(45));

    public async Task<OpticonSourceRelease> GetCurrentAsync(
        AdminConfig config,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        if (!Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var control)
            || control.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Opticon HTTPS control origin is not configured.");

        var current = typeof(OpticonSourceReleaseClient).Assembly.GetName().Version
                      ?? throw new InvalidOperationException("The command-center release version is unavailable.");
        var currentVersion = $"{current.Major}.{current.Minor}.{current.Build}";
        var manifestUri = new Uri(control, "/opticon/artifacts/v1/manifest.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifestBytes = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ArtifactManifestDto>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release server returned an empty manifest.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("The Opticon release manifest schema is unsupported.");

        var matches = manifest.Artifacts.Where(item =>
                item.Product.Equals("OpticonSource", StringComparison.Ordinal)
                && item.Version.Equals(currentVersion, StringComparison.Ordinal)
                && item.Architecture.Equals("source", StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"The command center can issue invitations only after exact source release {currentVersion} is published.");
        var source = matches[0];
        var bootstrapMatches = manifest.Artifacts.Where(item =>
                item.Product.Equals("OpticonBootstrap", StringComparison.Ordinal)
                && item.Version.Equals(currentVersion, StringComparison.Ordinal)
                && item.Architecture.Equals("x64", StringComparison.Ordinal))
            .ToArray();
        if (bootstrapMatches.Length != 1)
            throw new InvalidOperationException(
                $"The command center can issue invitations only after exact bootstrap release {currentVersion} is published.");
        var bootstrap = bootstrapMatches[0];
        if (source.Size is < 1024 or > 256L * 1024 * 1024
            || source.Sha256.Length != 64 || source.Sha256.Any(character => !Uri.IsHexDigit(character))
            || !source.File.Equals($"opticon-source-{currentVersion}.zip", StringComparison.Ordinal)
            || source.SdkVersion != SupportedSdkVersion || source.RuntimeVersion != SupportedRuntimeVersion
            || source.SourceManifestSha256.Length != 64 || source.SourceManifestSha256.Any(character => !Uri.IsHexDigit(character))
            || source.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || source.SigningProfile != OpticonSigningProfile.Production.ToString()
            || source.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || source.SourceManifestKeyId == InvitationSigning.CertificateThumbprint
            || source.ProductSignerThumbprint == InvitationSigning.CertificateThumbprint
            || source.SourceManifestKeyId == source.ProductSignerThumbprint
            || !source.TargetRuntimes.SequenceEqual(["win-x64", "win-arm64"], StringComparer.Ordinal))
            throw new InvalidDataException("The exact source release has invalid immutable build metadata.");
        if (bootstrap.Size is < 1024 or > 128L * 1024 * 1024
            || bootstrap.Sha256.Length != 64 || bootstrap.Sha256.Any(character => !Uri.IsHexDigit(character))
            || !bootstrap.SignerThumbprint.Equals(
                ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
            || bootstrap.SigningProfile != OpticonSigningProfile.Production.ToString()
            || bootstrap.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || bootstrap.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || bootstrap.File != $"opticon-bootstrap-{currentVersion}.exe")
            throw new InvalidDataException("The exact bootstrap release has invalid immutable publisher metadata.");

        var download = RequireCloudFrontDownload(source, currentVersion);
        var bootstrapDownload = RequireCloudFrontDownload(bootstrap, currentVersion);

        return new OpticonSourceRelease(currentVersion, source.File, source.Size,
            source.Sha256.ToLowerInvariant(), source.SdkVersion, source.RuntimeVersion,
            source.SourceManifestSha256.ToLowerInvariant(), source.SourceManifestKeyId,
            source.SigningProfile, source.ProductSignerThumbprint, source.TargetRuntimes,
            download, currentVersion, bootstrap.File, bootstrap.Size, bootstrap.Sha256.ToLowerInvariant(),
            bootstrap.SignerThumbprint.ToUpperInvariant(), bootstrapDownload);
    }

    private static Uri RequireCloudFrontDownload(ArtifactRecordDto artifact, string version)
    {
        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var download)
            || download.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(download.UserInfo)
            || !string.IsNullOrEmpty(download.Query) || !string.IsNullOrEmpty(download.Fragment)
            || !download.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || download.AbsolutePath != $"/opticon/releases/{version}/{Uri.EscapeDataString(artifact.File)}")
            throw new InvalidDataException("The exact release has an unsafe immutable CloudFront URL.");
        return download;
    }

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
