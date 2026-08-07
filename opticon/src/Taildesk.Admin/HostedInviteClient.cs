using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record HostedInvitePublication(string IdHash, string Url);
internal sealed record HostedInviteUpload(string DeviceName, string Role, DateTimeOffset ExpiresAt, byte[] Ciphertext);

public sealed class HostedInviteClient
{
    private const string InviteAdminPath = "/opticon/v1/invitations/";
    private readonly AdminState _state;
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(30));

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
        CancellationToken cancellationToken = default)
    {
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicId))).ToLowerInvariant();
        var upload = new HostedInviteUpload(payload.DeviceName, payload.Role.ToString(), payload.ExpiresAt, encryptedEnvelope);
        var body = JsonSerializer.SerializeToUtf8Bytes(upload, JsonDefaults.Options);
        var uri = BuildUri(InviteAdminPath + idHash);
        using var response = await SendSignedAsync(HttpMethod.Put, uri, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Fly invitation publishing failed ({(int)response.StatusCode}): {detail.Trim()}");
        }
        var origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        var url = new Uri(origin, $"/opticon/i/{Uri.EscapeDataString(publicId)}").AbsoluteUri + "#" + fragmentKey;
        return new HostedInvitePublication(idHash, url);
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

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, Uri uri, byte[] body, CancellationToken cancellationToken)
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
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
