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
    public async Task<TailscaleSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            RequireExecutable(), ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken,
            environment: BuildEnvironment(), clearEnvironment: true);
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
            RequireExecutable(),
            ["set", $"--advertise-exit-node={enabled.ToString().ToLowerInvariant()}"],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environment: BuildEnvironment(), clearEnvironment: true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }
    }

    private static string RequireExecutable()
    {
        var programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var tailscaleDirectory = Path.Combine(programFiles, "Tailscale");
        var executable = Path.Combine(tailscaleDirectory, "tailscale.exe");
        foreach (var path in new[] { programFiles, tailscaleDirectory, executable })
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("The fixed Tailscale CLI was not found.", executable);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"A fixed Tailscale path is a reparse point: {path}");
        }
        if ((File.GetAttributes(executable) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The fixed Tailscale CLI path is not a regular file.");
        return executable;
    }

    private static IReadOnlyDictionary<string, string?> BuildEnvironment()
    {
        var windows = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var system32 = Path.Combine(windows, "System32");
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["PATH"] = string.Join(Path.PathSeparator, system32, Path.Combine(system32, "Wbem")),
            ["PATHEXT"] = ".COM;.EXE"
        };
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;
}
