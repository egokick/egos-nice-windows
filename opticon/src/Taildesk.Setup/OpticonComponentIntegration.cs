using Microsoft.Win32;

namespace Taildesk.Setup;

/// <summary>
/// Records Opticon-managed engines and hides only their machine-wide ARP entries.
/// Elevated Setup never follows or mutates interactive-user profile paths.
/// </summary>
internal static class OpticonComponentIntegration
{
    private const string ManagedComponentsKey = @"SOFTWARE\Opticon\ManagedComponents";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly ComponentDefinition[] Components =
    [
        new("Private Network", ["Tailscale"]),
        new("Remote Access", ["RustDesk", "RustDesk Remote Desktop"])
    ];

    public static void Integrate(bool installedNetworkComponent, bool installedRemoteAccessComponent)
    {
        if (installedNetworkComponent) MarkManaged(Components[0]);
        if (installedRemoteAccessComponent) MarkManaged(Components[1]);
        foreach (var component in Components.Where(IsManaged)) HideFromInstalledApps(component);
    }

    private static void MarkManaged(ComponentDefinition component)
    {
        using var key = Registry.LocalMachine.CreateSubKey(
            string.Concat(ManagedComponentsKey, @"\", component.InventoryName), writable: true);
        key.SetValue("InstalledBy", "Opticon", RegistryValueKind.String);
        key.SetValue("IntegratedAtUtc", DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
    }

    public static bool IsManagedByOpticon(string inventoryName) =>
        Components.Any(component =>
            string.Equals(component.InventoryName, inventoryName, StringComparison.Ordinal)
            && IsManaged(component));

    private static bool IsManaged(ComponentDefinition component)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            string.Concat(ManagedComponentsKey, @"\", component.InventoryName), writable: false);
        return string.Equals(key?.GetValue("InstalledBy") as string, "Opticon", StringComparison.Ordinal);
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
                    if (entry is not null
                        && component.InstalledAppNames.Contains(displayName, StringComparer.OrdinalIgnoreCase))
                        entry.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ComponentDefinition(string InventoryName, string[] InstalledAppNames);
}
