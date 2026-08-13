using System.Net.Http.Headers;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record OpticonDeviceRelease(
    string Version, DeviceRole Role, string Architecture,
    string BundleFile, long BundleSize, string BundleSha256, Uri BundleDownloadUri,
    string BootstrapFile, long BootstrapSize, string BootstrapSha256,
    string BootstrapSignerThumbprint, string SigningProfile,
    string SourceReleaseKeyId, string ProductSignerThumbprint);

public sealed class OpticonDeviceReleaseClient
{
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(45));

    public async Task<OpticonDeviceRelease> GetCurrentAsync(
        AdminConfig config, DeviceRole role, CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        if (!Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var control)
            || control.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Opticon HTTPS control origin is not configured.");
        var assemblyVersion = typeof(OpticonDeviceReleaseClient).Assembly.GetName().Version
                              ?? throw new InvalidOperationException("The command-center release version is unavailable.");
        var version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(control, "/opticon/artifacts/v1/manifest.json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = JsonSerializer.Deserialize<ArtifactManifestDto>(
                           await ReadBoundedAsync(response, cancellationToken), JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release manifest is empty.");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("New invitations require the signed binary device-bundle manifest.");

        var bundles = manifest.Artifacts.Where(item => item.Product == "OpticonBundle"
            && item.Version == version && item.Role == role && item.Architecture == "x64").ToArray();
        var bootstraps = manifest.Artifacts.Where(item => item.Product == "OpticonBootstrap"
            && item.Version == version && item.Architecture == "x64").ToArray();
        if (bundles.Length != 1 || bootstraps.Length != 1)
            throw new InvalidOperationException(
                $"Deploy Opticon {version} before creating invitations; its device bundle and installer are not both live.");
        var bundle = bundles[0];
        var bootstrap = bootstraps[0];
        RequireTrust(bundle, version, requireSigner: false);
        RequireTrust(bootstrap, version, requireSigner: true);
        if (bundle.Size is < 1024 or > 512L * 1024 * 1024 || !IsSha256(bundle.Sha256)
            || bundle.File != $"opticon-bundle-{version}-{(role == DeviceRole.ManagedOnly ? "managed" : "controller")}-win-x64.zip"
            || bootstrap.Size is < 1024 or > 128L * 1024 * 1024 || !IsSha256(bootstrap.Sha256)
            || bootstrap.File != $"opticon-bootstrap-{version}.exe"
            || bootstrap.SignerThumbprint != ProductSigning.CertificateThumbprint)
            throw new InvalidDataException("The deployed binary release metadata is invalid.");
        var bundleUri = RequireCloudFront(bundle);
        _ = RequireCloudFront(bootstrap);
        return new OpticonDeviceRelease(version, role, "x64", bundle.File, bundle.Size,
            bundle.Sha256.ToLowerInvariant(), bundleUri, bootstrap.File, bootstrap.Size,
            bootstrap.Sha256.ToLowerInvariant(), bootstrap.SignerThumbprint,
            bundle.SigningProfile, bundle.SourceManifestKeyId, bundle.ProductSignerThumbprint);
    }

    private static void RequireTrust(ArtifactRecordDto artifact, string version, bool requireSigner)
    {
        if (artifact.Version != version || artifact.SigningProfile != BuildSigningTrust.ProfileName
            || artifact.SourceManifestKeyId != SourceReleaseSigning.KeyId
            || artifact.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || (requireSigner && artifact.SignerThumbprint != ProductSigning.CertificateThumbprint)
            || artifact.SourceManifestKeyId == InvitationSigning.CertificateThumbprint
            || artifact.ProductSignerThumbprint == InvitationSigning.CertificateThumbprint)
            throw new InvalidDataException("The deployed binary release trust metadata is invalid.");
    }

    private static Uri RequireCloudFront(ArtifactRecordDto artifact)
    {
        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != $"/opticon/releases/{artifact.Version}/{Uri.EscapeDataString(artifact.File)}")
            throw new InvalidDataException("A deployed device artifact has an unsafe immutable URL.");
        return uri;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0
            || response.Content.Headers.ContentLength is > 1024 * 1024)
            throw new InvalidDataException("The Opticon release manifest response is invalid.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > 1024 * 1024) throw new InvalidDataException("The release manifest is too large.");
            output.Write(buffer, 0, read);
        }
        return output.Length > 0 ? output.ToArray() : throw new InvalidDataException("The release manifest is empty.");
    }
}
