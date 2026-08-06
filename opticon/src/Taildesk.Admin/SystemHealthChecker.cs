using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public enum SystemCheckSeverity
{
    Pass,
    Warning,
    Failure
}

public sealed record SystemCheckResult(string Area, string Name, SystemCheckSeverity Severity, string Detail)
{
    public string Status => Severity.ToString().ToUpperInvariant();
}

public sealed class SystemHealthChecker
{
    private const string FlyHost = "taildesk-egokick-control.fly.dev";
    private const string FlyOrigin = "https://taildesk-egokick-control.fly.dev";
    private const string FlyDedicatedIpv4 = "213.188.217.227";
    private const string RouteHelperSha256 = "4A6F35EE0F2BE6A3599E417FA6F11A04E7500CFE639C3CE9EF9788A9DC0A4C27";
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;
    private readonly string _executablePath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public SystemHealthChecker(AdminState state, HeadscaleApiClient headscale, string? executablePath = null)
    {
        _state = state;
        _headscale = headscale;
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Opticon-SystemChecks/1.0");
    }

    public async Task<IReadOnlyList<SystemCheckResult>> RunAsync(
        IProgress<SystemCheckResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SystemCheckResult>();
        void Add(string area, string name, SystemCheckSeverity severity, string detail)
        {
            var item = new SystemCheckResult(area, name, severity, detail);
            results.Add(item);
            progress?.Report(item);
        }

        var config = _state.Config;
        Add("Opticon", "Primary command-center mode",
            config.Mode == AdminMode.Primary && config.SetupComplete ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            config.Mode == AdminMode.Primary && config.SetupComplete
                ? "This installation is the configured primary command center."
                : "Complete primary command-center setup in Settings.");

        var controlValid = Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var controlUri)
                           && controlUri.Scheme == Uri.UriSchemeHttps;
        var apiValid = Uri.TryCreate(config.HeadscaleApiUrl, UriKind.Absolute, out var apiUri)
                       && apiUri.Scheme == Uri.UriSchemeHttps
                       && controlValid
                       && apiUri.Host.Equals(controlUri!.Host, StringComparison.OrdinalIgnoreCase)
                       && apiUri.AbsolutePath.Equals("/opticon/v1/headscale/", StringComparison.Ordinal);
        Add("Opticon", "Self-hosted control addresses", apiValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            apiValid ? $"Control and signed administration use {controlUri!.Host}." : "The Headscale control/API URLs are missing, non-HTTPS, on different hosts, or outside the private API path.");

        try
        {
            var secret = SecretProtector.Unprotect(config.HeadscaleApiKeyProtected);
            Add("Security", "Admin signing secret", secret.Length >= 32 ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                secret.Length >= 32 ? "The DPAPI-protected HMAC signing secret is available." : "The saved signing secret is unexpectedly short; save the correct value in Settings.");
        }
        catch (Exception exception)
        {
            Add("Security", "Admin signing secret", SystemCheckSeverity.Failure, "The saved signing secret cannot be decrypted: " + Safe(exception.Message));
        }

        var inviteDirectoryValid = !string.IsNullOrWhiteSpace(config.InviteOutputDirectory)
                                   && Directory.Exists(config.InviteOutputDirectory)
                                   && !PrivateStorage.IsOneDrivePath(config.InviteOutputDirectory);
        Add("Security", "Private invitation storage", inviteDirectoryValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            inviteDirectoryValid ? $"Invitation state is outside OneDrive at {config.InviteOutputDirectory}." : "The invitation directory is missing or OneDrive-backed; select the local Opticon invitation directory.");

        CheckSigningCertificate(Add);
        await CheckRunningExecutableAsync(_executablePath, Add, cancellationToken);

