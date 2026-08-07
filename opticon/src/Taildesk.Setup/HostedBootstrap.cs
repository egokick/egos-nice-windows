using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal sealed record HostedBootstrap(string PublicId, string PrivateKey);

internal static class HostedBootstrapper
{
    private const string Origin = "https://taildesk-egokick-control.fly.dev";
    internal const string InvitePathEnvironmentVariable = "OPTICON_HOSTED_INVITE_PATH";
    internal const string InviteKeyEnvironmentVariable = "OPTICON_HOSTED_INVITE_KEY";
    private static readonly Regex NamePattern = new(
        "^Install-Opticon-(?<id>[A-Za-z0-9_-]{24,128})--(?<key>[A-Za-z0-9_-]{32,128})$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PublishedNamePattern = new(
        "^opticon-bootstrap-[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool TryParse(string? executablePath, out HostedBootstrap bootstrap)
    {
        bootstrap = default!;
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty);
        if (!match.Success) return false;
        bootstrap = new HostedBootstrap(match.Groups["id"].Value, match.Groups["key"].Value);
        return true;
    }

    internal static bool IsPublishedBootstrap(string? executablePath) =>
        string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
        && PublishedNamePattern.IsMatch(Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty);

    internal static async Task LaunchSetupAsync(HostedBootstrap bootstrap, Action<string> report)
    {
        var bootstrapPath = Environment.ProcessPath
                            ?? throw new InvalidOperationException("The signed Opticon bootstrap executable path is unavailable.");
        await InvitationSigning.VerifyAuthenticodeAsync(bootstrapPath);
        var directory = Path.Combine(Path.GetTempPath(), "Opticon-" + bootstrap.PublicId[..12]);
        Directory.CreateDirectory(directory);
        var invitePath = Path.Combine(directory, "invite.tdinvite");
        var bundlePath = Path.Combine(directory, "opticon-bundle.zip");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        report("Downloading the encrypted Opticon invitation…");
        await DownloadAsync(client, $"{Origin}/opticon/i/{Uri.EscapeDataString(bootstrap.PublicId)}/invite.tdinvite", invitePath, null, null);
        var encryptedInvite = await File.ReadAllBytesAsync(invitePath);
        InvitePayload invite;
        var signedEnvelope = HostedInviteFile.Decrypt(bootstrap.PrivateKey, encryptedInvite);
        try { invite = HostedInviteFile.ReadSigned(signedEnvelope); }
        finally { CryptographicOperations.ZeroMemory(signedEnvelope); }

        report("Finding the signed Opticon release for this device…");
        var manifestResponse = await client.GetAsync($"{Origin}/opticon/artifacts/v1/manifest.json");
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<ArtifactManifestDto>(manifestStream, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The Opticon release manifest is empty.");
        var bundle = manifest.Artifacts
            .Where(item => item.Product == "OpticonBundle" && item.Role == invite.Role && item.Architecture == "x64")
            .OrderByDescending(item => Version.Parse(item.Version))
            .FirstOrDefault()
                     ?? throw new InvalidDataException("No signed Opticon release is available for this invitation.");
        if (bundle.Size <= 0 || !Regex.IsMatch(bundle.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The Opticon release manifest contains an invalid bundle record.");

        report("Downloading and verifying the signed Opticon release…");
        await DownloadAsync(client, ResolveBundleUrl(bundle), bundlePath, bundle.Size, bundle.Sha256);
        var releaseDirectory = Path.Combine(directory, "release");
        if (Directory.Exists(releaseDirectory)) Directory.Delete(releaseDirectory, true);
        ZipFile.ExtractToDirectory(bundlePath, releaseDirectory);
        var setup = Path.Combine(releaseDirectory, "Taildesk.Setup.exe");
        if (!File.Exists(setup)) throw new InvalidDataException("The signed Opticon release does not contain Setup.");
        await InvitationSigning.VerifyAuthenticodeAsync(setup);

        report("Verified. Starting signed Opticon Setup…");
        var start = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = releaseDirectory };
        start.ArgumentList.Add("--hosted-invite=" + invitePath);
        start.ArgumentList.Add("--invite-key=" + bootstrap.PrivateKey);
        start.Environment[InvitePathEnvironmentVariable] = invitePath;
        start.Environment[InviteKeyEnvironmentVariable] = bootstrap.PrivateKey;
        if (Process.Start(start) is null) throw new InvalidOperationException("Signed Opticon Setup could not be started.");
    }

    private static string ResolveBundleUrl(ArtifactRecordDto bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.DownloadUrl))
            return $"{Origin}/opticon/artifacts/v1/{Uri.EscapeDataString(bundle.File)}";
        if (!Uri.TryCreate(bundle.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.IsDefaultPort is false
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host[..^".cloudfront.net".Length].Length == 0
            || uri.AbsolutePath != $"/opticon/releases/{Uri.EscapeDataString(bundle.Version)}/{Uri.EscapeDataString(bundle.File)}")
            throw new InvalidDataException("The Opticon release manifest contains an unsafe CloudFront download URL.");
        return uri.AbsoluteUri;
    }

    private static async Task DownloadAsync(HttpClient client, string url, string destination, long? expectedSize, string? expectedHash)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (expectedSize.HasValue && response.Content.Headers.ContentLength is long declared && declared != expectedSize.Value)
            throw new InvalidDataException("The Opticon download size did not match its signed release record.");
        await using var source = await response.Content.ReadAsStreamAsync();
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            await source.CopyToAsync(output);
        if (expectedSize.HasValue && new FileInfo(destination).Length != expectedSize.Value)
            throw new InvalidDataException("The Opticon download is incomplete.");
        if (expectedHash is not null)
        {
            await using var input = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Opticon download did not match its signed release record.");
        }
    }
}
