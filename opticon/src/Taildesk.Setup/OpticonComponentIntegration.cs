using System.Diagnostics;
using Microsoft.Win32;

namespace Taildesk.Setup;

/// <summary>
/// Presents engines installed by Opticon as its managed components, while
/// leaving independently installed copies untouched.
/// </summary>
internal static class OpticonComponentIntegration
{
    private const string ManagedComponentsKey = @"SOFTWARE\Opticon\ManagedComponents";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly ComponentDefinition[] Components =
    [
        new("Private Network", ["Tailscale"], ["Tailscale"], ["Tailscale.lnk"], ["Tailscale"], ["tailscale-ipn"], false),
        new("Remote Access", ["RustDesk", "RustDesk Remote Desktop"], ["RustDesk"], ["RustDesk.lnk", "RustDesk Tray.lnk"], ["RustDesk"], ["RustDesk"], true)
    ];

    public static void Integrate(InteractiveUserProfile profile, bool installedNetworkComponent, bool installedRemoteAccessComponent)
    {
        if (installedNetworkComponent) MarkManaged(Components[0]);
        if (installedRemoteAccessComponent) MarkManaged(Components[1]);

        foreach (var component in Components.Where(IsManaged))
        {
            RemoveStandaloneEntrypoints(component, profile);
            HideFromInstalledApps(component);
        }
    }

    private static void MarkManaged(ComponentDefinition component)
    {
        using var key = Registry.LocalMachine.CreateSubKey($"{ManagedComponentsKey}\\{component.InventoryName}", writable: true);
        key.SetValue("InstalledBy", "Opticon", RegistryValueKind.String);
        key.SetValue("IntegratedAtUtc", DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
    }

    public static bool IsManagedByOpticon(string inventoryName)
    {
        return Components.Any(component => string.Equals(component.InventoryName, inventoryName, StringComparison.Ordinal) && IsManaged(component));
    }

    private static bool IsManaged(ComponentDefinition component)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"{ManagedComponentsKey}\\{component.InventoryName}", writable: false);
        return string.Equals(key?.GetValue("InstalledBy") as string, "Opticon", StringComparison.Ordinal);
    }

    private static void RemoveStandaloneEntrypoints(ComponentDefinition component, InteractiveUserProfile profile)
    {
        var sharedLocations = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            profile.Programs,
            profile.Startup,
            profile.Desktop
        };

        foreach (var location in sharedLocations)
        {
            try
            {
                foreach (var folder in component.StartMenuFolders)
                {
                    var candidate = Path.Combine(location, folder);
                    if (Directory.Exists(candidate)) Directory.Delete(candidate, recursive: true);
                }
                foreach (var shortcut in component.Shortcuts)
                {
                    var candidate = Path.Combine(location, shortcut);
                    if (File.Exists(candidate)) File.Delete(candidate);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        RemoveRunValues(Registry.LocalMachine, RunKey, component.RunValueNames);
        if (!string.IsNullOrWhiteSpace(profile.Sid))
        {
            using var users = Registry.Users;
            RemoveRunValues(users, $"{profile.Sid}\\{RunKey}", component.RunValueNames);
        }

        StopInteractiveTrayProcesses(component);
    }

    private static void StopInteractiveTrayProcesses(ComponentDefinition component)
    {
        foreach (var processName in component.TrayProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!component.StopOnlyInteractiveTrayProcesses || process.SessionId != 0)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static void RemoveRunValues(RegistryKey root, string path, IEnumerable<string> valueNames)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: true);
            if (key is null) return;
            foreach (var valueName in valueNames) key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException) { }
    }

    private static void HideFromInstalledApps(ComponentDefinition component)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = localMachine.OpenSubKey(UninstallKey, writable: true);
                if (uninstall is null) continue;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subKeyName, writable: true);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (entry is not null && component.InstalledAppNames.Contains(displayName, StringComparer.OrdinalIgnoreCase))
                    {
                        // The separate ARP entry is hidden, but the service and component
                        // inventory remain available to administrators.
                        entry.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ComponentDefinition(
        string InventoryName,
        string[] InstalledAppNames,
        string[] StartMenuFolders,
        string[] Shortcuts,
        string[] RunValueNames,
        string[] TrayProcessNames,
        bool StopOnlyInteractiveTrayProcesses);
}
