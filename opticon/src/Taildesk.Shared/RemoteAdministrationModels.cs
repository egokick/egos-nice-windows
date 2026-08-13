using System.Net;
using System.Net.Sockets;

namespace Taildesk.Shared;

public static class RemoteAdministrationProtocol
{
    public const int UpdateVersion = 1;
    public const int CoordinatorPort = 45830;
    public const int SshPort = 45832;
    public const string AgentTaskName = "Taildesk Agent";
    public const string GuardianTaskName = "Taildesk Update Guardian";
    public const string GuardianWatchdogTaskName = "Taildesk Update Guardian Watchdog";
    public const string GuardianWatchdogArgument = "--update-watchdog";
    public const string MinimumWatchdogGuardianVersion = "1.1.2";
    public const string MinimumProtectedMachineStateAgentVersion = "1.1.39";
    // This is deliberately a one-version bridge, not a rolling legacy channel.
    // Its package is signed by the retired invitation trust root solely so 1.1.38
    // can transition its protected machine-state ACLs before normal updates resume.
    public const string LegacyMachineStateMigrationBridgeVersion = "1.1.41";
    public const string SshSupervisorTaskName = "Taildesk Opticon SSH Supervisor";
    public const string SshAccountName = "OpticonRemoteAdmin";
    public const string SshAdminProbeArgument = "--ssh-admin-probe";
    public const int SshAdminProbeVersion = 1;
    public static readonly TimeSpan MaximumSshSession = TimeSpan.FromHours(8);
    public static readonly TimeSpan UpdateCommitWindow = TimeSpan.FromMinutes(5);
    private static readonly Version MinimumProtectedMachineStateVersion =
        Version.Parse(MinimumProtectedMachineStateAgentVersion);
    private static readonly Version LegacyMachineStateMigrationBridgeTargetVersion =
        Version.Parse(LegacyMachineStateMigrationBridgeVersion);

    public static bool SupportsGuardianWatchdog(Version version) =>
        version >= UpdatePackageVerifier.ParseVersion(MinimumWatchdogGuardianVersion);

    // 1.1.39 is the first published Agent that creates the protected,
    // non-inherited ProgramData machine-state layout. An older Agent cannot
    // cross this boundary unattended because the new Agent must not adopt
    // mutable legacy state with inherited ACLs.
    public static bool RequiresLegacyMachineStateMigration(
        Version installedAgentVersion,
        Version targetAgentVersion) =>
        installedAgentVersion < MinimumProtectedMachineStateVersion
        && targetAgentVersion >= MinimumProtectedMachineStateVersion;

    public static bool IsLegacyMachineStateMigrationBridge(
        Version installedAgentVersion,
        Version targetAgentVersion,
        string? legacyMigrationSignerThumbprint) =>
        installedAgentVersion == new Version(1, 1, 38)
        && targetAgentVersion == LegacyMachineStateMigrationBridgeTargetVersion
        && string.Equals(
            legacyMigrationSignerThumbprint,
            InvitationSigning.CertificateThumbprint,
            StringComparison.Ordinal);

    public static bool IsTailscaleIpv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    public static bool IsCanonicalPrivateCoordinatorUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var coordinator))
            return false;
        return coordinator.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && coordinator.Port == CoordinatorPort
               && coordinator.AbsolutePath == "/"
               && string.IsNullOrEmpty(coordinator.UserInfo)
               && string.IsNullOrEmpty(coordinator.Query)
               && string.IsNullOrEmpty(coordinator.Fragment)
               && IsTailscaleIpv4(coordinator.Host);
    }

    public static bool IsSshLeaseWithinRequestedLifetime(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        TimeSpan requestedLifetime) =>
        createdAt != default
        && expiresAt > createdAt
        && expiresAt - createdAt <= requestedLifetime;
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
    // New Agents use a duration so provisioning latency and clock skew cannot
    // extend or invalidate the requested lease. ExpiresAt remains populated by
    // new clients for compatibility with installed Agents that predate this field.
    public int? RequestedLifetimeSeconds { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class SshAccessResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string UserName { get; set; } = RemoteAdministrationProtocol.SshAccountName;
    public int Port { get; set; } = RemoteAdministrationProtocol.SshPort;
    public string Host { get; set; } = string.Empty;
    // Null on older Agents. New clients validate the target-relative interval
    // instead of comparing the target's wall clock with the command center.
    public DateTimeOffset? CreatedAt { get; set; }
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

public sealed class GuardianMaintenanceStatusDto
{
    public Guid OperationId { get; set; }
    public string PreviousVersion { get; set; } = string.Empty;
    public string GuardianVersion { get; set; } = string.Empty;
    public bool Changed { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class UpdateJournal
{
    public int SchemaVersion { get; set; } = 1;
    // Schema 1 is the signed executable bundle transaction. Schema 2 adds the
    // independently verified source-archive transaction below. Old Guardians
    // reject schema 2 rather than treating an unsigned local build as a bundle.
    public UpdateDeliveryMode DeliveryMode { get; set; } = UpdateDeliveryMode.SignedBundle;
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
    public string SourceDownloadUrl { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public string SourceBuildOutputDirectory { get; set; } = string.Empty;
    public string SourceBuildAttestationPath { get; set; } = string.Empty;
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
    public string SigningProfile { get; set; } = string.Empty;
    public string SourceReleaseKeyId { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public bool LegacyMigration { get; set; }
    public string LegacyMigrationSignerThumbprint { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public int UpdateProtocolVersion { get; set; } = RemoteAdministrationProtocol.UpdateVersion;
    public string MinimumGuardianVersion { get; set; } = RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion;
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
    public string SignerThumbprint { get; set; } = string.Empty;
    // Optional during the Fly-volume migration.  New immutable bundles use an
    // absolute, CloudFront HTTPS URL rather than the control-plane origin.
    public string DownloadUrl { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    // Non-empty only for the immutable 1.1.41 bridge. The Agent independently
    // verifies the matching signed inner manifest before it can run.
    public string LegacyMigrationSignerThumbprint { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public List<string> TargetRuntimes { get; set; } = [];
    public ClientInstallValidationPolicy ClientInstallValidation { get; set; } = new();
}
