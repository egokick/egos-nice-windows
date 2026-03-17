using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var port = ResolvePort(args);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton(new NetworkProbe(port));

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

app.MapGet("/", () => Results.Content(PageAssets.HtmlPage, "text/html; charset=utf-8"));
app.MapGet("/api/network-info", (NetworkProbe probe) => Results.Json(probe.GetSnapshot()));
app.MapPost("/api/hotspot/start", (NetworkProbe probe) =>
{
    var result = probe.SetHotspotEnabled(enable: true);
    return Results.Json(result, statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});
app.MapPost("/api/hotspot/stop", (NetworkProbe probe) =>
{
    var result = probe.SetHotspotEnabled(enable: false);
    return Results.Json(result, statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});
app.MapGet("/healthz", () => Results.Text("ok", "text/plain; charset=utf-8"));

var startupSnapshot = app.Services.GetRequiredService<NetworkProbe>().GetSnapshot();
Console.WriteLine($"Hotspot Phone Demo listening on port {port}");
Console.WriteLine($"Hotspot SSID: {startupSnapshot.HotspotSsid ?? "(not detected)"}");
Console.WriteLine($"Hotspot state: {startupSnapshot.HotspotState}");
Console.WriteLine("Candidate phone URLs:");
foreach (var candidate in startupSnapshot.CandidateUrls)
{
    Console.WriteLine($"  {candidate.Url} [{candidate.InterfaceName}]");
}

await app.RunAsync();

static int ResolvePort(string[] args)
{
    foreach (var arg in args)
    {
        if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(arg["--port=".Length..], out var cliPort)
            && cliPort is > 0 and < 65536)
        {
            return cliPort;
        }
    }

    var portEnv = Environment.GetEnvironmentVariable("HOTSPOT_PHONE_DEMO_PORT");
    if (int.TryParse(portEnv, out var envPort) && envPort is > 0 and < 65536)
    {
        return envPort;
    }

    return 48211;
}

internal sealed class NetworkProbe
{
    private readonly int _port;

    public NetworkProbe(int port)
    {
        _port = port;
    }

    public NetworkSnapshot GetSnapshot()
    {
        var hotspot = HotspotInfo.TryRead();
        var candidates = GetCandidateUrls(_port);
        var recommendedUrl = candidates.FirstOrDefault()?.Url ?? $"http://localhost:{_port}/";
        var instruction = BuildInstruction(hotspot, recommendedUrl);

        return new NetworkSnapshot(
            MachineName: Environment.MachineName,
            Port: _port,
            HotspotState: hotspot.State,
            HotspotSsid: hotspot.Ssid,
            MaxClients: hotspot.MaxClients,
            CurrentWifiName: hotspot.CurrentWifiName,
            RecommendedUrl: recommendedUrl,
            Instruction: instruction,
            CandidateUrls: candidates,
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    public HotspotActionResponse SetHotspotEnabled(bool enable)
    {
        var targetState = enable ? "On" : "Off";
        try
        {
            var before = GetSnapshot();
            if (string.Equals(before.HotspotState, targetState, StringComparison.OrdinalIgnoreCase))
            {
                return new HotspotActionResponse(
                    Success: true,
                    Message: enable
                        ? $"Hotspot is already on{FormatSsidSuffix(before.HotspotSsid)}."
                        : "Hotspot is already off.",
                    Snapshot: before);
            }

            HotspotInfo.RequestState(enable);
            var after = WaitForState(targetState, TimeSpan.FromSeconds(20));
            var success = string.Equals(after.HotspotState, targetState, StringComparison.OrdinalIgnoreCase);
            return new HotspotActionResponse(
                Success: success,
                Message: BuildToggleMessage(enable, success, after),
                Snapshot: after);
        }
        catch (Exception ex)
        {
            var snapshot = GetSnapshot();
            return new HotspotActionResponse(
                Success: false,
                Message: $"{(enable ? "Could not start" : "Could not stop")} hotspot. {ex.Message}",
                Snapshot: snapshot);
        }
    }

    private NetworkSnapshot WaitForState(string targetState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var snapshot = GetSnapshot();
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(snapshot.HotspotState, targetState, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot;
            }

            Thread.Sleep(500);
            snapshot = GetSnapshot();
        }

        return snapshot;
    }

    private static string BuildToggleMessage(bool enable, bool success, NetworkSnapshot snapshot)
    {
        if (success)
        {
            return enable
                ? $"Hotspot started{FormatSsidSuffix(snapshot.HotspotSsid)}. Open {snapshot.RecommendedUrl} from the phone."
                : "Hotspot stopped.";
        }

        return $"Windows did not switch hotspot to the requested state. Current state: {snapshot.HotspotState}.";
    }

    private static string FormatSsidSuffix(string? ssid)
    {
        return string.IsNullOrWhiteSpace(ssid) ? string.Empty : $" on '{ssid}'";
    }

    private static string BuildInstruction(HotspotInfo hotspot, string recommendedUrl)
    {
        if (!string.IsNullOrWhiteSpace(hotspot.Ssid) && string.Equals(hotspot.State, "On", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connect the phone to hotspot SSID '{hotspot.Ssid}', then open {recommendedUrl}.";
        }

        if (!string.IsNullOrWhiteSpace(hotspot.Ssid) && !string.IsNullOrWhiteSpace(hotspot.CurrentWifiName))
        {
            return $"Hotspot is currently off. For plane use, turn on Windows Mobile Hotspot and connect the phone to '{hotspot.Ssid}'. For an immediate test right now, connect the phone to Wi-Fi '{hotspot.CurrentWifiName}' and open {recommendedUrl}.";
        }

        if (!string.IsNullOrWhiteSpace(hotspot.CurrentWifiName))
        {
            return $"Connect the phone to Wi-Fi '{hotspot.CurrentWifiName}', then open {recommendedUrl}.";
        }

        if (!string.IsNullOrWhiteSpace(hotspot.Ssid))
        {
            return $"Turn on Windows Mobile Hotspot, connect the phone to '{hotspot.Ssid}', then open {recommendedUrl}.";
        }

        return $"Connect the phone to the same network as this laptop, then open {recommendedUrl}.";
    }

    private static List<UrlCandidate> GetCandidateUrls(int port)
    {
        var candidates = new List<(int Score, UrlCandidate Candidate)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(address.Address) || IsLinkLocal(address.Address))
                {
                    continue;
                }

                if (!IsPrivateAddress(address.Address))
                {
                    continue;
                }

                var url = $"http://{address.Address}:{port}/";
                candidates.Add((
                    Score: GetPriorityScore(nic, address.Address),
                    Candidate: new UrlCandidate(
                        InterfaceName: nic.Name,
                        InterfaceType: nic.NetworkInterfaceType.ToString(),
                        Address: address.Address.ToString(),
                        Url: url)));
            }
        }

        return candidates
            .OrderBy(entry => entry.Score)
            .ThenBy(entry => entry.Candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Candidate.Address, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Candidate)
            .DistinctBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetPriorityScore(NetworkInterface nic, IPAddress address)
    {
        var name = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        if (address.ToString() == "192.168.137.1")
        {
            return 0;
        }

        if (name.Contains("wi-fi direct", StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.Contains("local area connection", StringComparison.Ordinal))
        {
            return 2;
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || name.Contains("wi-fi", StringComparison.Ordinal))
        {
            return 3;
        }

        if (name.Contains("bluetooth", StringComparison.Ordinal))
        {
            return 4;
        }

        return 5;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}

internal sealed record NetworkSnapshot(
    string MachineName,
    int Port,
    string HotspotState,
    string? HotspotSsid,
    int? MaxClients,
    string? CurrentWifiName,
    string RecommendedUrl,
    string Instruction,
    IReadOnlyList<UrlCandidate> CandidateUrls,
    DateTimeOffset GeneratedAtUtc);

internal sealed record UrlCandidate(
    string InterfaceName,
    string InterfaceType,
    string Address,
    string Url);

internal sealed record HotspotActionResponse(
    bool Success,
    string Message,
    NetworkSnapshot Snapshot);

internal sealed record HotspotInfo(
    string State,
    string? Ssid,
    int? MaxClients,
    string? CurrentWifiName)
{
    public static HotspotInfo TryRead()
    {
        const string script = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Windows.Networking.Connectivity.NetworkInformation,Windows,ContentType=WindowsRuntime] > $null
[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows,ContentType=WindowsRuntime] > $null

$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
if ($null -eq $profile) {
  $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Select-Object -First 1
}

$state = 'Unknown'
$ssid = $null
$maxClients = $null
if ($null -ne $profile) {
  try {
    $manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
    $state = $manager.TetheringOperationalState.ToString()
    $config = $manager.GetCurrentAccessPointConfiguration()
    $ssid = $config.Ssid
    $maxClients = $manager.MaxClientCount
  } catch {
  }
}

$currentWifi = $null
try {
  $currentWifi = Get-NetConnectionProfile |
    Where-Object { $_.InterfaceAlias -like 'Wi-Fi*' } |
    Select-Object -First 1 -ExpandProperty Name
} catch {
}

[pscustomobject]@{
  state = $state
  ssid = $ssid
  maxClients = $maxClients
  currentWifiName = $currentWifi
} | ConvertTo-Json -Compress
""";

        try
        {
            var output = RunPowerShell(script);
            var payload = JsonSerializer.Deserialize<HotspotPayload>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return new HotspotInfo(
                State: string.IsNullOrWhiteSpace(payload?.State) ? "Unknown" : payload.State.Trim(),
                Ssid: string.IsNullOrWhiteSpace(payload?.Ssid) ? null : payload.Ssid.Trim(),
                MaxClients: payload?.MaxClients,
                CurrentWifiName: string.IsNullOrWhiteSpace(payload?.CurrentWifiName) ? null : payload.CurrentWifiName.Trim());
        }
        catch
        {
            return new HotspotInfo("Unknown", null, null, null);
        }
    }

    public static void RequestState(bool enable)
    {
        var action = enable ? "start" : "stop";
        var script = string.Join(Environment.NewLine,
        [
            "$ErrorActionPreference = 'Stop'",
            "$ProgressPreference = 'SilentlyContinue'",
            "[Windows.Networking.Connectivity.NetworkInformation,Windows,ContentType=WindowsRuntime] > $null",
            "[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows,ContentType=WindowsRuntime] > $null",
            string.Empty,
            "$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()",
            "if ($null -eq $profile) {",
            "  throw 'No internet connection profile is available for hotspot control.'",
            "}",
            string.Empty,
            "$manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)",
            $"if ('{action}' -eq 'start') {{",
            "  $manager.StartTetheringAsync() | Out-Null",
            "} else {",
            "  $manager.StopTetheringAsync() | Out-Null",
            "}",
            string.Empty,
            "[pscustomobject]@{ ok = $true } | ConvertTo-Json -Compress"
        ]);

        _ = RunPowerShell(script);
    }

    private static string RunPowerShell(string script)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                Arguments = $"-NoProfile -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start PowerShell.");
        }

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stdErr) ? "PowerShell command failed." : stdErr.Trim());
        }

        var jsonLine = stdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new InvalidOperationException("PowerShell command did not return JSON.");
        }

        return jsonLine;
    }

    private sealed class HotspotPayload
    {
        public string? State { get; set; }

        public string? Ssid { get; set; }

        public int? MaxClients { get; set; }

        public string? CurrentWifiName { get; set; }
    }
}

internal static class PageAssets
{
    public const string HtmlPage = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Phone LAN Test</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0d1320;
      --panel: rgba(18, 27, 44, 0.9);
      --panel-strong: rgba(24, 37, 62, 0.96);
      --text: #ecf4ff;
      --muted: #a9bddc;
      --line: rgba(169, 189, 220, 0.16);
      --accent: #78f0c7;
      --accent-soft: rgba(120, 240, 199, 0.14);
      --warn: #ffd27d;
      --danger: #ff8f9b;
      --shadow: 0 28px 80px rgba(0, 0, 0, 0.32);
    }

    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
      color: var(--text);
      background:
        radial-gradient(circle at top left, rgba(120, 240, 199, 0.12), transparent 28%),
        radial-gradient(circle at top right, rgba(90, 146, 255, 0.18), transparent 24%),
        linear-gradient(180deg, #0d1320 0%, #111b2e 100%);
    }

    .shell {
      width: min(980px, calc(100vw - 28px));
      margin: 18px auto 40px;
      display: grid;
      gap: 16px;
    }

    .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 26px;
      box-shadow: var(--shadow);
      backdrop-filter: blur(16px);
      padding: 20px;
    }

    .hero {
      display: grid;
      gap: 10px;
    }

    .eyebrow {
      margin: 0;
      color: var(--muted);
      letter-spacing: 0.12em;
      text-transform: uppercase;
      font-size: 0.74rem;
      font-weight: 700;
    }

    h1, h2, p { margin: 0; }
    h1 {
      font-size: clamp(1.8rem, 4vw, 3rem);
      line-height: 1.04;
    }

    .hero-copy {
      color: var(--muted);
      line-height: 1.6;
      max-width: 68ch;
    }

    .status-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 14px;
    }

    .tile {
      display: grid;
      gap: 8px;
      padding: 16px;
      border: 1px solid var(--line);
      border-radius: 20px;
      background: var(--panel-strong);
    }

    .tile-label {
      color: var(--muted);
      font-size: 0.76rem;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      font-weight: 700;
    }

    .tile-value {
      font-size: 1.15rem;
      font-weight: 700;
      word-break: break-word;
    }

    .tile-subtle {
      color: var(--muted);
      line-height: 1.45;
      font-size: 0.95rem;
    }

    .url-card {
      display: grid;
      gap: 12px;
      padding: 18px;
      border: 1px solid rgba(120, 240, 199, 0.22);
      border-radius: 22px;
      background: linear-gradient(135deg, var(--accent-soft), rgba(120, 240, 199, 0.03));
    }

    .url-big {
      font-family: "Cascadia Mono", "Consolas", monospace;
      font-size: clamp(1rem, 2.4vw, 1.4rem);
      font-weight: 700;
      line-height: 1.4;
      word-break: break-all;
    }

    .hint {
      color: var(--muted);
      line-height: 1.5;
    }

    .control-row {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      margin-top: 14px;
    }

    .action-button {
      min-height: 44px;
      padding: 0 18px;
      border: 1px solid var(--line);
      border-radius: 999px;
      color: var(--text);
      background: rgba(255, 255, 255, 0.05);
      font: inherit;
      font-weight: 700;
      cursor: pointer;
    }

    .action-button.primary {
      border-color: rgba(120, 240, 199, 0.28);
      background: linear-gradient(135deg, rgba(120, 240, 199, 0.2), rgba(120, 240, 199, 0.08));
    }

    .action-button.secondary {
      border-color: rgba(255, 143, 155, 0.24);
      background: linear-gradient(135deg, rgba(255, 143, 155, 0.18), rgba(255, 143, 155, 0.06));
    }

    .action-button:disabled {
      opacity: 0.52;
      cursor: default;
    }

    .list {
      display: grid;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .list-item {
      display: grid;
      gap: 6px;
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 18px;
      background: rgba(255, 255, 255, 0.03);
    }

    .pill {
      display: inline-flex;
      align-items: center;
      min-height: 30px;
      width: fit-content;
      padding: 0 12px;
      border-radius: 999px;
      font-size: 0.84rem;
      font-weight: 700;
      background: rgba(255, 210, 125, 0.16);
      color: var(--warn);
    }

    .pill.ok {
      background: rgba(120, 240, 199, 0.16);
      color: var(--accent);
    }

    .pill.offline {
      background: rgba(255, 143, 155, 0.16);
      color: var(--danger);
    }

    .fine-print {
      color: var(--muted);
      font-size: 0.9rem;
      line-height: 1.55;
    }

    @media (max-width: 700px) {
      .shell { width: min(100vw - 18px, 980px); }
      .panel { padding: 16px; border-radius: 22px; }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="panel hero">
      <p class="eyebrow">Laptop To Phone Check</p>
      <h1>Phone LAN Test Page</h1>
      <p class="hero-copy">
        Use this page to verify another device can reach this laptop over the current Wi-Fi network or Windows Mobile Hotspot.
        Open this page on the laptop first, then connect the phone to the shown SSID and open the shown URL.
      </p>
    </section>

    <section class="panel">
      <div class="status-grid">
        <div class="tile">
          <span class="tile-label">Hotspot SSID</span>
          <span id="ssidValue" class="tile-value">Loading...</span>
          <span id="hotspotState" class="pill">Checking...</span>
        </div>
        <div class="tile">
          <span class="tile-label">Current Wi-Fi</span>
          <span id="wifiValue" class="tile-value">Loading...</span>
          <span id="maxClients" class="tile-subtle"></span>
        </div>
        <div class="tile">
          <span class="tile-label">What To Do</span>
          <span id="instructionValue" class="tile-subtle">Loading...</span>
        </div>
      </div>
      <div class="control-row">
        <button id="startHotspotButton" class="action-button primary" type="button">Start Hotspot</button>
        <button id="stopHotspotButton" class="action-button secondary" type="button">Stop Hotspot</button>
      </div>
      <p id="actionStatus" class="fine-print">Use these controls to make the laptop host its own SSID.</p>
    </section>

    <section class="panel">
      <div class="url-card">
        <span class="tile-label">Phone URL To Open</span>
        <div id="recommendedUrl" class="url-big">Loading...</div>
        <p class="hint">
          If this page is already open on the phone, the browser address bar should match one of the candidate URLs below.
        </p>
      </div>
    </section>

    <section class="panel">
      <p class="eyebrow">Candidate URLs</p>
      <ul id="candidateUrls" class="list">
        <li class="list-item">Loading...</li>
      </ul>
    </section>

    <section class="panel">
      <p class="eyebrow">Live Result</p>
      <h2 id="liveResult">Checking current browser...</h2>
      <p id="liveDetail" class="fine-print"></p>
      <p id="refreshStamp" class="fine-print"></p>
    </section>
  </main>

  <script>
    const elements = {
      actionStatus: document.getElementById("actionStatus"),
      ssidValue: document.getElementById("ssidValue"),
      hotspotState: document.getElementById("hotspotState"),
      wifiValue: document.getElementById("wifiValue"),
      maxClients: document.getElementById("maxClients"),
      instructionValue: document.getElementById("instructionValue"),
      recommendedUrl: document.getElementById("recommendedUrl"),
      candidateUrls: document.getElementById("candidateUrls"),
      liveResult: document.getElementById("liveResult"),
      liveDetail: document.getElementById("liveDetail"),
      refreshStamp: document.getElementById("refreshStamp"),
      startHotspotButton: document.getElementById("startHotspotButton"),
      stopHotspotButton: document.getElementById("stopHotspotButton"),
    };

    const state = {
      hotspotAction: "",
      hotspotActionInFlight: false,
      info: null,
    };

    async function refresh() {
      try {
        const response = await fetch("/api/network-info", {
          cache: "no-store",
          headers: { "Accept": "application/json" },
        });
        if (!response.ok) {
          throw new Error("Could not load network details.");
        }

        const info = await response.json();
        render(info);
      } catch (error) {
        const message = error instanceof Error ? error.message : "Could not load network details.";
        elements.ssidValue.textContent = "Unavailable";
        elements.hotspotState.textContent = "Error";
        elements.hotspotState.className = "pill offline";
        elements.wifiValue.textContent = "Unavailable";
        elements.maxClients.textContent = "";
        elements.instructionValue.textContent = message;
        elements.recommendedUrl.textContent = window.location.href;
        elements.candidateUrls.innerHTML = "<li class=\"list-item\">The network details could not be loaded.</li>";
        renderLiveResult(null);
      }
    }

    function render(info) {
      state.info = info;
      elements.ssidValue.textContent = info.hotspotSsid || "Not detected";
      elements.wifiValue.textContent = info.currentWifiName || "No active Wi-Fi profile";
      elements.maxClients.textContent = Number.isInteger(info.maxClients)
        ? `Windows hotspot allows up to ${info.maxClients} clients.`
        : "";
      elements.instructionValue.textContent = info.instruction || "Connect the phone to the same network and try the URL below.";
      elements.recommendedUrl.textContent = info.recommendedUrl || window.location.href;

      const state = (info.hotspotState || "Unknown").toLowerCase();
      elements.hotspotState.textContent = info.hotspotState || "Unknown";
      elements.hotspotState.className =
        state === "on"
          ? "pill ok"
          : state === "off"
            ? "pill offline"
            : "pill";

      elements.candidateUrls.replaceChildren();
      const candidates = Array.isArray(info.candidateUrls) ? info.candidateUrls : [];
      if (candidates.length === 0) {
        const item = document.createElement("li");
        item.className = "list-item";
        item.textContent = "No private LAN addresses were detected on this laptop right now.";
        elements.candidateUrls.append(item);
      } else {
        for (const candidate of candidates) {
          const item = document.createElement("li");
          item.className = "list-item";

          const url = document.createElement("div");
          url.className = "url-big";
          url.textContent = candidate.url;

          const meta = document.createElement("div");
          meta.className = "fine-print";
          meta.textContent = `${candidate.interfaceName} (${candidate.interfaceType}) - ${candidate.address}`;

          item.append(url, meta);
          elements.candidateUrls.append(item);
        }
      }

      elements.refreshStamp.textContent =
        `Refreshed ${new Date(info.generatedAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })}`;
      updateHotspotButtons();
      renderLiveResult(info);
    }

    function updateHotspotButtons() {
      const stateValue = String(state.info?.hotspotState || "Unknown").toLowerCase();
      const busy = state.hotspotActionInFlight;
      const canStart = !busy && stateValue !== "on" && stateValue !== "intransition";
      const canStop = !busy && stateValue !== "off" && stateValue !== "unknown" && stateValue !== "intransition";
      elements.startHotspotButton.disabled = !canStart;
      elements.stopHotspotButton.disabled = !canStop;
      elements.startHotspotButton.textContent = busy && state.hotspotAction === "start" ? "Starting..." : "Start Hotspot";
      elements.stopHotspotButton.textContent = busy && state.hotspotAction === "stop" ? "Stopping..." : "Stop Hotspot";
    }

    async function requestHotspot(action) {
      if (state.hotspotActionInFlight) {
        return;
      }

      state.hotspotActionInFlight = true;
      state.hotspotAction = action;
      elements.actionStatus.textContent = action === "start"
        ? "Starting Windows Mobile Hotspot..."
        : "Stopping Windows Mobile Hotspot...";
      updateHotspotButtons();

      try {
        const response = await fetch(`/api/hotspot/${action}`, {
          method: "POST",
          headers: { "Accept": "application/json" },
        });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data?.message || "Hotspot control failed.");
        }

        elements.actionStatus.textContent = data.message || "Hotspot state updated.";
        if (data.snapshot) {
          render(data.snapshot);
        } else {
          await refresh();
        }
      } catch (error) {
        elements.actionStatus.textContent = error instanceof Error ? error.message : "Hotspot control failed.";
        await refresh();
      } finally {
        state.hotspotActionInFlight = false;
        state.hotspotAction = "";
        updateHotspotButtons();
      }
    }

    function renderLiveResult(info) {
      const current = window.location.origin + "/";
      const host = window.location.hostname;
      const isLaptopLocal =
        host === "localhost" ||
        host === "127.0.0.1" ||
        host === "[::1]";

      if (isLaptopLocal) {
        elements.liveResult.textContent = "This is the laptop-local view.";
        elements.liveDetail.textContent = info
          ? `Use the phone URL above instead of ${current}.`
          : `Use the phone URL above instead of ${current}.`;
        return;
      }

      elements.liveResult.textContent = "Success: this browser reached the laptop.";
      elements.liveDetail.textContent = `This device loaded the page from ${current}`;
    }

    document.addEventListener("DOMContentLoaded", async () => {
      elements.startHotspotButton.addEventListener("click", async () => {
        await requestHotspot("start");
      });
      elements.stopHotspotButton.addEventListener("click", async () => {
        await requestHotspot("stop");
      });
      await refresh();
      window.setInterval(() => { void refresh(); }, 5000);
    });
  </script>
</body>
</html>
""";
}
