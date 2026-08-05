using System.Diagnostics;
using System.Text.Json;
using Taildesk.Admin;
using Taildesk.Shared;

var state = new AdminState();
await state.InitializeAsync();
if (state.Config.Mode != AdminMode.Primary || !state.Config.SetupComplete)
    throw new InvalidOperationException("The local Opticon primary command center is not configured.");
var dockerE2E = args.Contains("--docker-e2e", StringComparer.OrdinalIgnoreCase);

if (args.Contains("--coordinator", StringComparer.OrdinalIgnoreCase))
{
    await using var coordinator = new CoordinatorServer(state, new HeadscaleApiClient(state));
    await coordinator.StartAsync();
    Console.WriteLine($"Coordinator listening on {state.Config.CoordinatorUrl}.");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
if (args.Contains("--system-checks", StringComparer.OrdinalIgnoreCase))
{
    var installedOpticon = Path.Combine(AppPaths.InstallDirectory, "Admin", "Opticon.exe");
    var checks = await new SystemHealthChecker(state, new HeadscaleApiClient(state), installedOpticon).RunAsync();
    foreach (var check in checks)
        Console.WriteLine($"{check.Status,-7} {check.Area,-14} {check.Name}: {check.Detail}");
    var failures = checks.Count(check => check.Severity == SystemCheckSeverity.Failure);
    var warnings = checks.Count(check => check.Severity == SystemCheckSeverity.Warning);
    Console.WriteLine($"SUMMARY {checks.Count - failures - warnings} passed, {warnings} warnings, {failures} failures");
    if (failures > 0) Environment.ExitCode = 2;
    return;
}
if (args.Contains("--verify-active", StringComparer.OrdinalIgnoreCase))
{
    var active = state.Config.Invites
        .Where(invite => !invite.RedeemedAt.HasValue && !invite.IsExpired && !string.IsNullOrWhiteSpace(invite.HostedUrlProtected))
        .OrderByDescending(invite => invite.CreatedAt)
        .FirstOrDefault() ?? throw new InvalidOperationException("No active hosted invitation exists.");
    await VerifyLandingUsesCurrentBundleAsync(active.HostedUrl, active.Role);
    Console.WriteLine($"PASS active invitation for {active.DeviceName} serves the current hash-pinned bundle and remains valid until {active.ExpiresAt:u}.");
    return;
}

if (dockerE2E) await DockerInviteAcceptance.BuildAsync();

var headscale = new HeadscaleApiClient(state);
InviteBundleResult? result = null;
var stopwatch = Stopwatch.StartNew();
try
{
    result = await new InviteBundleService(state, headscale).CreateAsync(
        "Opticon link validation " + DateTimeOffset.UtcNow.ToString("HHmmss"),
        DeviceRole.ManagedOnly,
        advertiseExitNode: false,
        allowedRoots: ["Documents"]);
    stopwatch.Stop();

    var expectedDefault = DateTimeOffset.UtcNow.AddDays(InvitationPolicy.DefaultLifetimeDays);
    if (result.Record.ExpiresAt < expectedDefault.AddMinutes(-2) || result.Record.ExpiresAt > expectedDefault.AddMinutes(2))
        throw new InvalidDataException("The live invitation did not receive the fourteen-day default expiry.");
    var originalUrl = result.InvitationUrl;
    var originalExpiry = result.Record.ExpiresAt;
    var originalKeyId = result.Record.TailscaleKeyId;
    var oldKeyRevoked = await new InviteBundleService(state, headscale).ExtendAsync(result.Record, 1);
    if (!oldKeyRevoked || result.Record.HostedUrl != originalUrl || result.Record.ExpiresAt != originalExpiry.AddDays(1) || result.Record.TailscaleKeyId == originalKeyId)
        throw new InvalidDataException("Live invitation extension did not preserve the URL, advance expiry, and rotate the one-use key.");

    var link = new Uri(result.InvitationUrl);
    if (link.Scheme != Uri.UriSchemeHttps || link.Fragment.Length < 33)
        throw new InvalidDataException("The generated invitation URL is missing HTTPS or its private fragment key.");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var landing = await client.GetAsync(result.InvitationUrl.Split('#')[0]);
    var html = await landing.Content.ReadAsStringAsync();
    if (!landing.IsSuccessStatusCode || !html.Contains("Install Opticon", StringComparison.Ordinal))
        throw new InvalidDataException("The generated invitation landing page is unavailable.");
    var encryptedUrl = result.InvitationUrl.Split('#')[0].TrimEnd('/') + "/invite.tdinvite";
    var encrypted = await client.GetByteArrayAsync(encryptedUrl);
    if (encrypted.Length < 64)
        throw new InvalidDataException("The hosted encrypted invitation is unexpectedly short.");

    if (dockerE2E) await DockerInviteAcceptance.VerifyAsync(result);

    Console.WriteLine($"PASS hosted invitation created in {stopwatch.Elapsed.TotalSeconds:F2}s with a 14-day default; same-URL extension rotated its key.");
}
finally
{
    if (result is not null)
    {
        try { await new HostedInviteClient(state).DeleteAsync(result.Record.HostedInviteIdHash); } catch { }
        try { await headscale.RevokeKeyAsync(result.Record.TailscaleKeyId); } catch { }
        state.Config.Invites.RemoveAll(invite => invite.Id == result.Record.Id);
        await state.SaveAsync();
        Console.WriteLine("PASS test invitation and Headscale key were revoked and removed.");
    }
}
if (dockerE2E && result is not null)
{
    await DockerInviteAcceptance.VerifyRemovedAsync(result.InvitationUrl);
    Console.WriteLine("PASS Opticon Docker invitation acceptance completed end to end without retaining credentials.");
}

static async Task VerifyLandingUsesCurrentBundleAsync(string invitationUrl, DeviceRole role)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var landing = await client.GetAsync(invitationUrl.Split('#')[0]);
    var html = await landing.Content.ReadAsStringAsync();
    if (!landing.IsSuccessStatusCode) throw new InvalidDataException($"The active invitation landing page returned {(int)landing.StatusCode}.");

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    string? manifestPath = null;
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "fly-headscale", "artifacts", "manifest.json");
        if (File.Exists(candidate)) { manifestPath = candidate; break; }
        directory = directory.Parent;
    }
    if (manifestPath is null) throw new FileNotFoundException("The local Fly artifact manifest was not found.");

    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
    var artifact = manifest.RootElement.GetProperty("artifacts").EnumerateArray().First(item =>
        item.GetProperty("product").GetString() == "OpticonBundle"
        && item.GetProperty("role").GetString() == role.ToString());
    var file = artifact.GetProperty("file").GetString()!;
    var hash = artifact.GetProperty("sha256").GetString()!;
    var size = artifact.GetProperty("size").GetInt64().ToString();
    if (!html.Contains(file, StringComparison.Ordinal) || !html.Contains(hash, StringComparison.OrdinalIgnoreCase) || !html.Contains(size, StringComparison.Ordinal))
        throw new InvalidDataException("The invitation landing page does not pin the current role bundle filename, size, and SHA-256 hash.");
}
