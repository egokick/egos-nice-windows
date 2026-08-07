namespace Taildesk.Shared;

public static class EnrollmentReplayPolicy
{
    public static bool IsExactAcceptedReplay(
        InviteRecord invite,
        DeviceRecord? enrolledDevice,
        EnrollmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(request);
        return invite.RedeemedAt.HasValue
               && SecurityHelpers.FixedTimeEquals(
                   invite.InviteSecretHash,
                   SecurityHelpers.HashToken(request.InviteSecret))
               && invite.EnrolledDeviceId.HasValue
               && enrolledDevice is not null
               && enrolledDevice.Id == invite.EnrolledDeviceId.Value
               && enrolledDevice.TailnetDeviceId.Equals(request.TailnetDeviceId, StringComparison.Ordinal)
               && enrolledDevice.TailscaleIp.Equals(request.TailscaleIp, StringComparison.OrdinalIgnoreCase)
               && enrolledDevice.HostName.Equals(request.HostName, StringComparison.OrdinalIgnoreCase)
               && enrolledDevice.DnsName.Equals(request.DnsName, StringComparison.OrdinalIgnoreCase)
               && enrolledDevice.OperatingSystem.Equals(request.OperatingSystem, StringComparison.Ordinal)
               && enrolledDevice.AgentVersion.Equals(request.AgentVersion, StringComparison.Ordinal);
    }
}
