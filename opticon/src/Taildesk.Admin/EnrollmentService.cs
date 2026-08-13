using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record EnrollmentDecision(int StatusCode, EnrollmentResponse Response);

/// <summary>
/// The authoritative enrollment transaction shared by the production HTTP
/// coordinator and the local Docker E2E driver. Network-origin validation
/// remains in CoordinatorServer; all identity, invitation, durability, and
/// cleanup rules live here so tests cannot enroll through a parallel model.
/// </summary>
public sealed class EnrollmentService
{
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;

    public EnrollmentService(AdminState state, HeadscaleApiClient headscale)
    {
        _state = state;
        _headscale = headscale;
    }

    public async Task<EnrollmentDecision> EnrollAsync(
        EnrollmentRequest request,
        string expectedHubIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _state.InviteGate.WaitAsync(cancellationToken);
        try
        {
            var invite = _state.Config.Invites.FirstOrDefault(item => item.Id == request.InviteId);
            if (invite is null
                || !SecurityHelpers.FixedTimeEquals(invite.InviteSecretHash, SecurityHelpers.HashToken(request.InviteSecret)))
                return Decision(403, "The invitation is invalid, expired, or already used.");

            if (invite.RedeemedAt.HasValue)
            {
                var enrolled = invite.EnrolledDeviceId.HasValue
                    ? _state.Config.Devices.FirstOrDefault(item => item.Id == invite.EnrolledDeviceId.Value)
                    : null;
                return EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, enrolled, request)
                    ? Decision(200, "Enrollment was already completed.", accepted: true)
                    : Decision(403, "The invitation was already used by a different enrollment identity.");
            }
            if (invite.IsExpired)
                return Decision(403, "The invitation is invalid, expired, or already used.");

            var authoritativeNodes = await _headscale.GetDevicesAsync(cancellationToken);
            var authoritative = authoritativeNodes
                .SingleOrDefault(item => item.Id.Equals(request.TailnetDeviceId, StringComparison.Ordinal));
            var authoritativeHub = authoritativeNodes.SingleOrDefault(item =>
                item.Ip.Equals(expectedHubIp, StringComparison.OrdinalIgnoreCase)
                && item.Tags.Contains("tag:taildesk-hub", StringComparer.OrdinalIgnoreCase));
            var expectedRoleTag = invite.Role == DeviceRole.ControllerAndManaged
                ? "tag:taildesk-controller" : "tag:taildesk-managed";
            var oppositeRoleTag = invite.Role == DeviceRole.ControllerAndManaged
                ? "tag:taildesk-managed" : "tag:taildesk-controller";
            var hasExpectedExitTag = authoritative?.Tags.Contains("tag:taildesk-exit", StringComparer.OrdinalIgnoreCase) == true;
            if (authoritative is null || authoritativeHub is null
                || !authoritative.Ip.Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase)
                || !authoritative.UserId.Equals(authoritativeHub.UserId, StringComparison.Ordinal)
                || !authoritative.Tags.Contains(expectedRoleTag, StringComparer.OrdinalIgnoreCase)
                || authoritative.Tags.Contains(oppositeRoleTag, StringComparer.OrdinalIgnoreCase)
                || hasExpectedExitTag != invite.AdvertiseExitNode)
                return Decision(403, "Headscale identity, user, address, or invitation tags did not match exactly.");

            if (_state.Config.Devices.Any(item =>
                    item.TailnetDeviceId.Equals(request.TailnetDeviceId, StringComparison.Ordinal)
                    || item.TailscaleIp.Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase)))
                return Decision(409, "That Headscale node ID or Tailscale address is already enrolled. Revoke it before creating a replacement invitation.");

            var device = new DeviceRecord
            {
                Id = Guid.NewGuid(),
                TailnetDeviceId = request.TailnetDeviceId,
                Name = string.IsNullOrWhiteSpace(invite.DeviceName) ? request.HostName : invite.DeviceName,
                HostName = request.HostName,
                DnsName = request.DnsName,
                TailscaleIp = request.TailscaleIp,
                OperatingSystem = request.OperatingSystem,
                AgentVersion = request.AgentVersion,
                AgentTokenProtected = invite.AgentTokenProtected,
                RustDeskPasswordProtected = invite.RustDeskPasswordProtected,
                ControllerTokenProtected = invite.ControllerTokenProtected,
                Role = invite.Role,
                AdvertisesExitNode = invite.AdvertiseExitNode,
                State = DeviceConnectionState.Online,
                LastSeen = DateTimeOffset.UtcNow
            };
            _state.Config.Devices.Add(device);
            var oldExpiry = invite.ExpiresAt;
            invite.RedeemedAt = DateTimeOffset.UtcNow;
            invite.ExpiresAt = invite.RedeemedAt.Value;
            invite.EnrolledDeviceId = device.Id;
            try
            {
                await _state.SaveAsync(cancellationToken);
            }
            catch
            {
                _state.Config.Devices.Remove(device);
                invite.RedeemedAt = null;
                invite.ExpiresAt = oldExpiry;
                invite.EnrolledDeviceId = null;
                throw;
            }

            try { await _headscale.RevokeKeyAsync(invite.TailscaleKeyId, CancellationToken.None); } catch { }
            if (!string.IsNullOrWhiteSpace(invite.HostedInviteIdHash))
            {
                var hostedId = invite.HostedInviteIdHash;
                var hostedUrl = invite.HostedUrlProtected;
                try
                {
                    await new HostedInviteClient(_state).DeleteAsync(hostedId, CancellationToken.None);
                    invite.HostedInviteIdHash = string.Empty;
                    invite.HostedUrlProtected = string.Empty;
                    try { await _state.SaveAsync(CancellationToken.None); }
                    catch
                    {
                        invite.HostedInviteIdHash = hostedId;
                        invite.HostedUrlProtected = hostedUrl;
                    }
                }
                catch { /* Enrollment remains valid; the hosted object expires independently. */ }
            }
            return Decision(200, "Enrollment complete.", accepted: true);
        }
        finally
        {
            _state.InviteGate.Release();
        }
    }

    private static EnrollmentDecision Decision(int statusCode, string message, bool accepted = false) =>
        new(statusCode, new EnrollmentResponse { Accepted = accepted, Message = message });
}
