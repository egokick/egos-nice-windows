using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Taildesk.Uninstaller;

internal static class Program
{
    private const string ServiceName = "OpticonAgent";
    private static readonly string[] HistoricalTasks =
    [
        "Taildesk Agent", "Taildesk Update Guardian", "Taildesk Update Guardian Watchdog",
        "Taildesk SSH Supervisor", "Taildesk Fly Route", "Opticon Command Center",
        "Taildesk Setup Resume"
    ];
    private static readonly string[] FirewallRules =
    [
        "Opticon Agent (Tailscale only)", "Taildesk Agent (Tailscale only)",
        "Opticon RustDesk (Tailscale only)", "RustDesk Direct (Tailscale only)",
        "RustDesk External IPv4 Block", "RustDesk External IPv6 Block"
    ];

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var staged = args.Contains("--staged", StringComparer.Ordinal);
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        if (args.Any(arg => arg is not "--staged" and not "--quiet")) return 2;

        try
        {
            if (!staged) return await RelaunchFromStagingAsync(quiet);
            if (!quiet && MessageBox.Show(
                    "Remove Opticon, its device state, Tailscale, RustDesk, services, scheduled tasks, and firewall rules from this computer?",
                    "Uninstall Opticon", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return 0;

            await RemoveEverythingAsync();
            if (!quiet)
                MessageBox.Show("Opticon was removed from this computer.", "Opticon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ScheduleStagingCleanup();
            return 0;
        }
        catch (Exception exception)
        {
            if (!quiet)
                MessageBox.Show("Opticon could not be completely removed.\n\n" + exception.Message,
                    "Opticon uninstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task<int> RelaunchFromStagingAsync(bool quiet)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpticonUninstall", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staged = Path.Combine(root, "Uninstall-Opticon.exe");
        File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("The uninstaller path is unavailable."), staged);
        var start = new ProcessStartInfo(staged) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--staged");
        if (quiet) start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the staged uninstaller.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task RemoveEverythingAsync()
    {
        await RunIgnoreAsync("sc.exe", "stop", ServiceName);
        await RunIgnoreAsync("sc.exe", "delete", ServiceName);
        foreach (var task in HistoricalTasks)
        {
            await RunIgnoreAsync("schtasks.exe", "/End", "/TN", task);
            await RunIgnoreAsync("schtasks.exe", "/Delete", "/TN", task, "/F");
        }
        foreach (var rule in FirewallRules)
            await RunIgnoreAsync("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={rule}");
        await RunIgnoreAsync("net.exe", "user", "OpticonRemoteAdmin", "/delete");

        StopOpticonProcesses();
        await RunIgnoreAsync(FindTailscale(), "logout");
        await UninstallMsiAsync("RustDesk", "RustDesk Remote Desktop");
        await UninstallMsiAsync("Tailscale");

        using (var uninstall = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true))
            uninstall?.DeleteSubKeyTree("Opticon", throwOnMissingSubKey: false);

        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonProvenance"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonBootstrap"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonBootstrapUnvalidated"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Taildesk"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Taildesk"));
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Opticon"));
    }

    private static void StopOpticonProcesses()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk"))) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var image = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                    if (!image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch { }
            }
        }
    }

    private static async Task UninstallMsiAsync(params string[] displayNames)
    {
        foreach (var productCode in FindProductCodes(displayNames))
        {
            var result = await RunAsync("msiexec.exe", "/x", productCode, "/qn", "/norestart");
            if (result.ExitCode is not (0 or 1605 or 1614 or 3010))
                throw new InvalidOperationException($"Windows Installer could not remove {displayNames[0]} (exit {result.ExitCode}). {result.Error}".Trim());
        }
    }

    private static IReadOnlyList<string> FindProductCodes(IReadOnlyCollection<string> displayNames)
    {
        var productCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                var display = entry?.GetValue("DisplayName") as string;
                if (display is null || !displayNames.Contains(display, StringComparer.OrdinalIgnoreCase)) continue;
                if (Guid.TryParse(name, out var id)) productCodes.Add(id.ToString("B"));
            }
        }
        return productCodes.ToArray();
    }

    private static string FindTailscale()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : "tailscale.exe";
    }

    private static void DeleteFixedRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) && !File.Exists(full)) return;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to remove the reparse-point root {full}.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else File.Delete(full);
    }

    private static async Task RunIgnoreAsync(string file, params string[] arguments)
    {
        try { _ = await RunAsync(file, arguments); } catch { }
    }

    private static async Task<CommandResult> RunAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Windows could not start {Path.GetFileName(file)}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await output, await error);
    }

    private static void ScheduleStagingCleanup()
    {
        var executable = Environment.ProcessPath;
        var directory = executable is null ? null : Path.GetDirectoryName(executable);
        if (executable is null || directory is null) return;
        _ = MoveFileEx(executable, null, 4);
        _ = MoveFileEx(directory, null, 4);
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null) _ = MoveFileEx(parent, null, 4);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
