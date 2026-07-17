using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using StayActive.EnrollmentBroker.Security;

namespace StayActive.EnrollmentBroker.Services;

/// <summary>
/// Headscale v0.29 REST client.  The versioned API exposes
/// POST/GET /api/v1/preauthkey and POST /api/v1/preauthkey/expire.  The
/// service API key is used only as an Authorization Bearer value and is never
/// included in exceptions or logs.
/// </summary>
public sealed class HeadscaleV029Client : IHeadscaleV029Client, IDisposable
{
    private const string PreAuthKeyPath = "api/v1/preauthkey";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
    private bool _disposed;

    public HeadscaleV029Client(HttpClient httpClient, IControllerCredentialStore credentialStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null)
        {
            throw new ArgumentException("The Headscale HTTP client must have a base address.", nameof(httpClient));
        }

        ArgumentNullException.ThrowIfNull(credentialStore);
        _apiKey = ControllerCredential.Validate(
            credentialStore.ReadGenericCredential(ControllerCredential.TargetName));
    }

    public async Task<HeadscaleCreatedPreAuthKey> CreateOneUsePreAuthKeyAsync(
        string userId,
        DateTimeOffset expirationUtc,
        IReadOnlyList<string> aclTags,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(aclTags);

        var requestBody = new CreatePreAuthKeyRequest(
            userId,
            Reusable: false,
            Ephemeral: false,
            Expiration: expirationUtc.UtcDateTime,
            AclTags: aclTags.ToArray());
        using var request = CreateRequest(HttpMethod.Post, PreAuthKeyPath);
        request.Content = JsonContent.Create(requestBody, options: _jsonOptions);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "create pre-authentication key");

        CreatePreAuthKeyResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<CreatePreAuthKeyResponse>(_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new HeadscaleProtocolException("Headscale returned an invalid pre-authentication key response.");
        }

        var key = payload?.PreAuthKey;
        if (key is null
            || !IsNumericIdentifier(key.Id)
            || string.IsNullOrWhiteSpace(key.Key)
            || key.AclTags is null)
        {
            throw new HeadscaleProtocolException("Headscale returned an incomplete pre-authentication key response.");
        }

        return new HeadscaleCreatedPreAuthKey(
            key.Id!,
            key.Key!,
            key.Reusable,
            key.Ephemeral,
            key.Used,
            key.Expiration,
            key.AclTags.ToArray());
    }

    public async Task<HeadscalePreAuthKeyStatus?> GetPreAuthKeyStatusAsync(
        string keyId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!IsNumericIdentifier(keyId))
        {
            throw new ArgumentException("A Headscale numeric pre-authentication key id is required.", nameof(keyId));
        }

        using var request = CreateRequest(HttpMethod.Get, PreAuthKeyPath);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "list pre-authentication keys");

        ListPreAuthKeysResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<ListPreAuthKeysResponse>(_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new HeadscaleProtocolException("Headscale returned an invalid pre-authentication key list.");
        }

        var key = payload?.PreAuthKeys?.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, keyId, StringComparison.Ordinal));
        if (key is null)
        {
            return null;
        }

        if (!IsNumericIdentifier(key.Id) || key.AclTags is null)
        {
            throw new HeadscaleProtocolException("Headscale returned an invalid pre-authentication key status.");
        }

        return new HeadscalePreAuthKeyStatus(
            key.Id!,
            key.Reusable,
            key.Ephemeral,
            key.Used,
            key.Expiration,
            key.AclTags.ToArray());
    }

    public async Task ExpirePreAuthKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!IsNumericIdentifier(keyId))
        {
            throw new ArgumentException("A Headscale numeric pre-authentication key id is required.", nameof(keyId));
        }

        using var request = CreateRequest(HttpMethod.Post, PreAuthKeyPath + "/expire");
        request.Content = JsonContent.Create(new ExpirePreAuthKeyRequest(keyId), options: _jsonOptions);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "expire pre-authentication key");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new HeadscaleUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HeadscaleUnavailableException();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Do not read the body: Headscale is entitled to include details
            // about a key, while the broker should never reflect or log them.
            throw new HeadscaleApiException(operation, (int)response.StatusCode);
        }
    }

    private static bool IsNumericIdentifier(string? value) =>
        value is { Length: > 0 and <= 32 }
        && value.All(static character => character is >= '0' and <= '9');

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CreatePreAuthKeyRequest(
        string User,
        bool Reusable,
        bool Ephemeral,
        DateTime Expiration,
        string[] AclTags);

    private sealed record ExpirePreAuthKeyRequest(string Id);

    private sealed record CreatePreAuthKeyResponse(CreatePreAuthKeyPayload? PreAuthKey);

    private sealed record ListPreAuthKeysResponse(List<PreAuthKeyStatusPayload>? PreAuthKeys);

    private sealed class CreatePreAuthKeyPayload
    {
        public string? Id { get; init; }

        public string? Key { get; init; }

        public bool Reusable { get; init; }

        public bool Ephemeral { get; init; }

        public bool Used { get; init; }

        public DateTimeOffset? Expiration { get; init; }

        public string[]? AclTags { get; init; }

        public override string ToString() =>
            $"CreatePreAuthKeyPayload {{ Id = {Id}, Key = [REDACTED], Reusable = {Reusable}, Ephemeral = {Ephemeral}, Used = {Used} }}";
    }

    // There is intentionally no Key property here. List responses can contain
    // a redacted prefix, but the broker does not deserialize or retain it.
    private sealed record PreAuthKeyStatusPayload(
        string? Id,
        bool Reusable,
        bool Ephemeral,
        bool Used,
        DateTimeOffset? Expiration,
        string[]? AclTags);
}
