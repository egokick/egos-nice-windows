using System.Text.Json;
using Microsoft.Win32;

namespace YouTubeSyncTray;

internal enum BrowserCookieSource
{
    Firefox,
    Edge,
    Chrome
}

internal sealed class AppSettings
{
    public int DownloadCount { get; set; } = 100;
    public BrowserCookieSource BrowserCookies { get; set; } =
        ChromiumBrowserLocator.GetPreferredBrowserOrFallback(BrowserCookieSource.Chrome);
    public string BrowserProfile { get; set; } = "Default";

    public void Normalize()
    {
        DownloadCount = Math.Clamp(DownloadCount, 1, 5000);
        BrowserProfile = string.IsNullOrWhiteSpace(BrowserProfile) ? "Default" : BrowserProfile.Trim();
        if (!ChromiumBrowserLocator.SupportsManagedProfile(BrowserCookies))
        {
            BrowserCookies = ChromiumBrowserLocator.GetPreferredBrowserOrFallback(BrowserCookieSource.Chrome);
        }
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YouTubeSyncTray");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

internal static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "YouTubeSyncTray";

    public static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
        var value = key?.GetValue(AppName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRunAtStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enable)
        {
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
            return;
        }

        if (key.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
