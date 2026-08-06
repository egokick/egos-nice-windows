using System.Net;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal sealed class InternalHealthClient : IDisposable
{
    private const int AgentPort = 45831;
    private const int MaximumResponseBytes = 64 * 1024;
    private const string HealthHeader = "X-Opticon-Update-Health";
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
        MaxResponseContentBufferSize = MaximumResponseBytes
    };
    private readonly string _token;

    public InternalHealthClient()
    {
        _token = UpdateHealthTokenStore.LoadFromAgentConfigFile();
        if (_token.Length is < 32 or > 4096 || _token.Any(char.IsControl))
            throw new InvalidDataException("The local update health token is invalid.");
    }

    public async Task<HealthCheckResult> CheckAsync(
        UpdateJournal journal,
        string expectedVersion,
        UpdatePhase expectedPhase,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!GuardianPathPolicy.TryParseTailscaleIpv4(journal.BindAddress, out var address))
                return HealthCheckResult.Unhealthy("The journaled Agent address is invalid.");
            var uri = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), AgentPort, "/internal/update-health").Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation(HealthHeader, _token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return HealthCheckResult.Unhealthy($"The protected Agent health endpoint returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return HealthCheckResult.Unhealthy("The protected Agent health response is too large.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > MaximumResponseBytes)
                    return HealthCheckResult.Unhealthy("The protected Agent health response exceeded its size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var health = JsonSerializer.Deserialize<InternalHealthResponse>(output.ToArray(), JsonDefaults.Options);
            if (health is null
                || health.DeviceId == Guid.Empty
                || health.OperationId != journal.OperationId
                || !UpdatePackageVerifier.NormalizeVersion(health.AgentVersion)
                    .Equals(UpdatePackageVerifier.NormalizeVersion(expectedVersion), StringComparison.Ordinal)
                || !string.Equals(health.UpdatePhase, expectedPhase.ToString(), StringComparison.Ordinal)
                || !health.RustDeskReady
                || !IPAddress.TryParse(health.BindAddress, out var reportedAddress)
                || !reportedAddress.Equals(address))
                return HealthCheckResult.Unhealthy("The protected Agent health identity, version, update phase, or recovery state does not match.");

            return HealthCheckResult.Healthy;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(exception.Message);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class InternalHealthResponse
    {
        public string AgentVersion { get; set; } = string.Empty;
        public Guid DeviceId { get; set; }
        public string BindAddress { get; set; } = string.Empty;
        public Guid? OperationId { get; set; }
        public string UpdatePhase { get; set; } = string.Empty;
        public bool RustDeskReady { get; set; }
    }
}

internal sealed record HealthCheckResult(bool IsHealthy, string Message)
{
    public static HealthCheckResult Healthy { get; } = new(true, string.Empty);
    public static HealthCheckResult Unhealthy(string message) => new(false, message);
}
