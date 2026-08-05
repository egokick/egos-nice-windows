using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Net.NetworkInformation;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class CoordinatorServer : IAsyncDisposable
{
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;
    private WebApplication? _app;

    public CoordinatorServer(AdminState state, HeadscaleApiClient headscale)
    {
        _state = state;
        _headscale = headscale;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Config.Mode != AdminMode.Primary || !_state.Config.SetupComplete)
        {
            return;
        }
        if (!IPAddress.TryParse(_state.Config.CoordinatorBindAddress, out var address) || !IsTailscaleAddress(address))
        {
            throw new InvalidOperationException("The coordinator must bind to its 100.64.0.0/10 Tailscale IPv4 address.");
        }

        for (var attempt = 0; attempt < 60 && !AddressIsAssigned(address); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        if (!AddressIsAssigned(address))
        {
            throw new InvalidOperationException("The coordinator's Tailscale address did not become available within two minutes.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, _state.Config.CoordinatorPort));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
        _app = builder.Build();

        _app.UseRateLimiter();
        _app.MapPost("/api/v1/enroll", EnrollAsync);
        _app.MapGet("/api/v1/registry", RegistryAsync);
        _app.MapGet("/api/v1/credentials/{deviceId:guid}", CredentialsAsync);
        await _app.StartAsync(cancellationToken);
    }

    private async Task<IResult> EnrollAsync(EnrollmentRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IsTailscaleAddress(remote) || !remote.MapToIPv4().ToString().Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new EnrollmentResponse { Message = "Enrollment must originate from the reported Tailscale address." }, statusCode: 403);
        }

        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            var invite = _state.Config.Invites.FirstOrDefault(item => item.Id == request.InviteId);
            if (invite is null || invite.RedeemedAt.HasValue || invite.IsExpired
                || !SecurityHelpers.FixedTimeEquals(invite.InviteSecretHash, SecurityHelpers.HashToken(request.InviteSecret)))
            {
                return Results.Json(new EnrollmentResponse { Message = "The invitation is invalid, expired, or already used." }, statusCode: 403);
            }

            var authoritativeNodes = await _headscale.GetDevicesAsync(cancellationToken);
            var authoritative = authoritativeNodes
                .SingleOrDefault(item => item.Id.Equals(request.TailnetDeviceId, StringComparison.Ordinal));
            var authoritativeHub = authoritativeNodes.SingleOrDefault(item =>
                item.Ip.Equals(_state.Config.CoordinatorBindAddress, StringComparison.OrdinalIgnoreCase)
                && item.Tags.Contains("tag:taildesk-hub", StringComparer.OrdinalIgnoreCase));
            var expectedRoleTag = invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed";
            var oppositeRoleTag = invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-managed" : "tag:taildesk-controller";
            var hasExpectedExitTag = authoritative?.Tags.Contains("tag:taildesk-exit", StringComparer.OrdinalIgnoreCase) == true;
            if (authoritative is null || authoritativeHub is null
                || !authoritative.Ip.Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase)
                || !authoritative.UserId.Equals(authoritativeHub.UserId, StringComparison.Ordinal)
                || !authoritative.Tags.Contains(expectedRoleTag, StringComparer.OrdinalIgnoreCase)
                || authoritative.Tags.Contains(oppositeRoleTag, StringComparer.OrdinalIgnoreCase)
                || hasExpectedExitTag != invite.AdvertiseExitNode)
            {
                return Results.Json(new EnrollmentResponse { Message = "Headscale identity, user, address, or invitation tags did not match exactly." }, statusCode: 403);
            }
            if (_state.Config.Devices.Any(item => item.TailnetDeviceId.Equals(request.TailnetDeviceId, StringComparison.Ordinal)
                                                  || item.TailscaleIp.Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(new EnrollmentResponse { Message = "That Headscale node ID or Tailscale address is already enrolled. Revoke it before creating a replacement invitation." }, statusCode: 409);
            }

            await _headscale.RevokeKeyAsync(invite.TailscaleKeyId, cancellationToken);
            var device = new DeviceRecord { Id = Guid.NewGuid() };
            device.TailnetDeviceId = request.TailnetDeviceId;
            device.Name = string.IsNullOrWhiteSpace(invite.DeviceName) ? request.HostName : invite.DeviceName;
            device.HostName = request.HostName;
            device.DnsName = request.DnsName;
            device.TailscaleIp = request.TailscaleIp;
            device.OperatingSystem = request.OperatingSystem;
            device.AgentVersion = request.AgentVersion;
            device.AgentTokenProtected = invite.AgentTokenProtected;
            device.RustDeskPasswordProtected = invite.RustDeskPasswordProtected;
            device.ControllerTokenProtected = invite.ControllerTokenProtected;
            device.Role = invite.Role;
            device.AdvertisesExitNode = invite.AdvertiseExitNode;
            device.State = DeviceConnectionState.Online;
            device.LastSeen = DateTimeOffset.UtcNow;
            if (!_state.Config.Devices.Contains(device)) _state.Config.Devices.Add(device);
            invite.RedeemedAt = DateTimeOffset.UtcNow;
            invite.ExpiresAt = invite.RedeemedAt.Value;
            invite.EnrolledDeviceId = device.Id;
            if (!string.IsNullOrWhiteSpace(invite.HostedInviteIdHash))
            {
                try
                {
                    await new HostedInviteClient(_state).DeleteAsync(invite.HostedInviteIdHash, cancellationToken);
                    invite.HostedInviteIdHash = string.Empty;
                    invite.HostedUrlProtected = string.Empty;
                }
                catch { /* Enrollment remains valid; the Fly object expires independently. */ }
            }
            await _state.SaveAsync(cancellationToken);
            return Results.Ok(new EnrollmentResponse { Accepted = true, Message = "Enrollment complete." });
        }
        finally
        {
            _state.InviteGate.Release();
        }
    }

    private async Task<IResult> RegistryAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            var controller = AuthenticateController(context, token);
            if (controller is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new RegistrySnapshot
            {
                Devices = _state.Config.Devices.Select(device => new ControllerDeviceDto
                {
                    Id = device.Id,
                    Name = device.Name,
                    HostName = device.HostName,
                    DnsName = device.DnsName,
                    TailscaleIp = device.TailscaleIp,
                    OperatingSystem = device.OperatingSystem,
                    Role = device.Role,
                    LastSeen = device.LastSeen,
                    HasAgentAccess = device.AuthorizedControllerIds.Contains(controller.Id),
                    HasRemoteAccess = device.AuthorizedControllerIds.Contains(controller.Id)
                }).ToList()
            });
        }
        finally
        {
            _state.InviteGate.Release();
        }
    }

    private async Task<IResult> CredentialsAsync(Guid deviceId, HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            var controller = AuthenticateController(context, token);
            if (controller is null) return Results.Unauthorized();
            var target = _state.Config.Devices.FirstOrDefault(device => device.Id == deviceId);
            if (target is null) return Results.NotFound();
            if (!target.AuthorizedControllerIds.Contains(controller.Id)) return Results.StatusCode(403);
            return Results.Ok(new ControllerCredentialDto
            {
                DeviceId = target.Id,
                AgentToken = SecretProtector.Unprotect(target.AgentTokenProtected),
                RustDeskPassword = SecretProtector.Unprotect(target.RustDeskPasswordProtected)
            });
        }
        finally
        {
            _state.InviteGate.Release();
        }
    }

    private DeviceRecord? AuthenticateController(HttpContext context, string token)
    {
        var controller = _state.Config.Devices.FirstOrDefault(device =>
            device.Role == DeviceRole.ControllerAndManaged
            && !string.IsNullOrWhiteSpace(device.ControllerTokenProtected)
            && SecurityHelpers.FixedTimeEquals(SecretProtector.Unprotect(device.ControllerTokenProtected), token));
        var remote = context.Connection.RemoteIpAddress;
        return controller is not null && remote is not null && IsTailscaleAddress(remote)
               && remote.MapToIPv4().ToString().Equals(controller.TailscaleIp, StringComparison.OrdinalIgnoreCase)
            ? controller : null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static bool IsTailscaleAddress(IPAddress address)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool AddressIsAssigned(IPAddress address) => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Any(item => item.Address.Equals(address));
}
