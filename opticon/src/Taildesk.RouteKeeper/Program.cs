using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Taildesk.Shared;

namespace Taildesk.RouteKeeper;

internal static class Program
{
    private const string DefaultControllerIPv4 = "213.188.217.227";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Windows did not provide the route helper path.");
            await ProductSigning.VerifyAuthenticodeAsync(executable);

            var controller = ReadControllerAddress(args);
            var powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powerShell)) throw new FileNotFoundException("Windows PowerShell is unavailable.", powerShell);

            var command = BuildCommand(controller.ToString());
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
            var windowsDirectory = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!Path.GetDirectoryName(powerShell)!.StartsWith(
                    windowsDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(powerShell) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The fixed Windows PowerShell path is not trusted.");
            var start = new ProcessStartInfo
            {
                FileName = powerShell,
                WorkingDirectory = systemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted",
                         "-EncodedCommand", encoded
                     })
                start.ArgumentList.Add(argument);
            start.Environment.Clear();
            start.Environment["SystemRoot"] = windowsDirectory;
            start.Environment["WINDIR"] = windowsDirectory;
            start.Environment["ProgramFiles"] =
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            start.Environment["ComSpec"] = Path.Combine(systemDirectory, "cmd.exe");
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator, systemDirectory, Path.Combine(systemDirectory, "Wbem"));
            start.Environment["PATHEXT"] = ".COM;.EXE";
            start.Environment["PSModulePath"] = Path.Combine(
                systemDirectory, "WindowsPowerShell", "v1.0", "Modules");
            start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";

            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException(
                                    "Windows could not start the fixed route operation.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch
        {
            return 1;
        }
    }

    private static IPAddress ReadControllerAddress(IReadOnlyList<string> args)
    {
        if (args.Count > 1) throw new ArgumentException("Only --controller-ip=<IPv4> is supported.");
        var value = args.Count == 0
            ? DefaultControllerIPv4
            : args[0].StartsWith("--controller-ip=", StringComparison.Ordinal)
                ? args[0]["--controller-ip=".Length..]
                : throw new ArgumentException("Only --controller-ip=<IPv4> is supported.");
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The controller route must be an IPv4 address.");
        return address;
    }

    private static string BuildCommand(string controllerIPv4) => $$"""
        $ErrorActionPreference = 'Stop'
        $controller = '{{controllerIPv4}}'
        $physical = @(Get-NetIPConfiguration | Where-Object {
            $_.NetAdapter.Status -eq 'Up' -and
            $_.NetAdapter.HardwareInterface -and
            $_.IPv4Address -and
            $_.IPv4DefaultGateway
        })
        if ($physical.Count -eq 0) { throw 'No active physical IPv4 adapter with a default gateway was found.' }
        $selected = $physical | Sort-Object {
            (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric
        } | Select-Object -First 1
        $prefix = "$controller/32"
        $gateway = $selected.IPv4DefaultGateway.NextHop
        $managed = @(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
            Where-Object { $_.Protocol -eq 'NetMgmt' })
        $correct = @($managed | Where-Object {
            $_.InterfaceIndex -eq $selected.InterfaceIndex -and $_.NextHop -eq $gateway
        })
        if ($correct.Count -eq 0 -or $managed.Count -ne $correct.Count) {
            $managed | Remove-NetRoute -Confirm:$false
            New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $selected.InterfaceIndex `
                -NextHop $gateway -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
            Restart-Service -Name 'Tailscale' -Force
        }
        """;
}
