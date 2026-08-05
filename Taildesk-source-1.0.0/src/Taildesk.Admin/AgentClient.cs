using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class AgentClient
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

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
        using var request = CreateRequest(device, token, HttpMethod.Get,
            $"api/v1/files/download?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(remotePath)}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var temporary = localPath + ".taildesk-partial";
        try
        {
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await CopyWithProgressAsync(input, output, total, progress, cancellationToken);
            File.Move(temporary, localPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public async Task UploadAsync(DeviceRecord device, string token, string localPath, string root, string destinationDirectory,
        bool overwrite, IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var content = new ProgressStreamContent(file, progress, cancellationToken);
        var url = $"api/v1/files/upload?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(destinationDirectory)}&fileName={Uri.EscapeDataString(Path.GetFileName(localPath))}&overwrite={overwrite.ToString().ToLowerInvariant()}";
        using var request = CreateRequest(device, token, HttpMethod.Post, url);
        request.Content = content;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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

    public async Task RotateCredentialsAsync(DeviceRecord device, string oldToken, string newToken, string newPassword, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, oldToken, HttpMethod.Post, "api/v1/security/rotate",
            new CredentialRotationRequest { NewAgentToken = newToken, NewRustDeskPassword = newPassword }, cancellationToken);

    public async Task SetRoleAsync(DeviceRecord device, string token, DeviceRole role, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(device, token, HttpMethod.Post, "api/v1/security/role", new RoleChangeRequest { Role = role }, cancellationToken);

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
        request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
        using var response = await _http.SendAsync(request, timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
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

    public static bool IsTailscaleIp(string value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address)) return false;
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500) body = body[..500];
        throw new InvalidOperationException($"Agent returned {(int)response.StatusCode}: {body}");
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, long total,
        IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long current = 0;
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

        public ProgressStreamContent(Stream source, IProgress<(long Current, long Total)>? progress, CancellationToken cancellationToken)
        {
            _source = source;
            _progress = progress;
            _cancellationToken = cancellationToken;
            Headers.ContentLength = source.Length;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await CopyWithProgressAsync(_source, stream, _source.Length, _progress, _cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _source.Length;
            return true;
        }
    }
}
