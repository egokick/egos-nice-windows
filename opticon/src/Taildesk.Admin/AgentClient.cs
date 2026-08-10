using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class AgentClient
{
    // Agents through 1.1.9 use ASP.NET's default JSON enum binding, which
    // accepts numeric enum values. Keep request serialization compatible with
    // those installed agents while response parsing remains more permissive.
    private static readonly JsonSerializerOptions AgentRequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<DeviceStatusDto> GetStatusAsync(DeviceRecord device, string token, CancellationToken cancellationToken = default) =>
        await GetAsync<DeviceStatusDto>(device, token, "api/v1/status", cancellationToken);

    public async Task<IReadOnlyList<RootDto>> GetRootsAsync(DeviceRecord device, string token, CancellationToken cancellationToken = default) =>
        await GetAsync<List<RootDto>>(device, token, "api/v1/roots", cancellationToken);

    public async Task<FileListingDto> GetFilesAsync(DeviceRecord device, string token, string root, string path, CancellationToken cancellationToken = default) =>
        await GetAsync<FileListingDto>(device, token,
            $"api/v1/files?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}", cancellationToken);

    public Task<FileTransferDigest> DownloadAsync(
        DeviceRecord device,
        string token,
        string root,
        string remotePath,
        string localPath,
        IProgress<(long Current, long Total)>? progress,
        CancellationToken cancellationToken = default,
        bool overwrite = true)
    {
        var fullPath = Path.GetFullPath(localPath);
        return DownloadToRootAsync(
            device, token, root, remotePath,
            Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The download destination has no parent directory."),
            Path.GetFileName(fullPath), progress, cancellationToken, overwrite);
    }

    public async Task<FileTransferDigest> DownloadToRootAsync(
        DeviceRecord device,
        string token,
        string root,
        string remotePath,
        string localRoot,
        string localRelativePath,
        IProgress<(long Current, long Total)>? progress,
        CancellationToken cancellationToken = default,
        bool overwrite = true)
    {
        // The current Agent download contract has no ETag or other strong content
        // validator. Restart into a fresh, handle-created temporary file instead of
        // combining an unauthenticated stale prefix with a new response.
        using var target = GuardedLocalTransferTarget.Create(localRoot, localRelativePath);
        using var request = CreateRequest(device, token, HttpMethod.Get,
            $"api/v1/files/download?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(remotePath)}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
            throw new InvalidDataException("The Agent returned an unexpected partial response to a full-file download.");

        var expectedLength = response.Content.Headers.ContentLength;
        progress?.Report((0, expectedLength ?? 0));
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        FileTransferDigest digest;
        await using (var output = target.OpenWriteStream())
        {
            digest = await CopyWithDigestAsync(input, output, expectedLength ?? 0, progress, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        if (expectedLength.HasValue && digest.Length != expectedLength.Value)
            throw new IOException("The download ended before the complete remote file was received.");
        target.Promote(overwrite);
        return digest;
    }

    public async Task<FileTransferDigest> GetRemoteFileDigestAsync(
        DeviceRecord device,
        string token,
        string root,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(device, token, HttpMethod.Get,
            $"api/v1/files/download?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(remotePath)}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
            throw new InvalidDataException("The Agent returned an unexpected partial response while verifying a file.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var digest = await ReadDigestAsync(input, cancellationToken);
        if (response.Content.Headers.ContentLength is long expectedLength && digest.Length != expectedLength)
            throw new IOException("The remote file changed or ended while it was being verified.");
        return digest;
    }

    public async Task UploadAsync(DeviceRecord device, string token, Guid transferId, string localPath, string root, string destinationDirectory,
        bool overwrite, IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await UploadStreamAsync(device, token, transferId, file, Path.GetFileName(localPath), file.Length,
            root, destinationDirectory, overwrite, progress, cancellationToken);
    }

    public async Task UploadStreamAsync(
        DeviceRecord device,
        string token,
        Guid transferId,
        Stream file,
        string fileName,
        long totalLength,
        string root,
        string destinationDirectory,
        bool overwrite,
        IProgress<(long Current, long Total)>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!file.CanRead || !file.CanSeek || totalLength < 0 || file.Length != totalLength)
            throw new InvalidDataException("The guarded upload source has an invalid length or stream type.");
        if (string.IsNullOrWhiteSpace(fileName) || !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal))
            throw new InvalidDataException("The guarded upload source has an invalid file name.");
        var common = $"root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(destinationDirectory)}&fileName={Uri.EscapeDataString(fileName)}&transferId={transferId:D}&totalLength={totalLength}&overwrite={overwrite.ToString().ToLowerInvariant()}";
        var status = await TryGetUploadStatusAsync(
            device, token, $"api/v1/files/upload-status?{common}", cancellationToken);
        if (status is null)
        {
            file.Position = 0;
            progress?.Report((0, totalLength));
            using var legacyContent = new ProgressStreamContent(file, 0, totalLength, progress, cancellationToken);
            var legacyUrl = $"api/v1/files/upload?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(destinationDirectory)}&fileName={Uri.EscapeDataString(fileName)}&overwrite={overwrite.ToString().ToLowerInvariant()}";
            using var legacyRequest = CreateRequest(device, token, HttpMethod.Post, legacyUrl);
            legacyRequest.Content = legacyContent;
            using var legacyResponse = await _http.SendAsync(
                legacyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(legacyResponse, cancellationToken);
            return;
        }
        if (status.TotalBytes != totalLength || status.BytesReceived < 0 || status.BytesReceived > totalLength)
            throw new InvalidDataException("The Agent returned an invalid resumable upload position.");
        file.Position = status.BytesReceived;
        progress?.Report((status.BytesReceived, totalLength));
        using var content = new ProgressStreamContent(file, status.BytesReceived, totalLength, progress, cancellationToken);
        var url = $"api/v1/files/upload?{common}&offset={status.BytesReceived}";
        using var request = CreateRequest(device, token, HttpMethod.Post, url);
        request.Content = content;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<UploadStatusDto?> TryGetUploadStatusAsync(
        DeviceRecord device,
        string token,
        string relative,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(device, token, HttpMethod.Get, relative);
        using var response = await _http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, timeout.Token);
        return await response.Content.ReadFromJsonAsync<UploadStatusDto>(JsonDefaults.Options, timeout.Token)
               ?? throw new InvalidDataException("The Agent returned an empty resumable upload status.");
    }

    public async Task CreateDirectoryAsync(DeviceRecord device, string token, string root, string path, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, token, HttpMethod.Post, "api/v1/files/mkdir", new CreateDirectoryRequest { Root = root, RelativePath = path }, cancellationToken);

    public async Task DeleteAsync(DeviceRecord device, string token, string root, string path, bool recursive, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(device, token, HttpMethod.Delete,
            $"api/v1/files?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}&recursive={recursive.ToString().ToLowerInvariant()}");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteIfMatchAsync(
        DeviceRecord device,
        string token,
        string root,
        string path,
        FileTransferDigest expected,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, token, HttpMethod.Post, "api/v1/files/delete-if-match",
            new ConditionalDeleteRequest
            {
                Root = root,
                RelativePath = path,
                ExpectedLength = expected.Length,
                ExpectedSha256 = expected.Sha256
            }, cancellationToken);
    public async Task<Uri> CreateMediaUriAsync(DeviceRecord device, string token, string root, string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(device, token, HttpMethod.Post, "api/v1/media-link");
        request.Content = JsonContent.Create(new MediaLinkRequest { Root = root, RelativePath = path }, options: JsonDefaults.Options);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var link = await response.Content.ReadFromJsonAsync<MediaLinkResponse>(JsonDefaults.Options, cancellationToken)
                   ?? throw new InvalidDataException("The agent returned an empty media link.");
        return ValidateMediaUri(device, link.RelativeUrl, link.ExpiresAt);
    }

    public async Task<IReadOnlyDictionary<string, Uri>> CreateMediaUrisAsync(
        DeviceRecord device,
        string token,
        string root,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        using var request = CreateRequest(device, token, HttpMethod.Post, "api/v1/media-links");
        request.Content = JsonContent.Create(new MediaLinksRequest
        {
            Root = root,
            RelativePaths = paths.ToList()
        }, options: JsonDefaults.Options);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var links = await response.Content.ReadFromJsonAsync<MediaLinksResponse>(JsonDefaults.Options, cancellationToken)
                    ?? throw new InvalidDataException("The agent returned an empty media-link batch.");
        if (links.Items.Count > paths.Count
            || links.Items.Any(item => !paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The Agent returned an unexpected media link.");
        return links.Items.ToDictionary(
            item => item.RelativePath,
            item => ValidateMediaUri(device, item.RelativeUrl, links.ExpiresAt),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static Uri ValidateMediaUri(DeviceRecord device, string relativeUrl, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl)
            || !relativeUrl.StartsWith("/api/v1/media?", StringComparison.Ordinal)
            || !Uri.TryCreate(relativeUrl, UriKind.Relative, out var relative))
            throw new InvalidDataException("The Agent returned an unsafe media link.");

        var now = DateTimeOffset.UtcNow;
        if (expiresAt < now.AddMinutes(-1) || expiresAt > now.AddMinutes(10))
            throw new InvalidDataException("The Agent returned a media link with an invalid lifetime.");

        var expected = BaseUri(device);
        var resolved = new Uri(expected, relative);
        RequireMediaOrigin(device, resolved);
        return resolved;
    }

    public async Task<byte[]> GetMediaBytesAsync(
        DeviceRecord device,
        Uri mediaUri,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > 32 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        RequireMediaOrigin(device, mediaUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, mediaUri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK
            || response.Content.Headers.ContentRange is not null
            || response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("The Agent returned an invalid or oversized media response.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(Math.Min(maximumBytes, (int)(response.Content.Headers.ContentLength ?? 0)));
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The Agent media response exceeded its allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        return output.ToArray();
    }

    private static void RequireMediaOrigin(DeviceRecord device, Uri resolved)
    {
        var expected = BaseUri(device);
        if (!resolved.IsAbsoluteUri
            || !resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !resolved.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
            || resolved.Port != expected.Port
            || !resolved.AbsolutePath.Equals("/api/v1/media", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || !string.IsNullOrEmpty(resolved.Fragment))
            throw new InvalidDataException("The Agent returned a media link outside its authenticated Tailscale origin.");
    }

    public async Task SetExitNodeAsync(DeviceRecord device, string token, bool enabled, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, token, HttpMethod.Post, "api/v1/actions/exit-node", new ExitNodeRequest { Enabled = enabled }, cancellationToken);

    public async Task RotateCredentialsAsync(
        DeviceRecord device,
        string authorizationToken,
        Guid operationId,
        string newToken,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, authorizationToken, HttpMethod.Post, "api/v1/security/rotate",
            new CredentialRotationRequest
            {
                OperationId = operationId,
                NewAgentToken = newToken,
                NewRustDeskPassword = newPassword
            }, cancellationToken);

    public async Task CommitCredentialRotationAsync(
        DeviceRecord device,
        string newToken,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, newToken, HttpMethod.Post, "api/v1/security/rotate/commit",
            new CredentialRotationCommitRequest { OperationId = operationId }, cancellationToken);

    public async Task SetRoleAsync(DeviceRecord device, string token, DeviceRole role, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, token, HttpMethod.Post, "api/v1/security/role", new RoleChangeRequest { Role = role }, cancellationToken);

    public Task<SshAccessResponse> OpenSshAsync(
        DeviceRecord device,
        string token,
        SshAccessRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<SshAccessResponse>(
            device, token, HttpMethod.Post, "api/v1/ssh/access", request, TimeSpan.FromMinutes(20), cancellationToken);

    public Task RevokeSshAsync(
        DeviceRecord device,
        string token,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(device, token, HttpMethod.Post, "api/v1/ssh/revoke",
            new SshRevokeRequest { SessionId = sessionId }, cancellationToken);

    public Task<UpdateStatusDto> PrepareUpdateAsync(
        DeviceRecord device,
        string token,
        OpticonUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<UpdateStatusDto>(
            device, token, HttpMethod.Post, "api/v1/update/prepare", request, TimeSpan.FromMinutes(30), cancellationToken);

    public Task<UpdateStatusDto> PrepareSourceUpdateAsync(
        DeviceRecord device,
        string token,
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        // The target verifies the immutable source archive and performs an
        // isolated local build before it reaches Ready, so its bounded request
        // window is intentionally longer than an executable-bundle download.
        SendJsonResultAsync<UpdateStatusDto>(
            device, token, HttpMethod.Post, "api/v1/update/source/prepare", request,
            TimeSpan.FromMinutes(60), cancellationToken);

    public Task<UpdateStatusDto> ActivateUpdateAsync(
        DeviceRecord device,
        string token,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<UpdateStatusDto>(device, token, HttpMethod.Post, "api/v1/update/activate",
            new UpdateOperationRequest { OperationId = operationId }, TimeSpan.FromMinutes(2), cancellationToken);

    public Task<UpdateStatusDto> CommitUpdateAsync(
        DeviceRecord device,
        string token,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<UpdateStatusDto>(device, token, HttpMethod.Post, "api/v1/update/commit",
            new UpdateOperationRequest { OperationId = operationId }, TimeSpan.FromMinutes(2), cancellationToken);

    public Task<UpdateStatusDto> GetUpdateStatusAsync(
        DeviceRecord device,
        string token,
        CancellationToken cancellationToken = default) =>
        GetAsync<UpdateStatusDto>(device, token, "api/v1/update/status", cancellationToken);

    public Task<GuardianMaintenanceStatusDto> ReconcileGuardianAsync(
        DeviceRecord device,
        string token,
        OpticonUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<GuardianMaintenanceStatusDto>(
            device, token, HttpMethod.Post, "api/v1/update/guardian", request,
            TimeSpan.FromMinutes(30), cancellationToken);

    public Task<GuardianMaintenanceStatusDto> ReconcileSourceGuardianAsync(
        DeviceRecord device,
        string token,
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonResultAsync<GuardianMaintenanceStatusDto>(
            device, token, HttpMethod.Post, "api/v1/update/source/guardian", request,
            TimeSpan.FromMinutes(30), cancellationToken);

    public static async Task<bool> ProbeTcpAsync(
        string tailscaleIp,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsTailscaleIp(tailscaleIp))
            throw new InvalidOperationException("Opticon refuses to probe a non-Tailscale address.");
        if (port is not 21118 && port != RemoteAdministrationProtocol.SshPort)
            throw new ArgumentOutOfRangeException(nameof(port), "Only Opticon recovery-channel ports may be probed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(System.Net.IPAddress.Parse(tailscaleIp), port, linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private async Task<T> GetAsync<T>(DeviceRecord device, string token, string relative, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(device, token, HttpMethod.Get, relative);
        using var response = await _http.SendAsync(request, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, timeout.Token)
               ?? throw new InvalidDataException("The agent returned an empty response.");
    }

    private async Task SendJsonAsync(DeviceRecord device, string token, HttpMethod method, string relative, object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = CreateRequest(device, token, method, relative);
        request.Content = JsonContent.Create(body, options: AgentRequestJsonOptions);
        using var response = await _http.SendAsync(request, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
    }

    private async Task<T> SendJsonResultAsync<T>(
        DeviceRecord device,
        string token,
        HttpMethod method,
        string relative,
        object body,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        using var request = CreateRequest(device, token, method, relative);
        request.Content = JsonContent.Create(body, options: AgentRequestJsonOptions);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, timeout.Token)
               ?? throw new InvalidDataException("The agent returned an empty response.");
    }

    private static HttpRequestMessage CreateRequest(DeviceRecord device, string token, HttpMethod method, string relative)
    {
        if (!IsTailscaleIp(device.TailscaleIp))
        {
            throw new InvalidOperationException("Opticon refuses to connect to a non-Tailscale address.");
        }
        var request = new HttpRequestMessage(method, new Uri(BaseUri(device), relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static Uri BaseUri(DeviceRecord device) => new($"http://{device.TailscaleIp}:45831/");

    public static bool IsTailscaleIp(string value) =>
        RemoteAdministrationProtocol.IsTailscaleIpv4(value);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500) body = body[..500];
        var detail = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                detail = error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Preserve a non-JSON agent response for diagnostics.
        }
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Agent returned {(int)response.StatusCode} without an error detail."
                : $"Agent returned {(int)response.StatusCode}: {detail}");
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, long initial, long total,
        IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        var current = initial;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            current += read;
            progress?.Report((current, total));
        }
    }

    private static async Task<FileTransferDigest> CopyWithDigestAsync(
        Stream source,
        Stream destination,
        long total,
        IProgress<(long Current, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long current = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            current += read;
            progress?.Report((current, total));
        }
        return new FileTransferDigest(
            current,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<FileTransferDigest> ReadDigestAsync(Stream source, CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long current = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            current += read;
        }
        return new FileTransferDigest(
            current,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly IProgress<(long Current, long Total)>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly long _initial;
        private readonly long _total;

        public ProgressStreamContent(
            Stream source,
            long initial,
            long total,
            IProgress<(long Current, long Total)>? progress,
            CancellationToken cancellationToken)
        {
            _source = source;
            _initial = initial;
            _total = total;
            _progress = progress;
            _cancellationToken = cancellationToken;
            Headers.ContentLength = source.Length - source.Position;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await CopyWithProgressAsync(_source, stream, _initial, _total, _progress, _cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _source.Length - _source.Position;
            return true;
        }
    }
}
