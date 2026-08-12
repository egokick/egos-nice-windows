using System.Text.Json.Serialization;

namespace Taildesk.Shared;

public enum DeviceRole
{
    ManagedOnly,
    ControllerAndManaged
}

public enum DeviceConnectionState
{
    Unknown,
    Offline,
    TailscaleOnly,
    Online
}

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class DeviceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TailnetDeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DnsName { get; set; } = string.Empty;
    public string TailscaleIp { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string GuardianVersion { get; set; } = string.Empty;
    public int UpdateProtocolVersion { get; set; }
    public string AgentTokenProtected { get; set; } = string.Empty;
    public string RustDeskPasswordProtected { get; set; } = string.Empty;
    public string ControllerTokenProtected { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public DeviceConnectionState State { get; set; }
    public bool AdvertisesExitNode { get; set; }
    public bool ExitNodeApproved { get; set; }
    public bool RustDeskReady { get; set; }
    public bool SshReady { get; set; }
    public int SshPort { get; set; } = RemoteAdministrationProtocol.SshPort;
    public UpdateStatusDto? UpdateStatus { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;
    public string Notes { get; set; } = string.Empty;
    public bool PendingCredentialRotation { get; set; }
    public Guid? PendingCredentialRotationId { get; set; }
    public string PendingAgentTokenProtected { get; set; } = string.Empty;
    public string PendingRustDeskPasswordProtected { get; set; } = string.Empty;
    public bool PendingRoleSync { get; set; }
    public List<Guid> AuthorizedControllerIds { get; set; } = [];

    [JsonIgnore]
    public bool PrivacyMode2Enabled { get; set; }

    [JsonIgnore]
    public DateTimeOffset? OnlineSince { get; set; }

    [JsonIgnore]
    public int? BatteryPercentage { get; set; }

    [JsonIgnore]
    public string OnlineTime => State != DeviceConnectionState.Online || !OnlineSince.HasValue
        ? "—"
        : FormatOnlineTime(DateTimeOffset.UtcNow - OnlineSince.Value);

    [JsonIgnore]
    public string BatteryLife => State == DeviceConnectionState.Online && BatteryPercentage is >= 0 and <= 100
        ? $"{BatteryPercentage.Value}%"
        : "—";

    internal static string FormatOnlineTime(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{Math.Max(0, (int)duration.TotalMinutes)}m";
    }
}

public sealed class InviteRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string InviteSecretHash { get; set; } = string.Empty;
    public string AgentTokenProtected { get; set; } = string.Empty;
    public string RustDeskPasswordProtected { get; set; } = string.Empty;
    public string ControllerTokenProtected { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public Guid? EnrolledDeviceId { get; set; }
    public string BundlePath { get; set; } = string.Empty;
    public bool AdvertiseExitNode { get; set; }
    public string TailscaleKeyId { get; set; } = string.Empty;
    public List<string> PendingTailscaleKeyRevocations { get; set; } = [];
    public string HostedInviteIdHash { get; set; } = string.Empty;
    public string HostedUrlProtected { get; set; } = string.Empty;
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public string[] TargetRuntimes { get; set; } = [];
    public string InstallProtocol { get; set; } = InvitationPolicy.SourceInstallProtocol;

    // Gateway-only rows are synthesized by the primary Command Center from
    // authenticated, sanitized inventory. They are deliberately never written
    // to admin.json because they do not contain invitation secrets or a URL.
    [JsonIgnore]
    public bool IsRemoteOrphan { get; set; }

    [JsonIgnore]
    public bool CanRevokeRemoteNetworkKey { get; set; }

    [JsonIgnore]
    public string RecordSource => IsRemoteOrphan ? "Gateway only" : "Local";

    [JsonIgnore]
    public string HostedUrl => string.IsNullOrWhiteSpace(HostedUrlProtected)
        ? string.Empty
        : SecretProtector.Unprotect(HostedUrlProtected);

    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    [JsonIgnore]
    public string Status => IsRemoteOrphan
        ? "Remote orphan"
        : (PendingTailscaleKeyRevocations?.Count ?? 0) > 0
        ? "Key cleanup pending"
        : RedeemedAt.HasValue ? "Redeemed" : IsExpired ? "Expired" : "Ready";
}

public sealed class InvitePayload
{
    public int SchemaVersion { get; set; } = InvitationPolicy.HostedLinkSchemaVersion;
    public Guid InviteId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string InviteSecret { get; set; } = string.Empty;
    public string TailscaleAuthKey { get; set; } = string.Empty;
    public string HeadscaleLoginUrl { get; set; } = string.Empty;
    public string AgentToken { get; set; } = string.Empty;
    public string RustDeskPassword { get; set; } = string.Empty;
    public string ControllerToken { get; set; } = string.Empty;
    public string CoordinatorUrl { get; set; } = string.Empty;
    public string ExpectedTailnet { get; set; } = string.Empty;
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public string[] TargetRuntimes { get; set; } = [];
    public string InstallProtocol { get; set; } = InvitationPolicy.SourceInstallProtocol;
    public ClientInstallValidationPolicy ClientInstallValidation { get; set; } = new();
    public bool AdvertiseExitNode { get; set; }
    public string[] AllowedRoots { get; set; } = ["Desktop", "Documents", "Downloads", "Pictures", "Videos"];
}

public sealed class AdminBootstrap
{
    public int SchemaVersion { get; set; } = 1;
    public string CoordinatorUrl { get; set; } = string.Empty;
    public string ControllerTokenProtected { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public bool IsMachineProtected { get; set; }
}

public sealed class EnrollmentRequest
{
    public Guid InviteId { get; set; }
    public string InviteSecret { get; set; } = string.Empty;
    public string TailnetDeviceId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DnsName { get; set; } = string.Empty;
    public string TailscaleIp { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
}

public sealed class EnrollmentResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class DeviceStatusDto
{
    public string HostName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string GuardianVersion { get; set; } = string.Empty;
    public int UpdateProtocolVersion { get; set; }
    public string TailscaleIp { get; set; } = string.Empty;
    public string TailnetDeviceId { get; set; } = string.Empty;
    public bool RustDeskRunning { get; set; }
    public bool RustDeskReady { get; set; }
    public bool SshReady { get; set; }
    public int SshPort { get; set; } = RemoteAdministrationProtocol.SshPort;
    public bool AdvertisesExitNode { get; set; }
    public long FreeDiskBytes { get; set; }
    public long TotalDiskBytes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public long? OnlineDurationSeconds { get; set; }
    public int? BatteryPercentage { get; set; }
    public DateTimeOffset ServerTime { get; set; }
    public UpdateStatusDto? UpdateStatus { get; set; }
}

public sealed class RootDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PathHint { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}

public sealed class FileEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
}

public sealed class FileListingDto
{
    public string Root { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public List<FileEntryDto> Entries { get; set; } = [];
}

public sealed class CreateDirectoryRequest
{
    public string Root { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}

public sealed class ConditionalDeleteRequest
{
    public string Root { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long ExpectedLength { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
}
public sealed class MediaLinkRequest
{
    public string Root { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}

public sealed class MediaLinkResponse
{
    public string RelativeUrl { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class MediaLinksRequest
{
    public string Root { get; set; } = string.Empty;
    public List<string> RelativePaths { get; set; } = [];
}

public sealed class MediaLinkItemDto
{
    public string RelativePath { get; set; } = string.Empty;
    public string RelativeUrl { get; set; } = string.Empty;
}

public sealed class MediaLinksResponse
{
    public DateTimeOffset ExpiresAt { get; set; }
    public List<MediaLinkItemDto> Items { get; set; } = [];
}

public sealed class UploadStatusDto
{
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class ExitNodeRequest
{
    public bool Enabled { get; set; }
}

public sealed class CredentialRotationRequest
{
    public Guid OperationId { get; set; }
    public string NewAgentToken { get; set; } = string.Empty;
    public string NewRustDeskPassword { get; set; } = string.Empty;
}

public sealed class CredentialRotationCommitRequest
{
    public Guid OperationId { get; set; }
}

public sealed class RoleChangeRequest
{
    public DeviceRole Role { get; set; }
}

public sealed class RegistrySnapshot
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ControllerDeviceDto> Devices { get; set; } = [];
}

public sealed class ControllerDeviceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DnsName { get; set; } = string.Empty;
    public string TailscaleIp { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public bool HasAgentAccess { get; set; }
    public bool HasRemoteAccess { get; set; }
}

public sealed class ControllerCredentialDto
{
    public Guid DeviceId { get; set; }
    public string AgentToken { get; set; } = string.Empty;
    public string RustDeskPassword { get; set; } = string.Empty;
}

public sealed class TransferItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public TransferState State { get; set; } = TransferState.Queued;
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
