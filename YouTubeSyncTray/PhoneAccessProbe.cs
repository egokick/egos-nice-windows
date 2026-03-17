using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class PhoneAccessProbe
{
    private readonly int _port;

    public PhoneAccessProbe(int port)
    {
        _port = port;
    }

    public PhoneAccessSnapshot GetSnapshot(bool canControlHotspot = false)
    {
        var hotspot = HotspotInfo.TryRead();
        var candidates = GetCandidateUrls(_port);
        var recommendedUrl = candidates.FirstOrDefault()?.Url ?? BuildHttpUrl("127.0.0.1", _port);
        var instruction = BuildInstruction(hotspot, recommendedUrl);

        return new PhoneAccessSnapshot(
            MachineName: Environment.MachineName,
            Port: _port,
            HotspotState: hotspot.State,
            HotspotSsid: hotspot.Ssid,
            MaxClients: hotspot.MaxClients,
            CurrentWifiName: hotspot.CurrentWifiName,
            RecommendedUrl: recommendedUrl,
            Instruction: instruction,
            CandidateUrls: candidates,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            CanControlHotspot: canControlHotspot);
    }

    public HotspotActionResponse SetHotspotEnabled(bool enable, bool canControlHotspot = false)
    {
        var targetState = enable ? "On" : "Off";
        try
        {
            var before = GetSnapshot(canControlHotspot);
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
            var after = WaitForState(targetState, TimeSpan.FromSeconds(20), canControlHotspot);
            var success = string.Equals(after.HotspotState, targetState, StringComparison.OrdinalIgnoreCase);
            return new HotspotActionResponse(
                Success: success,
                Message: BuildToggleMessage(enable, success, after),
                Snapshot: after);
        }
        catch (Exception ex)
        {
            var snapshot = GetSnapshot(canControlHotspot);
            return new HotspotActionResponse(
                Success: false,
                Message: $"{(enable ? "Could not start" : "Could not stop")} hotspot. {ex.Message}",
                Snapshot: snapshot);
        }
    }

    private PhoneAccessSnapshot WaitForState(string targetState, TimeSpan timeout, bool canControlHotspot)
    {
        var deadline = DateTime.UtcNow + timeout;
        var snapshot = GetSnapshot(canControlHotspot);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(snapshot.HotspotState, targetState, StringComparison.OrdinalIgnoreCase))
            {
                return snapshot;
            }

            Thread.Sleep(500);
            snapshot = GetSnapshot(canControlHotspot);
        }

        return snapshot;
    }

    private static string BuildInstruction(HotspotInfo hotspot, string recommendedUrl)
    {
        if (!string.IsNullOrWhiteSpace(hotspot.Ssid) && string.Equals(hotspot.State, "On", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connect the phone to hotspot SSID '{hotspot.Ssid}', then open {recommendedUrl}.";
        }

        if (!string.IsNullOrWhiteSpace(hotspot.Ssid) && string.Equals(hotspot.State, "InTransition", StringComparison.OrdinalIgnoreCase))
        {
            return $"Windows is still changing hotspot state for '{hotspot.Ssid}'. Wait a few seconds, then open {recommendedUrl}.";
        }

        if (!string.IsNullOrWhiteSpace(hotspot.Ssid) && !string.IsNullOrWhiteSpace(hotspot.CurrentWifiName))
        {
            return $"Hotspot is currently off. For plane use, start Windows Mobile Hotspot and connect the phone to '{hotspot.Ssid}'. For an immediate test right now, connect the phone to Wi-Fi '{hotspot.CurrentWifiName}' and open {recommendedUrl}.";
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

    private static string BuildToggleMessage(bool enable, bool success, PhoneAccessSnapshot snapshot)
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

                var url = BuildHttpUrl(address.Address.ToString(), port);
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

    private static string BuildHttpUrl(string host, int port)
    {
        return port == 80
            ? $"http://{host}/"
            : $"http://{host}:{port}/";
    }
}

internal sealed record PhoneAccessSnapshot(
    string MachineName,
    int Port,
    string HotspotState,
    string? HotspotSsid,
    int? MaxClients,
    string? CurrentWifiName,
    string RecommendedUrl,
    string Instruction,
    IReadOnlyList<UrlCandidate> CandidateUrls,
    DateTimeOffset GeneratedAtUtc,
    bool CanControlHotspot);

internal sealed record UrlCandidate(
    string InterfaceName,
    string InterfaceType,
    string Address,
    string Url);

internal sealed record HotspotActionResponse(
    bool Success,
    string Message,
    PhoneAccessSnapshot Snapshot);

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
            "  $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Select-Object -First 1",
            "}",
            "if ($null -eq $profile) {",
            "  throw 'No connection profile is available for hotspot control.'",
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
