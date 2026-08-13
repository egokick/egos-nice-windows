using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
    private const string RouteTaskName = "Taildesk Fly Route";
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;
    private readonly string _executablePath;
    private readonly HttpClient _http = DirectHttp.CreateClient(TimeSpan.FromSeconds(25));

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
            try
            {
                var artifact = DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture);
                await VerifyVendorExecutableAsync(tailscalePath, artifact, cancellationToken);
                var version = await RunFixedProcessAsync(tailscalePath, ["version"], TimeSpan.FromSeconds(20), cancellationToken);
                var actualVersion = version.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
                var exactVersion = version.Succeeded && HasExactThreePartVersion(actualVersion, artifact.Version);
                Add("Tailscale", "Pinned client", exactVersion ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                    version.Succeeded ? $"Installed version is {actualVersion}; required version is {artifact.Version}." : "Tailscale version could not be read.");

                if (exactVersion)
                {
                    tailscaleStatus = await ReadJsonProcessAsync(tailscalePath, ["status", "--json"], cancellationToken);
                    tailscalePrefs = await ReadJsonProcessAsync(tailscalePath, ["debug", "prefs"], cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Add("Tailscale", "Pinned client", SystemCheckSeverity.Failure,
                    "The installed Tailscale client failed its fixed-path publisher check: " + Safe(exception.Message));
            }
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
            var snapshot = await ReadWindowsSnapshotAsync(config, cancellationToken);
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
            await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, FlyOrigin + "/health");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            add("Control plane", "Fly health", response.IsSuccessStatusCode ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                response.IsSuccessStatusCode ? "The private Opticon control service is healthy." : $"Fly health returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) { add("Control plane", "Fly health", SystemCheckSeverity.Failure, Safe(exception.Message)); }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.microsoft.com/favicon.ico");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, expected + "/api/v1/registry");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var alive = response.StatusCode == HttpStatusCode.Unauthorized;
            add("Coordinator", "Local coordinator listener", alive ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
                alive ? "The coordinator answered on the Tailscale address and correctly rejected an unauthenticated request." : $"The coordinator returned unexpected HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) { add("Coordinator", "Local coordinator listener", SystemCheckSeverity.Failure, Safe(exception.Message)); }
    }

    private static async Task<WindowsSnapshot?> ReadWindowsSnapshotAsync(AdminConfig config, CancellationToken cancellationToken)
    {
        var ip64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.CoordinatorBindAddress));
        var rust64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.ExpandEnvironmentVariables(config.RustDeskPath)));
        var script = """
            $ErrorActionPreference='SilentlyContinue'
            $ip=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__IP64__'))
            $rust=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__RUST64__'))
            $ts=Get-CimInstance Win32_Service -Filter "Name='Tailscale'"
            $rs=Get-CimInstance Win32_Service -Filter "Name='RustDesk'"
            $nordService=Get-CimInstance Win32_Service -Filter "Name='nordvpn-service'"
            $nordFile=Get-ChildItem (Join-Path $env:ProgramData 'NordVPN\settings\*.json')|Sort-Object LastWriteTime -Descending|Select-Object -First 1
            $nord=if($nordFile){(Get-Content -Raw -LiteralPath $nordFile.FullName|ConvertFrom-Json).SettingsDto}
            $nordDefault=[bool](Get-NetRoute -DestinationPrefix '0.0.0.0/0'|Where-Object{$_.InterfaceAlias -eq 'NordLynx'}|Select-Object -First 1)
            $coord=@(@('Opticon Coordinator (Tailscale only)','Taildesk Coordinator (Tailscale only)') | ForEach-Object { Get-NetFirewallRule -DisplayName $_ -ErrorAction SilentlyContinue } | Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow'} | ForEach-Object { $r=$_;$p=$r|Get-NetFirewallPortFilter;$a=$r|Get-NetFirewallAddressFilter;[pscustomobject]@{Name=$r.DisplayName;Protocol=$p.Protocol;Port=$p.LocalPort;Local=@($a.LocalAddress);Remote=@($a.RemoteAddress)} })
            $task=Get-ScheduledTask -TaskName 'Taildesk Fly Route' -ErrorAction SilentlyContinue -ErrorVariable taskError
            $taskInfo=if($task){Get-ScheduledTaskInfo -TaskName 'Taildesk Fly Route'}
            $taskXml64=''
            if($task){
              $taskXml=Export-ScheduledTask -TaskName 'Taildesk Fly Route'
              if($taskXml.Length -gt 131072){throw 'The route-task definition is unexpectedly large.'}
              $taskXml64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($taskXml))
            }
            $route=Get-NetRoute -DestinationPrefix '__FLYIP__/32' | Where-Object {$_.Protocol -eq 'NetMgmt'} | Sort-Object RouteMetric | Select-Object -First 1
            $physical=if($route){Get-NetAdapter -InterfaceIndex $route.InterfaceIndex}
            function RuleOk([string]$name){ $r=Get-NetFirewallRule -DisplayName $name|Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Outbound' -and $_.Action -eq 'Block'}|Select-Object -First 1;if(-not $r){return $false};$app=$r|Get-NetFirewallApplicationFilter;return $app.Program -eq $rust }
            $inbound=@(Get-NetFirewallApplicationFilter -Program $rust -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object {$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow'})
            $profiles=@(Get-NetFirewallProfile)
            [pscustomobject]@{
              TailscaleState=$ts.State;TailscaleStartMode=$ts.StartMode
              NordServiceState=$nordService.State;NordSplitEnabled=[bool]$nord.IsSplitTunnelingEnabled;NordSplitMode=[string]$nord.SplitTunnelingMode;NordSplitApps=@($nord.SplitTunnelingApps|Select-Object -First 32|ForEach-Object{$_.Path});NordDefaultRoutePresent=$nordDefault
              RustDeskState=$rs.State;RustDeskStartMode=$rs.StartMode;RustDeskProcessCount=@(Get-Process rustdesk).Count
              CoordinatorRules=$coord
              RouteTaskPresent=[bool]$task;RouteTaskXmlBase64=$taskXml64;RouteTaskState=[string]$task.State;RouteTaskLastResult=$taskInfo.LastTaskResult
              RoutePresent=[bool]$route;RouteNextHop=$route.NextHop;RouteInterface=$physical.Name;RouteIsPhysical=[bool]$physical.HardwareInterface
              RustDeskV4Block=(RuleOk 'RustDesk External IPv4 Block');RustDeskV6Block=(RuleOk 'RustDesk External IPv6 Block');RustDeskInboundAllowCount=$inbound.Count
              FirewallProfilesEnabledAndBlocking=(@($profiles|Where-Object{$_.Enabled -ne 'True' -or $_.DefaultInboundAction -ne 'Block'}).Count -eq 0)
            }|ConvertTo-Json -Depth 6 -Compress
            """
            .Replace("__IP64__", ip64, StringComparison.Ordinal)
            .Replace("__RUST64__", rust64, StringComparison.Ordinal)
            .Replace("__FLYIP__", FlyDedicatedIpv4, StringComparison.Ordinal);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await ProcessRunner.RunAsync(
            RequireSystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(45), cancellationToken,
            environment: BuildSystemProcessEnvironment(), clearEnvironment: true);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<WindowsSnapshot>(result.StandardOutput, JsonDefaults.Options);
            if (snapshot is null) return null;

            snapshot.RouteHelperPath = Path.Combine(AppPaths.InstallDirectory, "Admin", "Tools", "Taildesk.RouteKeeper.exe");
            try
            {
                await ProductSigning.VerifyAuthenticodeAsync(snapshot.RouteHelperPath, cancellationToken);
                snapshot.RouteHelperTrusted = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                snapshot.RouteHelperTrusted = false;
            }

            var rustDeskPath = Environment.ExpandEnvironmentVariables(config.RustDeskPath);
            snapshot.RustDeskPath = rustDeskPath;
            if (File.Exists(rustDeskPath))
            {
                try
                {
                    var artifact = DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture);
                    await VerifyVendorExecutableAsync(rustDeskPath, artifact, cancellationToken);
                    snapshot.RustDeskVersion = FileVersionInfo.GetVersionInfo(rustDeskPath).ProductVersion ?? string.Empty;
                    snapshot.RustDeskTrusted = HasExactThreePartVersion(snapshot.RustDeskVersion, artifact.Version);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    snapshot.RustDeskTrusted = false;
                }
            }
            return snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
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

        var routeTaskValid = snapshot.RouteTaskPresent
                             && snapshot.RouteHelperTrusted
                             && IsExactRouteTask(snapshot.RouteTaskXmlBase64, snapshot.RouteHelperPath);
        add("Network", "Roaming route maintenance", routeTaskValid ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            routeTaskValid ? $"The exact signed RouteKeeper SYSTEM task is {snapshot.RouteTaskState}; last result {snapshot.RouteTaskLastResult}." : "The exact signed RouteKeeper SYSTEM startup/sign-in/five-minute task is missing, unreadable, untrusted, or has drifted.");
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
        add("RustDesk", "Pinned controller engine", snapshot.RustDeskTrusted ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.RustDeskTrusted ? $"Installed controller engine is {snapshot.RustDeskVersion} with the pinned publisher." : "RustDesk is missing, outside its fixed Program Files path, not exactly the pinned version, or failed its publisher check.");
        var controllerOnly = snapshot.RustDeskState == "Stopped" && snapshot.RustDeskStartMode == "Disabled" && snapshot.RustDeskInboundAllowCount == 0;
        add("RustDesk", "Controller-only posture", controllerOnly ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            controllerOnly ? "Hosting service is disabled and no inbound RustDesk allow rule exists." : $"Expected stopped/disabled with no inbound allow rule; state={snapshot.RustDeskState}, mode={snapshot.RustDeskStartMode}, inbound rules={snapshot.RustDeskInboundAllowCount}.");
        add("RustDesk", "External network blocks", snapshot.RustDeskV4Block && snapshot.RustDeskV6Block ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.RustDeskV4Block && snapshot.RustDeskV6Block ? "RustDesk is blocked from non-Tailscale IPv4 and all IPv6 destinations." : "One or both RustDesk outbound isolation rules are missing or drifted.");

        add("Firewall", "Windows Firewall profiles", snapshot.FirewallProfilesEnabledAndBlocking ? SystemCheckSeverity.Pass : SystemCheckSeverity.Failure,
            snapshot.FirewallProfilesEnabledAndBlocking ? "Domain, private, and public firewall profiles are enabled with default inbound blocking." : "At least one Windows Firewall profile is disabled or does not default to blocking inbound traffic.");
    }

    private static async Task<JsonDocument?> ReadJsonProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunFixedProcessAsync(executable, arguments, TimeSpan.FromSeconds(25), cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        try { return JsonDocument.Parse(result.StandardOutput); }
        catch { return null; }
    }

    private static Task<ProcessResult> RunFixedProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            executable, arguments, timeout, cancellationToken,
            environment: BuildSystemProcessEnvironment(), clearEnvironment: true);

    private static async Task VerifyVendorExecutableAsync(
        string path,
        DependencyArtifact artifact,
        CancellationToken cancellationToken)
    {
        var fixedPath = RequireFixedExecutable(
            path,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!PathsEqual(fixedPath, path))
            throw new InvalidDataException($"The {artifact.Product} executable path is not canonical.");

        var path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixedPath));
        var command =
            "$ErrorActionPreference='Stop';" +
            "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + path64 + "'));" +
            "$s=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $p;" +
            "if($s.Status.ToString() -cne 'Valid' -or $null -eq $s.SignerCertificate -or $null -eq $s.TimeStamperCertificate){exit 41};" +
            "$codeEku=@($s.SignerCertificate.EnhancedKeyUsageList|Where-Object{$_.ObjectId -eq '1.3.6.1.5.5.7.3.3'});" +
            "$timeEku=@($s.TimeStamperCertificate.EnhancedKeyUsageList|Where-Object{$_.ObjectId -eq '1.3.6.1.5.5.7.3.8'});" +
            "if($codeEku.Count -ne 1 -or $timeEku.Count -ne 1){exit 42};" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint.ToUpperInvariant())";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = await ProcessRunner.RunAsync(
            RequireSystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(45), cancellationToken,
            environment: BuildSystemProcessEnvironment(), clearEnvironment: true);
        var actual = result.StandardOutput.Trim().ToUpperInvariant();
        var expected = artifact.ExpectedSignerThumbprint.ToUpperInvariant();
        if (!result.Succeeded || actual.Length != 40 || expected.Length != 40
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
            throw new InvalidDataException($"The {artifact.Product} executable does not have its pinned, trusted, timestamped publisher signature.");
    }

    private static string RequireSystemExecutable(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(component => component is "" or "." or ".." || component.Contains(':')))
            throw new InvalidDataException("A Windows system-tool path was not canonical.");
        var system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        return RequireFixedExecutable(Path.Combine(system32, relativePath), system32);
    }

    private static string RequireFixedExecutable(string path, string allowedRoot)
    {
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(component => component is "" or "." or ".." || component.Contains(':')))
            throw new InvalidDataException("A fixed executable escaped its protected root.");

        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"A fixed executable path is a reparse point: {current}");
            current = Path.Combine(current, component);
        }
        var finalAttributes = File.GetAttributes(fullPath);
        if ((finalAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"A fixed executable is not a regular file: {fullPath}");
        return fullPath;
    }

    private static IReadOnlyDictionary<string, string?> BuildSystemProcessEnvironment()
    {
        var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var system32 = Path.Combine(windows, "System32");
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)?.TrimEnd(Path.DirectorySeparatorChar),
            ["ComSpec"] = RequireSystemExecutable("cmd.exe"),
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["PROCESSOR_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "AMD64",
                Architecture.Arm64 => "ARM64",
                Architecture.X86 => "x86",
                _ => throw new PlatformNotSupportedException("Windows process architecture is unsupported.")
            },
            ["PATH"] = string.Join(Path.PathSeparator,
                system32,
                Path.Combine(system32, "Wbem"),
                Path.Combine(system32, @"WindowsPowerShell\v1.0")),
            ["PATHEXT"] = ".COM;.EXE",
            ["PSModulePath"] = Path.Combine(system32, @"WindowsPowerShell\v1.0\Modules"),
            ["TEMP"] = Path.Combine(windows, "Temp"),
            ["TMP"] = Path.Combine(windows, "Temp")
        };
    }

    private static bool HasExactThreePartVersion(string actual, string expected)
    {
        if (!Version.TryParse(actual.Trim(), out var actualVersion)
            || !Version.TryParse(expected, out var expectedVersion)) return false;
        return actualVersion.Major == expectedVersion.Major
               && actualVersion.Minor == expectedVersion.Minor
               && actualVersion.Build == expectedVersion.Build
               && actualVersion.Revision <= 0;
    }

    private static bool IsExactRouteTask(string xmlBase64, string expectedHelper)
    {
        try
        {
            var bytes = Convert.FromBase64String(xmlBase64);
            if (bytes.Length is 0 or > 131072) return false;
            var document = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.None);
            XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = document.Root;
            if (root?.Name != task + "Task") return false;
            var actions = root.Element(task + "Actions");
            var principals = root.Element(task + "Principals");
            var triggers = root.Element(task + "Triggers");
            var settings = root.Element(task + "Settings");
            if (actions is null || principals is null || triggers is null || settings is null) return false;

            var actionNodes = actions.Elements().ToArray();
            var principalNodes = principals.Elements(task + "Principal").ToArray();
            var triggerNodes = triggers.Elements().ToArray();
            if (actionNodes.Length != 1 || actionNodes[0].Name != task + "Exec"
                || principalNodes.Length != 1 || principals.Elements().Count() != 1
                || triggerNodes.Length != 3
                || triggerNodes.Count(node => node.Name == task + "BootTrigger") != 1
                || triggerNodes.Count(node => node.Name == task + "LogonTrigger") != 1
                || triggerNodes.Count(node => node.Name == task + "TimeTrigger") != 1)
                return false;

            string Value(XElement parent, string name) => parent.Element(task + name)?.Value ?? string.Empty;
            var exec = actionNodes[0];
            var principal = principalNodes[0];
            var time = triggerNodes.Single(node => node.Name == task + "TimeTrigger");
            var repetition = time.Element(task + "Repetition");
            return actions.Attribute("Context")?.Value == "Author"
                   && PathsEqual(Value(exec, "Command"), expectedHelper)
                   && Value(exec, "Arguments") == $"--controller-ip={FlyDedicatedIpv4}"
                   && Value(principal, "UserId") == "S-1-5-18"
                   // Canonical exports omit LogonType. Tolerate the legacy API
                   // spelling when inspecting an already-registered task, but
                   // never generate or re-import that invalid XML value.
                   && (Value(principal, "LogonType").Length == 0
                       || Value(principal, "LogonType") == "ServiceAccount")
                   && Value(principal, "RunLevel") == "HighestAvailable"
                   && triggerNodes.All(node => Value(node, "Enabled") == "true")
                   && repetition is not null
                   && Value(repetition, "Interval") == "PT5M"
                   && repetition.Element(task + "Duration") is null
                   && Value(settings, "MultipleInstancesPolicy") == "IgnoreNew"
                   && Value(settings, "DisallowStartIfOnBatteries") == "false"
                   && Value(settings, "StopIfGoingOnBatteries") == "false"
                   && Value(settings, "AllowStartOnDemand") == "true"
                   && Value(settings, "Enabled") == "true"
                   && Value(settings, "RunOnlyIfNetworkAvailable") == "false"
                   && Value(settings, "StartWhenAvailable") == "true"
                   && Value(settings, "ExecutionTimeLimit") == "PT0S";
        }
        catch
        {
            return false;
        }
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
        public string RustDeskPath { get; set; } = string.Empty;
        public string RustDeskVersion { get; set; } = string.Empty;
        public bool RustDeskTrusted { get; set; }
        public FirewallRuleSnapshot[] CoordinatorRules { get; set; } = [];
        public bool RouteTaskPresent { get; set; }
        public string RouteTaskXmlBase64 { get; set; } = string.Empty;
        public string RouteHelperPath { get; set; } = string.Empty;
        public bool RouteHelperTrusted { get; set; }
        public string RouteTaskState { get; set; } = string.Empty;
        public int? RouteTaskLastResult { get; set; }
        public bool RoutePresent { get; set; }
        public string RouteNextHop { get; set; } = string.Empty;
        public string RouteInterface { get; set; } = string.Empty;
        public bool RouteIsPhysical { get; set; }
        public bool RustDeskV4Block { get; set; }
        public bool RustDeskV6Block { get; set; }
        public int RustDeskInboundAllowCount { get; set; }
        public bool FirewallProfilesEnabledAndBlocking { get; set; }
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
