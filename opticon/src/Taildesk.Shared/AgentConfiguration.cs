using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taildesk.Shared;

public sealed class AgentConfig
{
    public int SchemaVersion { get; set; } = 2;
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public int ApiPort { get; set; } = 45831;
    public string BindAddress { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public DeviceRole Role { get; set; } = DeviceRole.ManagedOnly;
    public string AgentTokenHash { get; set; } = string.Empty;
    public string MediaSigningKeyProtected { get; set; } = string.Empty;
    public string UpdateHealthTokenProtected { get; set; } = string.Empty;
    public string CoordinatorUrl { get; set; } = string.Empty;
    public Guid? PendingInviteId { get; set; }
    public Guid? CompletedInviteId { get; set; }
    public string PendingInviteSecretProtected { get; set; } = string.Empty;
    public bool AdvertiseExitNode { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> SharedRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ControllerShortcutPaths { get; set; } = [];
    public long MaxUploadBytes { get; set; } = 256L * 1024 * 1024 * 1024;
    public long MinimumFreeSpaceBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 2;
    public int MaxUploadDurationMinutes { get; set; } = 24 * 60;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    [JsonIgnore]
    public string MediaSigningKey => SecretProtector.Unprotect(MediaSigningKeyProtected, SecretScope.LocalMachine);

    [JsonIgnore]
    public string UpdateHealthToken => SecretProtector.Unprotect(UpdateHealthTokenProtected, SecretScope.LocalMachine);

    [JsonIgnore]
    public string PendingInviteSecret => SecretProtector.Unprotect(PendingInviteSecretProtected, SecretScope.LocalMachine);
}
