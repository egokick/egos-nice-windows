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

    public async Task DownloadAsync(DeviceRecord device, string token, string root, string remotePath, string localPath,
        IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken = default)
    {
        var temporary = localPath + ".taildesk-partial";
        var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        using var request = CreateRequest(device, token, HttpMethod.Get,
            $"api/v1/files/download?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(remotePath)}");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && offset > 0
            && response.Content.Headers.ContentRange?.Length == offset)
        {
            File.Move(temporary, localPath, true);
            progress?.Report((offset, offset));
            return;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            offset = 0;
        if (response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("The Agent returned a different download resume offset.");
        var total = response.Content.Headers.ContentRange?.Length
                    ?? (response.Content.Headers.ContentLength is long remaining ? offset + remaining : 0);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(
                         temporary,
                         offset == 0 ? FileMode.Create : FileMode.Open,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         true))
        {
            output.Position = offset;
            await CopyWithProgressAsync(input, output, offset, total, progress, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        if (total > 0 && new FileInfo(temporary).Length != total)
            throw new IOException("The download ended before the complete remote file was received.");
        File.Move(temporary, localPath, true);
    }

    public async Task UploadAsync(DeviceRecord device, string token, Guid transferId, string localPath, string root, string destinationDirectory,
        bool overwrite, IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var fileName = Path.GetFileName(localPath);
        var common = $"root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(destinationDirectory)}&fileName={Uri.EscapeDataString(fileName)}&transferId={transferId:D}&totalLength={file.Length}&overwrite={overwrite.ToString().ToLowerInvariant()}";
        var status = await TryGetUploadStatusAsync(
            device, token, $"api/v1/files/upload-status?{common}", cancellationToken);
        if (status is null)
        {
            progress?.Report((0, file.Length));
            using var legacyContent = new ProgressStreamContent(file, 0, file.Length, progress, cancellationToken);
            var legacyUrl = $"api/v1/files/upload?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(destinationDirectory)}&fileName={Uri.EscapeDataString(fileName)}&overwrite={overwrite.ToString().ToLowerInvariant()}";
            using var legacyRequest = CreateRequest(device, token, HttpMethod.Post, legacyUrl);
            legacyRequest.Content = legacyContent;
            using var legacyResponse = await _http.SendAsync(
                legacyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(legacyResponse, cancellationToken);
            return;
        }
        if (status.TotalBytes != file.Length || status.BytesReceived < 0 || status.BytesReceived > file.Length)
            throw new InvalidDataException("The Agent returned an invalid resumable upload position.");
        file.Position = status.BytesReceived;
        progress?.Report((status.BytesReceived, file.Length));
        using var content = new ProgressStreamContent(file, status.BytesReceived, file.Length, progress, cancellationToken);
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

    public async Task<Uri> CreateMediaUriAsync(DeviceRecord device, string token, string root, string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(device, token, HttpMethod.Post, "api/v1/media-link");
        request.Content = JsonContent.Create(new MediaLinkRequest { Root = root, RelativePath = path }, options: JsonDefaults.Options);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var link = await response.Content.ReadFromJsonAsync<MediaLinkResponse>(JsonDefaults.Options, cancellationToken)
                   ?? throw new InvalidDataException("The agent returned an empty media link.");
        return new Uri(BaseUri(device), link.RelativeUrl);
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
