using System.Net;
using System.Net.Sockets;

namespace Taildesk.Shared;

public static class RemoteAdministrationProtocol
{
    public const int UpdateVersion = 1;
    public const int SshPort = 45832;
    public const string AgentTaskName = "Taildesk Agent";
    public const string GuardianTaskName = "Taildesk Update Guardian";
    public const string GuardianWatchdogTaskName = "Taildesk Update Guardian Watchdog";
    public const string GuardianWatchdogArgument = "--update-watchdog";
    public const string SshSupervisorTaskName = "Taildesk Opticon SSH Supervisor";
    public const string SshAccountName = "OpticonRemoteAdmin";
    public const string SshAdminProbeArgument = "--ssh-admin-probe";
    public const int SshAdminProbeVersion = 1;
    public static readonly TimeSpan MaximumSshSession = TimeSpan.FromHours(8);
    public static readonly TimeSpan UpdateCommitWindow = TimeSpan.FromMinutes(5);

    public static bool IsTailscaleIpv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}

public sealed class SshAdminAttestation
{
    public int SchemaVersion { get; set; } = RemoteAdministrationProtocol.SshAdminProbeVersion;
    public string Challenge { get; set; } = string.Empty;
    public string UserSid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool Elevated { get; set; }
    public string ElevationType { get; set; } = string.Empty;
    public int IntegrityRid { get; set; }
    public bool AdministratorsEnabled { get; set; }
    public bool AdministrativeCapability { get; set; }
    public string TokenType { get; set; } = string.Empty;
}

public sealed class SshAccessRequest
{
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class SshAccessResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string UserName { get; set; } = RemoteAdministrationProtocol.SshAccountName;
    public int Port { get; set; } = RemoteAdministrationProtocol.SshPort;
    public string Host { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string HostPublicKey { get; set; } = string.Empty;
    public string SystemRoot { get; set; } = string.Empty;
}

public sealed class SshRevokeRequest
{
    public string SessionId { get; set; } = string.Empty;
}

public enum UpdatePhase
{
    None,
    Downloading,
    Verifying,
    Ready,
    ActivationScheduled,
    Activating,
    AwaitingCommit,
    Committed,
    RollingBack,
    RolledBack,
    Failed
}

public sealed class OpticonUpdateRequest
{
    public int ProtocolVersion { get; set; } = RemoteAdministrationProtocol.UpdateVersion;
    public Guid OperationId { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long PackageSize { get; set; }
    public string PackageSha256 { get; set; } = string.Empty;
}

public sealed class UpdateOperationRequest
{
    public Guid OperationId { get; set; }
}

public sealed class UpdateCommitRequest
{
    public Guid OperationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UpdateStatusDto
{
    public Guid OperationId { get; set; }
    public bool MaintenanceBootstrap { get; set; }
    public UpdatePhase Phase { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CommitDeadline { get; set; }
    public bool RollbackAvailable { get; set; }
}

public sealed class UpdateJournal
{
    public int SchemaVersion { get; set; } = 1;
    public bool MaintenanceBootstrap { get; set; }
    public bool SshWasListening { get; set; }
    public Guid OperationId { get; set; }
    public UpdatePhase Phase { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
    public long PackageSize { get; set; }
    public string StagedAgentDirectory { get; set; } = string.Empty;
    public string CandidateDirectory { get; set; } = string.Empty;
    public string RollbackDirectory { get; set; } = string.Empty;
    public string FailedCandidateDirectory { get; set; } = string.Empty;
    public string BindAddress { get; set; } = string.Empty;
    public int AgentProcessId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ActivateAfter { get; set; }
    public DateTimeOffset? GuardianClaimedAt { get; set; }
    public DateTimeOffset? CommitDeadline { get; set; }
    public string Message { get; set; } = string.Empty;

    public UpdateStatusDto ToStatus() => new()
    {
        OperationId = OperationId,
        MaintenanceBootstrap = MaintenanceBootstrap,
        Phase = Phase,
        CurrentVersion = CurrentVersion,
        TargetVersion = TargetVersion,
        Message = Message,
        StartedAt = StartedAt,
        UpdatedAt = UpdatedAt,
        CommitDeadline = CommitDeadline,
        RollbackAvailable = !string.IsNullOrWhiteSpace(RollbackDirectory)
                            && Directory.Exists(RollbackDirectory)
    };
}

public sealed class OpticonReleaseManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public int UpdateProtocolVersion { get; set; } = RemoteAdministrationProtocol.UpdateVersion;
    public string MinimumGuardianVersion { get; set; } = "1.1.1";
    public List<OpticonReleaseFile> Files { get; set; } = [];
}

public sealed class OpticonReleaseFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string SignerThumbprint { get; set; } = string.Empty;
}

public sealed class ArtifactManifestDto
{
    public int SchemaVersion { get; set; }
    public List<ArtifactRecordDto> Artifacts { get; set; } = [];
}

public sealed class ArtifactRecordDto
{
    public string Product { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DeviceRole? Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
