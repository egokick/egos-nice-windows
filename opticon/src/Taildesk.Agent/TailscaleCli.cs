using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class TailscaleSnapshot
{
    public string DeviceId { get; init; } = string.Empty;
    public string DnsName { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public bool Online { get; init; }
}

public sealed class TailscaleCli
{
    private static readonly string[] KnownPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        "tailscale.exe"
    ];

    public async Task<TailscaleSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(FindExecutable(), ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var self = document.RootElement.GetProperty("Self");
        var ips = self.TryGetProperty("TailscaleIPs", out var ipArray)
            ? ipArray.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

        return new TailscaleSnapshot
        {
            DeviceId = ReadString(self, "ID"),
            DnsName = ReadString(self, "DNSName").TrimEnd('.'),
            Ip = ips.FirstOrDefault(ip => ip.Contains('.')) ?? ips.FirstOrDefault() ?? string.Empty,
            Online = !self.TryGetProperty("Online", out var online) || online.GetBoolean()
        };
    }

    public async Task SetAdvertiseExitNodeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            FindExecutable(),
            ["set", $"--advertise-exit-node={enabled.ToString().ToLowerInvariant()}"],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }
    }

    private static string FindExecutable() => KnownPaths.FirstOrDefault(File.Exists)
        ?? ProcessRunner.FindOnPath("tailscale.exe")
        ?? throw new FileNotFoundException("Tailscale CLI was not found.");

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;
}
