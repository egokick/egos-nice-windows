using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class EnrollmentWorker : BackgroundService
{
    private readonly AgentState _state;
    private readonly TailscaleCli _tailscale;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<EnrollmentWorker> _logger;

    public EnrollmentWorker(AgentState state, TailscaleCli tailscale, IHttpClientFactory clients, ILogger<EnrollmentWorker> logger)
    {
        _state = state;
        _tailscale = tailscale;
        _clients = clients;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _state.Config.PendingInviteId.HasValue)
        {
            try
            {
                await EnrollAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Enrollment has not completed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task EnrollAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _tailscale.GetStatusAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshot.Ip))
        {
            throw new InvalidOperationException("Tailscale is not connected yet.");
        }

        var request = new EnrollmentRequest
        {
            InviteId = _state.Config.PendingInviteId!.Value,
            InviteSecret = _state.Config.PendingInviteSecret,
            TailnetDeviceId = snapshot.DeviceId,
            HostName = Environment.MachineName,
            DnsName = snapshot.DnsName,
            TailscaleIp = snapshot.Ip,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"
        };

        var endpoint = new Uri(new Uri(_state.Config.CoordinatorUrl.TrimEnd('/') + "/"), "api/v1/enroll");
        var response = await _clients.CreateClient(nameof(EnrollmentWorker)).PostAsJsonAsync(endpoint, request, JsonDefaults.Options, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonDefaults.Options, cancellationToken);
        if (!response.IsSuccessStatusCode || result?.Accepted != true)
        {
            throw new InvalidOperationException(result?.Message ?? $"Enrollment returned HTTP {(int)response.StatusCode}.");
        }

        await _state.MarkEnrolledAsync(cancellationToken);
        _logger.LogInformation("Enrollment completed with {Coordinator}.", _state.Config.CoordinatorUrl);
    }
}