        JsonDocument? tailscaleStatus = null;
        JsonDocument? tailscalePrefs = null;
        var tailscalePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(tailscalePath))
        {
            Add("Tailscale", "Pinned client", SystemCheckSeverity.Failure, "Tailscale is not installed in Program Files.");
        }
        else
        {
            var version = await ProcessRunner.RunAsync(tailscalePath, ["version"], TimeSpan.FromSeconds(20), cancellationToken);
            var actualVersion = version.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            Add("Tailscale", "Pinned client", version.Succeeded && actualVersion == DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture).Version
                    ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                version.Succeeded ? $"Installed version is {actualVersion}; required version is {DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture).Version}." : "Tailscale version could not be read.");

            tailscaleStatus = await ReadJsonProcessAsync(tailscalePath, ["status", "--json"], cancellationToken);
            tailscalePrefs = await ReadJsonProcessAsync(tailscalePath, ["debug", "prefs"], cancellationToken);
        }

        CheckTailscaleState(config, tailscaleStatus, tailscalePrefs, Add);
        tailscaleStatus?.Dispose();
        tailscalePrefs?.Dispose();

        try
        {
            await _headscale.TestAsync(cancellationToken);
            Add("Control plane", "Signed Headscale administration", SystemCheckSeverity.Pass, "The allowlisted HMAC-authenticated administration API accepted this command center.");
        }
        catch (Exception exception)
        {
            Add("Control plane", "Signed Headscale administration", SystemCheckSeverity.Failure, "Private administration failed: " + Safe(exception.Message));
        }

        await CheckConnectivityAsync(Add, cancellationToken);
        await CheckArtifactsAsync(Add, cancellationToken);
        await CheckCoordinatorAsync(config, Add, cancellationToken);

        try
        {
            var snapshot = await ReadWindowsSnapshotAsync(config, _executablePath, cancellationToken);
            CheckWindowsSnapshot(config, snapshot, Add);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Add("Windows", "Local services and firewall", SystemCheckSeverity.Failure, "Windows configuration inspection failed: " + Safe(exception.Message));
        }

        return results;
    }

    private static void CheckSigningCertificate(Action<string, string, SystemCheckSeverity, string> add)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            using var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, InvitationSigning.CertificateThumbprint, false)
                .OfType<X509Certificate2>().FirstOrDefault(item => item.HasPrivateKey);
            if (certificate is null)
            {
                add("Security", "Invitation signing key", SystemCheckSeverity.Failure, "The pinned invitation-signing certificate or its private key is missing from the current user store.");
                return;
            }
            var remaining = certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow;
            add("Security", "Invitation signing key", remaining > TimeSpan.FromDays(90) ? SystemCheckSeverity.Pass : SystemCheckSeverity.Warning,
                $"Pinned certificate and private key are present; certificate expires {certificate.NotAfter:d}.");
        }
        catch (Exception exception)
        {
            add("Security", "Invitation signing key", SystemCheckSeverity.Failure, "Certificate inspection failed: " + Safe(exception.Message));
        }
    }

    private static async Task CheckRunningExecutableAsync(string path, Action<string, string, SystemCheckSeverity, string> add, CancellationToken cancellationToken)
    {
        var expectedDirectory = Path.Combine(AppPaths.InstallDirectory, "Admin");
        var locationValid = path.StartsWith(expectedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileName(path).Equals("Opticon.exe", StringComparison.OrdinalIgnoreCase);
        add("Opticon", "Installed executable", locationValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Warning,
            locationValid ? $"Running the installed copy at {path}." : $"Running outside the normal installed directory: {path}.");
        try
        {
            await InvitationSigning.VerifyAuthenticodeAsync(path, cancellationToken);
            add("Security", "Opticon executable signature", SystemCheckSeverity.Pass, "The running executable has the pinned Opticon signer.");
        }
        catch (Exception exception)
        {
            add("Security", "Opticon executable signature", SystemCheckSeverity.Failure, Safe(exception.Message));
        }
    }

    private static void CheckTailscaleState(AdminConfig config, JsonDocument? status, JsonDocument? prefs,
        Action<string, string, SystemCheckSeverity, string> add)
    {
        if (status is null)
        {
            add("Tailscale", "Mesh connection", SystemCheckSeverity.Failure, "Tailscale status is unavailable.");
            add("Tailscale", "Hub identity and tags", SystemCheckSeverity.Failure, "The current mesh identity could not be verified.");
            return;
        }
        var root = status.RootElement;
        var backend = GetString(root, "BackendState");
        var self = root.TryGetProperty("Self", out var selfElement) ? selfElement : default;
        var online = self.ValueKind == JsonValueKind.Object && GetBool(self, "Online");
        var ips = GetStrings(root, "TailscaleIPs");
        var currentIp = ips.FirstOrDefault(value => value.Contains('.')) ?? string.Empty;
        var tailnet = root.TryGetProperty("CurrentTailnet", out var current) ? GetString(current, "Name") : string.Empty;
        var expectedTailnet = Uri.TryCreate(config.HeadscaleControlUrl, UriKind.Absolute, out var control) ? control.Host : string.Empty;
        var connected = backend == "Running" && online && currentIp == config.CoordinatorBindAddress && tailnet.Equals(expectedTailnet, StringComparison.OrdinalIgnoreCase);
        add("Tailscale", "Mesh connection", connected ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            connected ? $"Online as {currentIp} in {tailnet}." : $"Expected online {config.CoordinatorBindAddress} in {expectedTailnet}; found backend={backend}, online={online}, IP={currentIp}, tailnet={tailnet}.");

        var tags = self.ValueKind == JsonValueKind.Object ? GetStrings(self, "Tags") : [];
        var tagsValid = tags.Length == 1 && tags.Contains("tag:taildesk-hub", StringComparer.OrdinalIgnoreCase);
        add("Tailscale", "Hub identity and tags", tagsValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            tagsValid ? "This laptop has only tag:taildesk-hub." : "Expected only tag:taildesk-hub; found " + (tags.Length == 0 ? "no tags" : string.Join(", ", tags)) + ".");

        if (prefs is null)
        {
            add("Tailscale", "Client policy", SystemCheckSeverity.Failure, "Tailscale preferences are unavailable.");
            return;
        }
        var pref = prefs.RootElement;
        var controlUrl = GetString(pref, "ControlURL").TrimEnd('/');
        var corpDns = GetBool(pref, "CorpDNS");
        var routeAll = GetBool(pref, "RouteAll");
        var remoteConfig = GetBool(pref, "RemoteConfig");
        var prefsValid = controlUrl.Equals(config.HeadscaleControlUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                         && !corpDns && !routeAll && !remoteConfig;
        add("Tailscale", "Client policy", prefsValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            prefsValid ? "Uses the private Headscale server; external DNS, subnet routes, and remote configuration are disabled." : "Tailscale preferences drifted from the private Opticon policy.");
    }

    private async Task CheckConnectivityAsync(Action<string, string, SystemCheckSeverity, string> add, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(FlyOrigin + "/health", cancellationToken);
            add("Control plane", "Fly health", response.IsSuccessStatusCode ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                response.IsSuccessStatusCode ? "The private Opticon control service is healthy." : $"Fly health returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) { add("Control plane", "Fly health", SystemCheckSeverity.Failure, Safe(exception.Message)); }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.microsoft.com/favicon.ico");
            using var response = await _http.SendAsync(request, cancellationToken);
            add("Internet", "General internet access", response.IsSuccessStatusCode ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                response.IsSuccessStatusCode ? "A non-Fly internet endpoint is reachable." : $"General internet check returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) { add("Internet", "General internet access", SystemCheckSeverity.Failure, Safe(exception.Message)); }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(FlyHost, cancellationToken);
            var pinned = addresses.Any(address => address.ToString() == FlyDedicatedIpv4);
            add("Network", "Fly DNS pin", pinned ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                pinned ? $"{FlyHost} resolves to the dedicated IPv4 {FlyDedicatedIpv4}." : $"DNS no longer returns the expected dedicated IPv4 {FlyDedicatedIpv4}.");
        }
        catch (Exception exception) { add("Network", "Fly DNS pin", SystemCheckSeverity.Failure, Safe(exception.Message)); }
    }

    private async Task CheckArtifactsAsync(Action<string, string, SystemCheckSeverity, string> add, CancellationToken cancellationToken)
    {
        foreach (var artifact in new[]
                 {
                     DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture),
                     DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture)
                 })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, artifact.PrimaryUrl);
                using var response = await _http.SendAsync(request, cancellationToken);
                var size = response.Content.Headers.ContentLength;
                var valid = response.IsSuccessStatusCode && size == artifact.Size;
                add("Enrollment", $"Pinned {artifact.Product} download", valid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                    valid ? $"Fly serves {artifact.FileName} at the pinned {artifact.Size:N0} bytes." : $"Expected HTTP 200 and {artifact.Size:N0} bytes; received HTTP {(int)response.StatusCode} and {size?.ToString("N0") ?? "unknown"} bytes.");
            }
            catch (Exception exception) { add("Enrollment", $"Pinned {artifact.Product} download", SystemCheckSeverity.Failure, Safe(exception.Message)); }
        }
    }

    private async Task CheckCoordinatorAsync(AdminConfig config, Action<string, string, SystemCheckSeverity, string> add, CancellationToken cancellationToken)
    {
        var expected = $"http://{config.CoordinatorBindAddress}:{config.CoordinatorPort}";
        var configured = AgentClient.IsTailscaleIp(config.CoordinatorBindAddress)
                         && config.CoordinatorUrl.TrimEnd('/').Equals(expected, StringComparison.OrdinalIgnoreCase);
        add("Coordinator", "Private bind configuration", configured ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            configured ? $"Coordinator is configured for {expected}." : $"Expected {expected}; saved URL is {config.CoordinatorUrl}.");
        if (!configured) return;
        try
        {
            using var response = await _http.GetAsync(expected + "/api/v1/registry", cancellationToken);
            var alive = response.StatusCode == HttpStatusCode.Unauthorized;
            add("Coordinator", "Local coordinator listener", alive ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                alive ? "The coordinator answered on the Tailscale address and correctly rejected an unauthenticated request." : $"The coordinator returned unexpected HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) { add("Coordinator", "Local coordinator listener", SystemCheckSeverity.Failure, Safe(exception.Message)); }
    }

    private static async Task<WindowsSnapshot?> ReadWindowsSnapshotAsync(AdminConfig config, string executablePath, CancellationToken cancellationToken)
    {
        var ip64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.CoordinatorBindAddress));
        var rust64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.ExpandEnvironmentVariables(config.RustDeskPath)));
        var exe64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(executablePath));
        var script = """
            $ErrorActionPreference='SilentlyContinue'
            $ip=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__IP64__'))
            $rust=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__RUST64__'))
            $exe=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__EXE64__'))
            $ts=Get-CimInstance Win32_Service -Filter "Name='Tailscale'"
            $rs=Get-CimInstance Win32_Service -Filter "Name='RustDesk'"
            $nordService=Get-CimInstance Win32_Service -Filter "Name='nordvpn-service'"
            $nordFile=Get-ChildItem (Join-Path $env:ProgramData 'NordVPN\settings\*.json')|Sort-Object LastWriteTime -Descending|Select-Object -First 1
            $nord=if($nordFile){(Get-Content -Raw -LiteralPath $nordFile.FullName|ConvertFrom-Json).SettingsDto}
            $nordDefault=[bool](Get-NetRoute -DestinationPrefix '0.0.0.0/0'|Where-Object{$_.InterfaceAlias -eq 'NordLynx'}|Select-Object -First 1)
            $coord=@(@('Opticon Coordinator (Tailscale only)','Taildesk Coordinator (Tailscale only)') | ForEach-Object { Get-NetFirewallRule -DisplayName $_ -ErrorAction SilentlyContinue } | Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow'} | ForEach-Object { $r=$_;$p=$r|Get-NetFirewallPortFilter;$a=$r|Get-NetFirewallAddressFilter;[pscustomobject]@{Name=$r.DisplayName;Protocol=$p.Protocol;Port=$p.LocalPort;Local=@($a.LocalAddress);Remote=@($a.RemoteAddress)} })
            $taskError=@()
            $task=Get-ScheduledTask -TaskName 'Taildesk Fly Route' -ErrorAction SilentlyContinue -ErrorVariable taskError
            $taskInfo=if($task){Get-ScheduledTaskInfo -TaskName 'Taildesk Fly Route'}
            $savedErrorPreference=$ErrorActionPreference
            $ErrorActionPreference='Continue'
            $taskQuery=@(& schtasks.exe /Query /TN 'Taildesk Fly Route' 2>&1);$taskExitCode=$LASTEXITCODE
            $ErrorActionPreference=$savedErrorPreference
            $taskProtected=($taskExitCode -ne 0 -and (($taskQuery -join ' ') -match 'Access is denied'))
            $helper=Join-Path $env:ProgramData 'Taildesk\Set-TaildeskFlyBypassRoute.ps1'
            $helperHash=if(Test-Path -LiteralPath $helper){(Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash}else{''}
            $route=Get-NetRoute -DestinationPrefix '__FLYIP__/32' | Where-Object {$_.Protocol -eq 'NetMgmt'} | Sort-Object RouteMetric | Select-Object -First 1
            $physical=if($route){Get-NetAdapter -InterfaceIndex $route.InterfaceIndex}
            function RuleOk([string]$name){ $r=Get-NetFirewallRule -DisplayName $name|Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Outbound' -and $_.Action -eq 'Block'}|Select-Object -First 1;if(-not $r){return $false};$app=$r|Get-NetFirewallApplicationFilter;return $app.Program -eq $rust }
            $inbound=@(Get-NetFirewallApplicationFilter -Program $rust -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow'})
            $shell=New-Object -ComObject WScript.Shell
            $startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'Opticon.lnk';$menu=Join-Path ([Environment]::GetFolderPath('Programs')) 'Opticon.lnk'
            $startupTarget=if(Test-Path $startup){$shell.CreateShortcut($startup).TargetPath}else{''};$menuTarget=if(Test-Path $menu){$shell.CreateShortcut($menu).TargetPath}else{''}
            [pscustomobject]@{
              TailscaleState=$ts.State;TailscaleStartMode=$ts.StartMode
              NordServiceState=$nordService.State;NordSplitEnabled=[bool]$nord.IsSplitTunnelingEnabled;NordSplitMode=[string]$nord.SplitTunnelingMode;NordSplitApps=@($nord.SplitTunnelingApps|ForEach-Object{$_.Path});NordDefaultRoutePresent=$nordDefault
              RustDeskState=$rs.State;RustDeskStartMode=$rs.StartMode;RustDeskProcessCount=@(Get-Process rustdesk).Count
              CoordinatorRules=$coord
              RouteTaskPresent=[bool]$task;RouteTaskProtected=$taskProtected;RouteHelperHash=$helperHash;RouteTaskState=[string]$task.State;RouteTaskUser=$task.Principal.UserId;RouteTaskRunLevel=[string]$task.Principal.RunLevel;RouteTaskTriggerCount=@($task.Triggers).Count;RouteTaskAction=(@($task.Actions|ForEach-Object{$_.Execute+' '+$_.Arguments})-join ' ');RouteTaskLastResult=$taskInfo.LastTaskResult
              RoutePresent=[bool]$route;RouteNextHop=$route.NextHop;RouteInterface=$physical.Name;RouteIsPhysical=[bool]$physical.HardwareInterface
              RustDeskV4Block=(RuleOk 'RustDesk External IPv4 Block');RustDeskV6Block=(RuleOk 'RustDesk External IPv6 Block');RustDeskInboundAllowCount=$inbound.Count
              FirewallProfilesEnabled=(@(Get-NetFirewallProfile|Where-Object{$_.Enabled -ne 'True'}).Count -eq 0)
              StartupTarget=$startupTarget;StartMenuTarget=$menuTarget;ExpectedExecutable=$exe
            }|ConvertTo-Json -Depth 6 -Compress
            """
            .Replace("__IP64__", ip64, StringComparison.Ordinal)
            .Replace("__RUST64__", rust64, StringComparison.Ordinal)
            .Replace("__EXE64__", exe64, StringComparison.Ordinal)
            .Replace("__FLYIP__", FlyDedicatedIpv4, StringComparison.Ordinal);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], TimeSpan.FromSeconds(45), cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        try { return JsonSerializer.Deserialize<WindowsSnapshot>(result.StandardOutput, JsonDefaults.Options); }
        catch { return null; }
    }

    private static void CheckWindowsSnapshot(AdminConfig config, WindowsSnapshot? snapshot,
        Action<string, string, SystemCheckSeverity, string> add)
    {
        if (snapshot is null)
        {
            add("Windows", "Local services and firewall", SystemCheckSeverity.Failure, "Windows configuration could not be inspected.");
            return;
        }
        add("Windows", "Tailscale service", snapshot.TailscaleState == "Running" && snapshot.TailscaleStartMode == "Auto" ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            $"Service state is {snapshot.TailscaleState ?? "missing"}; start mode is {snapshot.TailscaleStartMode ?? "missing"}.");

        var coordinatorRule = snapshot.CoordinatorRules.FirstOrDefault(rule => rule.Protocol == "TCP" && rule.Port == config.CoordinatorPort.ToString()
            && rule.Local.Contains(config.CoordinatorBindAddress) && rule.Remote.Any(IsTailnetRange));
        add("Firewall", "Coordinator isolation", coordinatorRule is not null ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            coordinatorRule is not null ? $"{coordinatorRule.Name} permits TCP {config.CoordinatorPort} only on the hub IP from the tailnet." : "The exact Tailscale-only coordinator firewall rule is missing or drifted.");

        var routeTaskVisibleAndValid = snapshot.RouteTaskPresent && snapshot.RouteTaskUser.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                             && snapshot.RouteTaskRunLevel.Equals("Highest", StringComparison.OrdinalIgnoreCase)
                             && snapshot.RouteTaskTriggerCount >= 3
                             && snapshot.RouteTaskAction.Contains("Set-TaildeskFlyBypassRoute.ps1", StringComparison.OrdinalIgnoreCase)
                             && snapshot.RouteTaskAction.Contains(FlyDedicatedIpv4, StringComparison.OrdinalIgnoreCase);
        var routeTaskValid = routeTaskVisibleAndValid || snapshot.RouteTaskProtected && snapshot.RouteHelperHash.Equals(RouteHelperSha256, StringComparison.OrdinalIgnoreCase);
        add("Network", "Roaming route maintenance", routeTaskValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            routeTaskVisibleAndValid ? $"SYSTEM task is {snapshot.RouteTaskState}; last result {snapshot.RouteTaskLastResult}." : routeTaskValid ? "The SYSTEM task is administrator-protected and its pinned helper is intact." : "The SYSTEM startup/sign-in/five-minute Fly route task is missing or has drifted.");
        add("Network", "Current Fly host route", snapshot.RoutePresent && snapshot.RouteIsPhysical ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.RoutePresent ? $"{FlyDedicatedIpv4}/32 uses {snapshot.RouteInterface} via {snapshot.RouteNextHop}." : "The dedicated Fly IPv4 route is missing.");
        var expectedNordApps = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscaled.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale-ipn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(AppPaths.InstallDirectory, "Admin", "Opticon.exe"),
            Path.Combine(AppPaths.InstallDirectory, "Admin", "Cli", "opticon.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "OpenSSH", "ssh.exe"),
            Environment.ExpandEnvironmentVariables(config.RustDeskPath)
        };
        var nordAppsValid = snapshot.NordSplitApps.Length == expectedNordApps.Length
                            && expectedNordApps.All(expected => snapshot.NordSplitApps.Any(actual => PathsEqual(actual, expected)));
        var nordValid = snapshot.NordServiceState == "Running" && snapshot.NordDefaultRoutePresent
                        && snapshot.NordSplitEnabled && snapshot.NordSplitMode.Equals("vpnDisabledForApps", StringComparison.OrdinalIgnoreCase)
                        && nordAppsValid;
        add("Network", "NordVPN and private mesh coexistence", nordValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            nordValid ? "NordVPN is the default route and excludes only Tailscale, the Opticon UI/CLI, Windows OpenSSH, and the pinned RustDesk controller."
                : "NordVPN must be running as the default route with split tunneling set to exclude exactly the three Tailscale executables, Opticon UI and CLI, the Windows OpenSSH client, and RustDesk.");


        var rustDeskPresent = File.Exists(Environment.ExpandEnvironmentVariables(config.RustDeskPath));
        var rustDeskVersion = rustDeskPresent ? FileVersionInfo.GetVersionInfo(Environment.ExpandEnvironmentVariables(config.RustDeskPath)).ProductVersion ?? string.Empty : string.Empty;
        var rustDeskVersionValid = rustDeskPresent && rustDeskVersion.StartsWith(DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture).Version, StringComparison.Ordinal);
        add("RustDesk", "Pinned controller engine", rustDeskVersionValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            rustDeskVersionValid ? $"Installed controller engine is {rustDeskVersion}." : "RustDesk is missing or is not the pinned version.");
        var controllerOnly = snapshot.RustDeskState == "Stopped" && snapshot.RustDeskStartMode == "Disabled" && snapshot.RustDeskInboundAllowCount == 0;
        add("RustDesk", "Controller-only posture", controllerOnly ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            controllerOnly ? "Hosting service is disabled and no inbound RustDesk allow rule exists." : $"Expected stopped/disabled with no inbound allow rule; state={snapshot.RustDeskState}, mode={snapshot.RustDeskStartMode}, inbound rules={snapshot.RustDeskInboundAllowCount}.");
        add("RustDesk", "External network blocks", snapshot.RustDeskV4Block && snapshot.RustDeskV6Block ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.RustDeskV4Block && snapshot.RustDeskV6Block ? "RustDesk is blocked from non-Tailscale IPv4 and all IPv6 destinations." : "One or both RustDesk outbound isolation rules are missing or drifted.");

        add("Firewall", "Windows Firewall profiles", snapshot.FirewallProfilesEnabled ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.FirewallProfilesEnabled ? "Domain, private, and public firewall profiles are enabled." : "At least one Windows Firewall profile is disabled.");
        var shortcutsValid = PathsEqual(snapshot.StartupTarget, snapshot.ExpectedExecutable) && PathsEqual(snapshot.StartMenuTarget, snapshot.ExpectedExecutable);
        add("Opticon", "Sign-in and Start Menu shortcuts", shortcutsValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Warning,
            shortcutsValid ? "Startup and Start Menu shortcuts target the running installed Opticon." : "A startup or Start Menu shortcut is missing or targets another executable.");
    }

    private static async Task<JsonDocument?> ReadJsonProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(executable, arguments, TimeSpan.FromSeconds(25), cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        try { return JsonDocument.Parse(result.StandardOutput); }
        catch { return null; }
    }

    private static bool IsTailnetRange(string value) => value.Equals("100.64.0.0/10", StringComparison.OrdinalIgnoreCase)
                                                         || value.Equals("100.64.0.0/255.192.0.0", StringComparison.OrdinalIgnoreCase);
    private static bool PathsEqual(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                                                                 && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string Safe(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim()[..Math.Min(value.Replace("\r", " ").Replace("\n", " ").Trim().Length, 300)];
    private static string GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static bool GetBool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static string[] GetStrings(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray() : [];

    private sealed class WindowsSnapshot
    {
        public string TailscaleState { get; set; } = string.Empty;
        public string TailscaleStartMode { get; set; } = string.Empty;
        public string NordServiceState { get; set; } = string.Empty;
        public bool NordSplitEnabled { get; set; }
        public string NordSplitMode { get; set; } = string.Empty;
        public string[] NordSplitApps { get; set; } = [];
        public bool NordDefaultRoutePresent { get; set; }
        public string RustDeskState { get; set; } = string.Empty;
        public string RustDeskStartMode { get; set; } = string.Empty;
        public int RustDeskProcessCount { get; set; }
        public FirewallRuleSnapshot[] CoordinatorRules { get; set; } = [];
        public bool RouteTaskPresent { get; set; }
        public bool RouteTaskProtected { get; set; }
        public string RouteHelperHash { get; set; } = string.Empty;
        public string RouteTaskState { get; set; } = string.Empty;
        public string RouteTaskUser { get; set; } = string.Empty;
        public string RouteTaskRunLevel { get; set; } = string.Empty;
        public int RouteTaskTriggerCount { get; set; }
        public string RouteTaskAction { get; set; } = string.Empty;
        public int? RouteTaskLastResult { get; set; }
        public bool RoutePresent { get; set; }
        public string RouteNextHop { get; set; } = string.Empty;
        public string RouteInterface { get; set; } = string.Empty;
        public bool RouteIsPhysical { get; set; }
        public bool RustDeskV4Block { get; set; }
        public bool RustDeskV6Block { get; set; }
        public int RustDeskInboundAllowCount { get; set; }
        public bool FirewallProfilesEnabled { get; set; }
        public string StartupTarget { get; set; } = string.Empty;
        public string StartMenuTarget { get; set; } = string.Empty;
        public string ExpectedExecutable { get; set; } = string.Empty;
    }

    private sealed class FirewallRuleSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;
        public string[] Local { get; set; } = [];
        public string[] Remote { get; set; } = [];
    }
}
