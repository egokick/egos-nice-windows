using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class HeadscaleDeviceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public string DnsName { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public bool Online { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string[] Tags { get; init; } = [];
    public string UserId { get; init; } = string.Empty;
}

public sealed record CreatedPreAuthKey(string Id, string Key);

// Uses only the self-hosted Headscale REST API. The API key is kept locally
// with Windows DPAPI and never leaves this command center except to Headscale.
public sealed class HeadscaleApiClient
{
    private readonly AdminState _state;
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(30));

    public HeadscaleApiClient(AdminState state)
    {
        _state = state;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Taildesk-Admin/1.0");
    }

    public async Task TestAsync(CancellationToken cancellationToken = default) =>
        _ = await GetDevicesAsync(cancellationToken);

    public async Task<IReadOnlyList<HeadscaleDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/node", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var nodes = document.RootElement.TryGetProperty("nodes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();
        return nodes.Select(ToDevice).ToArray();
    }

    public async Task<CreatedPreAuthKey> CreateInviteKeyAsync(
        DeviceRole role,
        bool exitNode,
        string description,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var expiration = expiresAt ?? InvitationPolicy.CreateDefaultExpiry();
        if (expiration <= DateTimeOffset.UtcNow || expiration > DateTimeOffset.UtcNow.AddDays(InvitationPolicy.MaximumLifetimeDays + 1))
            throw new ArgumentOutOfRangeException(nameof(expiresAt), $"Invitation expiry must be within {InvitationPolicy.MaximumLifetimeDays} days.");
        var tags = new List<string> { role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed" };
        if (exitNode) tags.Add("tag:taildesk-exit");
        var body = new { user = _state.Config.HeadscaleUserId, reusable = false, ephemeral = false, expiration = expiration.UtcDateTime, aclTags = tags };
        using var response = await SendAsync(HttpMethod.Post, "api/v1/preauthkey", body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var key = document.RootElement.TryGetProperty("preAuthKey", out var value) ? value : document.RootElement;
        var id = Scalar(key, "id");
        var secret = String(key, "key");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException("Headscale returned incomplete pre-authentication key metadata.");
        return new CreatedPreAuthKey(id, secret);
    }

    public async Task RevokeKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return;
        using var response = await SendAsync(HttpMethod.Post, "api/v1/preauthkey/expire", new { id = keyId }, cancellationToken);
        if ((int)response.StatusCode is 404 or 410) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A Headscale node ID is required.", nameof(deviceId));
        using var response = await SendAsync(HttpMethod.Delete, $"api/v1/node/{Uri.EscapeDataString(deviceId)}", null, cancellationToken);
        if ((int)response.StatusCode is 404 or 410) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public Task SetDeviceRoleAsync(string deviceId, DeviceRole role, bool exitNode, CancellationToken cancellationToken = default)
    {
        var tags = new List<string> { role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed" };
        if (exitNode) tags.Add("tag:taildesk-exit");
        return SetTagsAsync(deviceId, tags, cancellationToken);
    }

    public async Task SetTagsAsync(string nodeId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/v1/node/{Uri.EscapeDataString(nodeId)}/tags", new { tags }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ApproveExitNodeRoutesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/v1/node/{Uri.EscapeDataString(deviceId)}/approve_routes", new { routes = HeadscaleRoutes.ExitNode }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var baseAddress = ParseApiAddress();
        var request = new HttpRequestMessage(method, new Uri(baseAddress, path));
        var bodyBytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, JsonDefaults.Options);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = SecurityHelpers.CreateToken(18);
        var bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var canonical = $"{method.Method}\n{request.RequestUri!.PathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
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
        return await _http.SendAsync(request, cancellationToken);
    }

    private Uri ParseApiAddress()
    {
        if (!Uri.TryCreate(_state.Config.HeadscaleApiUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("Enter your self-hosted Headscale API HTTPS address in Settings.");
        if (string.IsNullOrWhiteSpace(_state.Config.HeadscaleApiKeyProtected)) throw new InvalidOperationException("Enter the Opticon admin signing secret in Settings first.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static HeadscaleDeviceInfo ToDevice(JsonElement node)
    {
        var addresses = Array(node, "ipAddresses");
        var hostInfo = node.TryGetProperty("hostinfo", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
        var name = String(node, "givenName");
        if (string.IsNullOrWhiteSpace(name)) name = String(node, "name");
        var user = node.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object ? userElement : default;
        return new HeadscaleDeviceInfo { Id = Scalar(node, "id"), Name = name, HostName = String(node, "name"), DnsName = String(node, "givenName"), Ip = addresses.FirstOrDefault(address => address.Contains('.')) ?? string.Empty, OperatingSystem = hostInfo.ValueKind == JsonValueKind.Object ? String(hostInfo, "os") : string.Empty, Online = Bool(node, "online"), LastSeen = Date(node, "lastSeen"), Tags = Array(node, "tags"), UserId = user.ValueKind == JsonValueKind.Object ? Scalar(user, "id") : string.Empty };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Headscale API returned {(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 800)]}");
    }

    private static string String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string Scalar(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : string.Empty : string.Empty;
    private static bool Bool(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static string[] Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray() : [];
    private static DateTimeOffset? Date(JsonElement element, string property) => DateTimeOffset.TryParse(String(element, property), out var date) ? date : null;
}
