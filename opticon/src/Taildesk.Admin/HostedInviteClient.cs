using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record HostedInvitePublication(string IdHash, string Url);
public sealed class HostedInvitationInventory
{
    public int SchemaVersion { get; set; }
    public List<ReleaseInvitationSummary> Invitations { get; set; } = [];
}
internal sealed record HostedInviteUpload(
    string DeviceName,
    string Role,
    DateTimeOffset ExpiresAt,
    string ReleaseVersion,
    string SourceSha256,
    string SourceFile,
    long SourceSize,
    string SourceManifestSha256,
    string SourceManifestKeyId,
    string SigningProfile,
    string ProductSignerThumbprint,
    string SdkVersion,
    string RuntimeVersion,
    string[] TargetRuntimes,
    string InstallProtocol,
    string TailscaleKeyId,
    byte[] Ciphertext);

public sealed class HostedInviteClient
{
    private const string InvitationInventoryPath = "/opticon/v1/invitations";
    private const string InviteAdminPath = "/opticon/v1/invitations/";
    private const string ReleasePreflightPath = "/opticon/v1/releases/preflight";
    private const string ReleaseAcquirePath = "/opticon/v1/releases/acquire";
    private const string ReleaseRevokeActivePath = "/opticon/v1/releases/revoke-active";
    private const string ReleaseReleasePath = "/opticon/v1/releases/release";
    private const string ReleaseFinalizePath = "/opticon/v1/releases/finalize";
    private readonly AdminState _state;
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(30));
    private readonly HttpClient _releaseHttp = DirectHttp.CreateClient(TimeSpan.FromMinutes(5));

    public HostedInviteClient(AdminState state)
    {
        _state = state;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Opticon-Admin/1.0");
    }

    public async Task<HostedInvitePublication> PublishAsync(
        InvitePayload payload,
        byte[] encryptedEnvelope,
        string publicId,
        string fragmentKey,
        string tailscaleKeyId,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        if (string.IsNullOrWhiteSpace(tailscaleKeyId))
            throw new ArgumentException("A Headscale pre-authentication key identity is required for a hosted invitation.", nameof(tailscaleKeyId));
        var idHash = ComputeIdHash(publicId);
        var upload = new HostedInviteUpload(payload.DeviceName, payload.Role.ToString(), payload.ExpiresAt,
            payload.ReleaseVersion, payload.SourceSha256, payload.SourceFile, payload.SourceSize,
            payload.SourceManifestSha256, payload.SourceManifestKeyId,
            payload.SigningProfile, payload.ProductSignerThumbprint, payload.SdkVersion,
            payload.RuntimeVersion, payload.TargetRuntimes, payload.InstallProtocol, tailscaleKeyId, encryptedEnvelope);
        var body = JsonSerializer.SerializeToUtf8Bytes(upload, JsonDefaults.Options);
        var uri = BuildUri(InviteAdminPath + idHash);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await SendSignedAsync(HttpMethod.Put, uri, body, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return BuildPublication(uri, publicId, fragmentKey, idHash);
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                if ((int)response.StatusCode < 500)
                    throw new InvalidOperationException($"Fly invitation publishing failed ({(int)response.StatusCode}): {detail.Trim()}");
                lastError = new HttpRequestException($"Fly invitation publishing failed ({(int)response.StatusCode}): {detail.Trim()}");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
        }

        // The PUT identity and body are deterministic. If every response was
        // lost, query the authenticated inventory before reporting failure so
        // an already-successful publish is committed locally instead of being
        // left as an invisible remote orphan.
        try
        {
            var inventory = await GetActiveInvitationsAsync(cancellationToken);
            var stored = inventory.FirstOrDefault(item =>
                SecurityHelpers.FixedTimeEquals(item.IdHash, idHash));
            if (stored is not null
                && string.Equals(stored.DeviceName, payload.DeviceName, StringComparison.Ordinal)
                && string.Equals(stored.Role, payload.Role.ToString(), StringComparison.Ordinal)
                && stored.ExpiresAt == payload.ExpiresAt
                && string.Equals(stored.ReleaseVersion, payload.ReleaseVersion, StringComparison.Ordinal)
                && string.Equals(stored.SourceFile, payload.SourceFile, StringComparison.Ordinal))
                return BuildPublication(uri, publicId, fragmentKey, idHash);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
        {
            lastError = exception;
        }
        throw new InvalidOperationException("Fly invitation publishing did not return a durable result after retries.", lastError);
    }

    internal static string ComputeIdHash(string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId)) throw new ArgumentException("A hosted invitation ID is required.", nameof(publicId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicId))).ToLowerInvariant();
    }

    private static HostedInvitePublication BuildPublication(Uri adminUri, string publicId, string fragmentKey, string idHash)
    {
        var origin = new Uri(adminUri.GetLeftPart(UriPartial.Authority));
        var url = new Uri(origin, $"/opticon/i/{Uri.EscapeDataString(publicId)}").AbsoluteUri + "#" + fragmentKey;
        return new HostedInvitePublication(idHash, url);
    }

    public async Task<IReadOnlyList<ReleaseInvitationSummary>> GetActiveInvitationsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendSignedAsync(HttpMethod.Get, BuildUri(InvitationInventoryPath), [], cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fly invitation inventory failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
        var content = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        var inventory = JsonSerializer.Deserialize<HostedInvitationInventory>(content, JsonDefaults.Options)
                        ?? throw new InvalidDataException("Fly invitation inventory returned an empty response.");
        if (inventory.SchemaVersion != 1 || inventory.Invitations is null)
            throw new InvalidDataException("Fly invitation inventory returned an unsupported response.");
        return inventory.Invitations;
    }

    public async Task DeleteAsync(string idHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idHash)) return;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await SendSignedAsync(HttpMethod.Delete, BuildUri(InviteAdminPath + Uri.EscapeDataString(idHash)), [], cancellationToken);
                if ((int)response.StatusCode is 404 or 410 || response.IsSuccessStatusCode) return;
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                if ((int)response.StatusCode < 500)
                    throw new InvalidOperationException($"Fly invitation removal failed ({(int)response.StatusCode}): {detail.Trim()}");
                lastError = new HttpRequestException($"Fly invitation removal failed ({(int)response.StatusCode}): {detail.Trim()}");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }
        throw new InvalidOperationException("Fly invitation removal failed after three attempts.", lastError);
    }

    public async Task<ReleaseDeploymentPreflight> GetReleasePreflightAsync(
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { targetVersion }, JsonDefaults.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, BuildUri(ReleasePreflightPath), body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fly release preflight failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
        var content = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        return JsonSerializer.Deserialize<ReleaseDeploymentPreflight>(content, JsonDefaults.Options)
               ?? throw new InvalidDataException("Fly release preflight returned an empty response.");
    }

    public async Task<ReleaseCancellationResponse> RevokeActiveReleaseInvitationsAsync(
        string targetVersion,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { targetVersion, leaseToken }, JsonDefaults.Options);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await SendSignedAsync(HttpMethod.Post, BuildUri(ReleaseRevokeActivePath), body, cancellationToken, _releaseHttp);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Fly active-invitation removal failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
                var content = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
                return JsonSerializer.Deserialize<ReleaseCancellationResponse>(content, JsonDefaults.Options)
                       ?? throw new InvalidDataException("Fly active-invitation removal returned an empty response.");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
        }
        throw new InvalidOperationException("Fly active-invitation removal did not return a durable result after retries.", lastError);
    }

    public async Task<ReleaseDeploymentLease> AcquireReleaseLeaseAsync(
        string targetVersion,
        string deploymentRevision,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { targetVersion, deploymentRevision, leaseToken }, JsonDefaults.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, BuildUri(ReleaseAcquirePath), body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fly release deployment acquisition failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
        var content = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        return JsonSerializer.Deserialize<ReleaseDeploymentLease>(content, JsonDefaults.Options)
               ?? throw new InvalidDataException("Fly release deployment acquisition returned an empty response.");
    }

    public async Task ReleaseDeploymentLeaseAsync(string leaseToken, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { leaseToken }, JsonDefaults.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, BuildUri(ReleaseReleasePath), body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fly release deployment release failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
    }

    public async Task FinalizeDeploymentLeaseAsync(
        string targetVersion,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { targetVersion, leaseToken }, JsonDefaults.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, BuildUri(ReleaseFinalizePath), body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fly release deployment finalization failed ({(int)response.StatusCode}): {await ReadDetailAsync(response, cancellationToken)}");
    }

    public async Task<byte[]> DownloadEncryptedAsync(string publicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicId)) throw new ArgumentException("A hosted invitation ID is required.", nameof(publicId));
        var uri = BuildUri($"/opticon/i/{Uri.EscapeDataString(publicId)}/invite.tdinvite");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The hosted invitation could not be read ({(int)response.StatusCode}). It may already be expired.");
        if (response.Content.Headers.ContentLength is > 65536)
            throw new InvalidDataException("The hosted invitation exceeds the allowed size.");
        var encrypted = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (encrypted.Length is < 64 or > 65536)
            throw new InvalidDataException("The hosted invitation has an invalid size.");
        return encrypted;
    }
    private Uri BuildUri(string path)
    {
        if (!Uri.TryCreate(_state.Config.HeadscaleControlUrl, UriKind.Absolute, out var control)
            || control.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The self-hosted Headscale control URL must be an HTTPS address before invitations can be published.");
        return new Uri(new Uri(control.GetLeftPart(UriPartial.Authority)), path);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        HttpMethod method,
        Uri uri,
        byte[] body,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = SecurityHelpers.CreateToken(18);
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{method.Method}\n{uri.PathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
        var secret = Encoding.UTF8.GetBytes(SecretProtector.Unprotect(_state.Config.HeadscaleApiKeyProtected));
        string signature;
        try
        {
            signature = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
        request.Headers.Add("X-Opticon-Key-Id", "primary");
        request.Headers.Add("X-Opticon-Timestamp", timestamp);
        request.Headers.Add("X-Opticon-Nonce", nonce);
        request.Headers.Add("X-Opticon-Content-SHA256", bodyHash);
        request.Headers.Add("X-Opticon-Signature", signature);
        return await (client ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Compressed Fly administrative metadata is not accepted.");
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximum))
            throw new InvalidDataException("Fly administrative metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximum)
                throw new InvalidDataException("Fly administrative metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new InvalidDataException("Fly administrative metadata is empty.");
        return output.ToArray();
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        detail = detail.Trim();
        return detail.Length <= 800 ? detail : detail[..800];
    }
}
