namespace Taildesk.Shared;

/// <summary>
/// Versioned, source-archive-only delivery protocol.  It is deliberately
/// separate from the legacy signed-bundle update protocol so an older Agent
/// cannot accidentally accept a source build it does not know how to attest.
/// </summary>
public static class SourceUpdateProtocol
{
    public const int Version = 1;
    public const string MinimumGuardianVersion = "1.2.0";
    public const string RequiredSdkVersion = "10.0.302";
    public const string RequiredRuntimeVersion = "10.0.10";
    public const string SourceBuildScriptName = "Build-OpticonUpdateFromSource.ps1";
}

public enum UpdateDeliveryMode
{
    SignedBundle = 0,
    SourceArchive = 1
}

/// <summary>
/// Pins one immutable source archive.  The command center may choose the
/// release, but it cannot choose an arbitrary URL, source manifest, SDK, or
/// output identity: each is checked independently by the Agent and Guardian.
/// </summary>
public sealed class SourceUpdateRequest
{
    public int ProtocolVersion { get; set; } = SourceUpdateProtocol.Version;
    public Guid OperationId { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
}

/// <summary>
/// The signed inner manifest in opticon-source-&lt;version&gt;.zip.  It has no
/// device-specific data; role and device identity stay in the authenticated
/// source-update request or invitation.
/// </summary>
public sealed class SourceArchiveManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string SourceReleaseKeyId { get; set; } = string.Empty;
    public string SourceReleaseCertificateBase64 { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string ProductSigningCertificateBase64 { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public List<string> TargetRuntimes { get; set; } = [];
    public List<SourceArchiveFile> Files { get; set; } = [];
}

public sealed class SourceArchiveFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Machine-local record written only after a verified source archive has built
/// its Agent output in a restricted directory.  Guardian rechecks every file
/// before copying it into the atomic candidate directory.
/// </summary>
public sealed class SourceUpdateBuildAttestation
{
    public int SchemaVersion { get; set; } = 1;
    public string ReleaseVersion { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public long SourceSize { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceManifestSha256 { get; set; } = string.Empty;
    public string SourceManifestKeyId { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string TargetRuntime { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public List<SourceUpdateBuildFile> Files { get; set; } = [];
}

public sealed class SourceUpdateBuildFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
