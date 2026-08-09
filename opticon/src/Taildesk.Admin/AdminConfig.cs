using System.Security.Principal;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public enum AdminMode
{
    Primary,
    Secondary
}

public sealed class AdminConfig
{
    public int SchemaVersion { get; set; } = 5;
    public AdminMode Mode { get; set; } = AdminMode.Primary;
    public bool SetupComplete { get; set; }
    public string HeadscaleApiUrl { get; set; } = string.Empty;
    public string HeadscaleControlUrl { get; set; } = string.Empty;
    public string HeadscaleUserId { get; set; } = string.Empty;
    public string HeadscaleApiKeyProtected { get; set; } = string.Empty;
    public string CoordinatorBindAddress { get; set; } = string.Empty;
    public int CoordinatorPort { get; set; } = 45830;
    public string CoordinatorUrl { get; set; } = string.Empty;
    public string ControllerTokenProtected { get; set; } = string.Empty;
    public string InviteOutputDirectory { get; set; } = PrivateStorage.InviteDirectory;
    public string RustDeskPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
    public List<DeviceRecord> Devices { get; set; } = [];
    public List<InviteRecord> Invites { get; set; } = [];
    public Dictionary<Guid, bool> PrivacyMode2ByDevice { get; set; } = [];
}

public sealed class AdminState
{
    private readonly JsonFileStore<AdminConfig> _store = new(AppPaths.AdminConfigFile);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Enrollment, cancellation, registry snapshots, and device revocation must
    // serialize so a controller cannot receive a half-updated device registry.
    internal SemaphoreSlim InviteGate { get; } = new(1, 1);

    public AdminConfig Config { get; private set; } = new();
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Config = await _store.LoadAsync(cancellationToken);
        Config.PrivacyMode2ByDevice ??= [];
        if (Config.SchemaVersion < 5)
        {
            Config.SchemaVersion = 5;
            await SaveAsync(cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(Config.InviteOutputDirectory) || PrivateStorage.IsOneDrivePath(Config.InviteOutputDirectory))
        {
            Config.InviteOutputDirectory = PrivateStorage.InviteDirectory;
            Directory.CreateDirectory(Config.InviteOutputDirectory);
            await SaveAsync(cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(Config.HeadscaleControlUrl))
        {
            Config.HeadscaleControlUrl = Config.HeadscaleApiUrl;
        }
        var bootstrapPath = AppPaths.ControllerBootstrapFile;
        if (File.Exists(bootstrapPath))
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                      ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
            var content = MachineStorageSecurity.ReadUserBootstrap(
                bootstrapPath, sid, maximumBytes: 64 * 1024);
            var bootstrap = JsonSerializer.Deserialize<AdminBootstrap>(content, JsonDefaults.Options)
                            ?? throw new InvalidDataException("The protected controller bootstrap is empty.");
            if (bootstrap.SchemaVersion != 1
                || !bootstrap.IsMachineProtected
                || string.IsNullOrWhiteSpace(bootstrap.ControllerTokenProtected)
                || string.IsNullOrWhiteSpace(bootstrap.DeviceName)
                || !Uri.TryCreate(bootstrap.CoordinatorUrl, UriKind.Absolute, out var coordinator)
                || coordinator.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(coordinator.UserInfo))
                throw new InvalidDataException("The protected controller bootstrap metadata is invalid.");
            var bootstrapToken = SecretProtector.Unprotect(
                bootstrap.ControllerTokenProtected,
                SecretScope.LocalMachine);
            Config.Mode = AdminMode.Secondary;
            Config.SetupComplete = true;
            Config.CoordinatorUrl = bootstrap.CoordinatorUrl;
            Config.ControllerTokenProtected = SecretProtector.Protect(bootstrapToken, SecretScope.CurrentUser);
            await SaveAsync(cancellationToken);
            MachineStorageSecurity.DeleteUserBootstrap(bootstrapPath, sid);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _store.SaveAsync(Config, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DeviceRecord? FindDevice(Guid id) => Config.Devices.FirstOrDefault(device => device.Id == id);
}
