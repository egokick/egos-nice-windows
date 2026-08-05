using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class TailscaleDeviceInfo
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
}

public sealed record CreatedAuthKey(string Id, string Key);

public sealed class TailscaleApiClient
{
    private const string ApiBase = "https://api.tailscale.com/api/v2/";
    private readonly AdminState _state;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string _accessToken = string.Empty;
    private DateTimeOffset _accessTokenExpiresAt;

    public TailscaleApiClient(AdminState state)
    {
        _state = state;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Taildesk-Admin/1.0");
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetDevicesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TailscaleDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"tailnet/{Tailnet()}/devices?fields=all", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var devices = new List<TailscaleDeviceInfo>();
        foreach (var item in document.RootElement.GetProperty("devices").EnumerateArray())
        {
            var addresses = TryArray(item, "addresses");
            var name = TryString(item, "name").TrimEnd('.');
            devices.Add(new TailscaleDeviceInfo
            {
                Id = TryString(item, "nodeId") is { Length: > 0 } nodeId ? nodeId : TryString(item, "id"),
                Name = name.Split('.').FirstOrDefault() ?? name,
                HostName = TryString(item, "hostname"),
                DnsName = name,
                Ip = addresses.FirstOrDefault(address => address.Contains('.')) ?? addresses.FirstOrDefault() ?? string.Empty,
                OperatingSystem = TryString(item, "os"),
                Online = TryBool(item, "online"),
                LastSeen = TryDate(item, "lastSeen"),
                Tags = TryArray(item, "tags")
            });
        }
        return devices;
    }

    public async Task<CreatedAuthKey> CreateInviteKeyAsync(DeviceRole role, bool exitNode, string description, CancellationToken cancellationToken = default)
    {
        var tags = new List<string>
        {
            role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed"
        };
        if (exitNode)
        {
            tags.Add("tag:taildesk-exit");
        }

        var body = new
        {
            capabilities = new
            {
                devices = new
                {
                    create = new
                    {
                        reusable = false,
                        ephemeral = false,
                        preauthorized = true,
                        tags
                    }
                }
            },
            expirySeconds = 900,
            description
        };
        using var response = await SendAsync(HttpMethod.Post, $"tailnet/{Tailnet()}/keys", body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var id = TryString(document.RootElement, "id");
        var key = TryString(document.RootElement, "key");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidDataException("Tailscale created an invitation key but returned incomplete key metadata.");
        }
        return new CreatedAuthKey(id, key);
    }

    public async Task RevokeKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return;
        using var response = await SendAsync(HttpMethod.Delete, $"tailnet/{Tailnet()}/keys/{Uri.EscapeDataString(keyId)}", null, cancellationToken);
        // Single-use keys disappear after successful use; that is the desired
        // end state and is equivalent to a successful explicit revocation.
        if ((int)response.StatusCode is 404 or 410) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A Tailscale device ID is required.", nameof(deviceId));
        }

        using var response = await SendAsync(
            HttpMethod.Delete,
            $"device/{Uri.EscapeDataString(deviceId)}",
            null,
            cancellationToken);

        // A missing device is already in the requested revoked state. Returning
        // false lets the command center describe the local cleanup accurately.
        if ((int)response.StatusCode is 404 or 410) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task SetDeviceRoleAsync(string deviceId, DeviceRole role, bool exitNode, CancellationToken cancellationToken = default)
    {
        var tags = new List<string>
        {
            role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed"
        };
        if (exitNode) tags.Add("tag:taildesk-exit");
        using var response = await SendAsync(HttpMethod.Post, $"device/{Uri.EscapeDataString(deviceId)}/tags", new { tags }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ApproveAdvertisedRoutesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var get = await SendAsync(HttpMethod.Get, $"device/{Uri.EscapeDataString(deviceId)}/routes", null, cancellationToken);
        await EnsureSuccessAsync(get, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var routes = TryArray(document.RootElement, "advertisedRoutes");
        using var post = await SendAsync(HttpMethod.Post, $"device/{Uri.EscapeDataString(deviceId)}/routes", new { routes }, cancellationToken);
        await EnsureSuccessAsync(post, cancellationToken);
    }

    public async Task ApplyPolicyAsync(string hujson, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ApiBase), $"tailnet/{Tailnet()}/acl"))
        {
            Content = new StringContent(hujson, Encoding.UTF8, "application/hujson")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(ApiBase), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
        }
        return await _http.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return _accessToken;
        }

        if (string.IsNullOrWhiteSpace(_state.Config.OAuthClientId) || string.IsNullOrWhiteSpace(_state.Config.OAuthClientSecretProtected))
        {
            throw new InvalidOperationException("Configure the Tailscale OAuth client in Settings first.");
        }
        var secret = SecretProtector.Unprotect(_state.Config.OAuthClientSecretProtected);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _state.Config.OAuthClientId,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials"
        });
        using var response = await _http.PostAsync(new Uri(new Uri(ApiBase), "oauth/token"), content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        _accessToken = TryString(document.RootElement, "access_token");
        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return _accessToken;
    }

    private string Tailnet() => Uri.EscapeDataString(string.IsNullOrWhiteSpace(_state.Config.Tailnet) ? "-" : _state.Config.Tailnet);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 800) detail = detail[..800];
        throw new InvalidOperationException($"Tailscale API returned {(int)response.StatusCode}: {detail}");
    }

    private static string TryString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool TryBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string[] TryArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : [];

    private static DateTimeOffset? TryDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
}
