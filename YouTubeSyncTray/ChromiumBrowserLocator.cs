using System.Text.Json;
using Microsoft.Win32;

namespace YouTubeSyncTray;

internal static class ChromiumBrowserLocator
{
    private const string UserChoiceRegistryPath = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";
    private const string AppPathsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    public static IReadOnlyList<BrowserCookieSource> GetInstalledBrowsers()
    {
        var browsers = new List<BrowserCookieSource>();
        foreach (var browser in GetManagedBrowsers())
        {
            if (TryGetExecutablePath(browser, out _))
            {
                browsers.Add(browser);
            }
        }

        return browsers;
    }

    public static IReadOnlyList<BrowserCookieSource> GetManagedBrowsers() =>
    [
        BrowserCookieSource.Chrome,
        BrowserCookieSource.Edge
    ];

    public static bool SupportsManagedProfile(BrowserCookieSource browser) =>
        browser is BrowserCookieSource.Chrome or BrowserCookieSource.Edge;

    public static BrowserCookieSource GetPreferredBrowserOrFallback(BrowserCookieSource fallbackBrowser)
    {
        var installedBrowsers = GetInstalledBrowsers();
        if (installedBrowsers.Count == 0)
        {
            return fallbackBrowser;
        }

        var defaultBrowser = GetDefaultBrowser(installedBrowsers);
        if (defaultBrowser.HasValue)
        {
            return defaultBrowser.Value;
        }

        if (installedBrowsers.Contains(fallbackBrowser))
        {
            return fallbackBrowser;
        }

        return installedBrowsers[0];
    }

    public static BrowserCookieSource? GetDefaultBrowser(IEnumerable<BrowserCookieSource>? availableBrowsers = null)
    {
        var allowedBrowsers = (availableBrowsers ?? GetInstalledBrowsers()).ToHashSet();
        if (allowedBrowsers.Count == 0)
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserChoiceRegistryPath, writable: false);
            var progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
            {
                return null;
            }

            var browser = progId.IndexOf("edge", StringComparison.OrdinalIgnoreCase) >= 0
                ? BrowserCookieSource.Edge
                : progId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0
                    ? BrowserCookieSource.Chrome
                    : (BrowserCookieSource?)null;

            return browser.HasValue && allowedBrowsers.Contains(browser.Value) ? browser : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryGetExecutablePath(BrowserCookieSource browser, out string executablePath)
    {
        executablePath = GetCandidateExecutablePaths(browser)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists)
            ?? string.Empty;

        return executablePath.Length > 0;
    }

    public static string GetExecutablePath(BrowserCookieSource browser)
    {
        if (TryGetExecutablePath(browser, out var executablePath))
        {
            return executablePath;
        }

        throw new InvalidOperationException($"Could not find the {GetDisplayName(browser)} executable in the standard install locations.");
    }

    public static string GetDisplayName(BrowserCookieSource browser) =>
        browser switch
        {
            BrowserCookieSource.Chrome => "Google Chrome",
            BrowserCookieSource.Edge => "Microsoft Edge",
            BrowserCookieSource.Firefox => "Firefox",
            _ => browser.ToString()
        };

    public static bool TryGetProfilePreferencesPath(BrowserCookieSource browser, string profile, out string preferencesPath)
    {
        preferencesPath = string.Empty;
        if (!TryGetUserDataPath(browser, out var userDataPath))
        {
            return false;
        }

        var normalizedProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
        var candidate = Path.Combine(userDataPath, normalizedProfile, "Preferences");
        if (!File.Exists(candidate))
        {
            return false;
        }

        preferencesPath = candidate;
        return true;
    }

    public static IReadOnlyList<string> EnumerateProfiles(BrowserCookieSource browser)
    {
        if (!TryGetUserDataPath(browser, out var userDataPath))
        {
            return [];
        }

        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Default"
        };

        try
        {
            foreach (var directory in Directory.GetDirectories(userDataPath, "*", SearchOption.TopDirectoryOnly))
            {
                var profileDirectoryName = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(profileDirectoryName))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(directory, "Preferences")))
                {
                    profiles.Add(profileDirectoryName);
                }
            }
        }
        catch
        {
            return profiles.ToList();
        }

        return profiles.ToList();
    }

    public static string GetProfileDisplayName(BrowserCookieSource browser, string profile)
    {
        if (!TryGetProfilePreferencesPath(browser, profile, out var preferencesPath))
        {
            return string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (document.RootElement.TryGetProperty("profile", out var profileElement)
                && profileElement.ValueKind == JsonValueKind.Object)
            {
                if (profileElement.TryGetProperty("name", out var nameElement)
                    && nameElement.ValueKind == JsonValueKind.String)
                {
                    var name = nameElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
    }

    public static bool TryGetUserDataPath(BrowserCookieSource browser, out string userDataPath)
    {
        userDataPath = browser switch
        {
            BrowserCookieSource.Chrome => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google",
                "Chrome",
                "User Data"),
            BrowserCookieSource.Edge => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Edge",
                "User Data"),
            _ => string.Empty
        };

        return userDataPath.Length > 0 && Directory.Exists(userDataPath);
    }

    public static bool TryGetProfileAvatarPath(BrowserCookieSource browser, string profile, out string avatarPath)
    {
        avatarPath = string.Empty;
        if (!TryGetUserDataPath(browser, out var userDataPath))
        {
            return false;
        }

        return TryGetProfileAvatarPath(userDataPath, profile, out avatarPath);
    }

    internal static bool TryGetProfileAvatarPath(string userDataPath, string profile, out string avatarPath)
    {
        avatarPath = string.Empty;
        if (string.IsNullOrWhiteSpace(userDataPath) || !Directory.Exists(userDataPath))
        {
            return false;
        }

        var normalizedProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
        var profileDirectory = Path.Combine(userDataPath, normalizedProfile);
        if (!Directory.Exists(profileDirectory))
        {
            return false;
        }

        try
        {
            var localStatePath = Path.Combine(userDataPath, "Local State");
            if (File.Exists(localStatePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
                if (TryGetProfileAvatarFileName(document.RootElement, normalizedProfile, out var avatarFileName))
                {
                    var candidatePath = Path.Combine(profileDirectory, Path.GetFileName(avatarFileName));
                    if (File.Exists(candidatePath))
                    {
                        avatarPath = candidatePath;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Fall back to the local profile folder scan below.
        }

        return TryFindProfileAvatarFile(profileDirectory, out avatarPath);
    }

    private static IEnumerable<string> GetCandidateExecutablePaths(BrowserCookieSource browser)
    {
        foreach (var registeredPath in GetRegisteredExecutablePaths(browser))
        {
            yield return registeredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installedLocations = browser switch
        {
            BrowserCookieSource.Chrome => new[]
            {
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
            },
            BrowserCookieSource.Edge => new[]
            {
                Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            },
            _ => Array.Empty<string>()
        };

        foreach (var candidate in installedLocations)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> GetRegisteredExecutablePaths(BrowserCookieSource browser)
    {
        var executableName = browser switch
        {
            BrowserCookieSource.Chrome => "chrome.exe",
            BrowserCookieSource.Edge => "msedge.exe",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(executableName))
        {
            yield break;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            string? value = null;
            try
            {
                using var key = root.OpenSubKey(Path.Combine(AppPathsRegistryPath, executableName), writable: false);
                value = key?.GetValue(string.Empty) as string;
            }
            catch
            {
                value = null;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static bool TryGetProfileAvatarFileName(JsonElement root, string profile, out string avatarFileName)
    {
        avatarFileName = string.Empty;
        if (!root.TryGetProperty("profile", out var profileElement)
            || profileElement.ValueKind != JsonValueKind.Object
            || !profileElement.TryGetProperty("info_cache", out var infoCacheElement)
            || infoCacheElement.ValueKind != JsonValueKind.Object
            || !infoCacheElement.TryGetProperty(profile, out var profileEntry)
            || profileEntry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        avatarFileName = GetJsonString(profileEntry, "gaia_picture_file_name");
        return !string.IsNullOrWhiteSpace(avatarFileName);
    }

    private static bool TryFindProfileAvatarFile(string profileDirectory, out string avatarPath)
    {
        avatarPath = string.Empty;
        if (!Directory.Exists(profileDirectory))
        {
            return false;
        }

        var candidate = Directory.EnumerateFiles(profileDirectory, "*Profile Picture*")
            .OrderBy(GetProfileAvatarPriority)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        avatarPath = candidate;
        return true;
    }

    private static int GetProfileAvatarPriority(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 2,
            ".webp" => 3,
            ".gif" => 4,
            ".bmp" => 5,
            ".ico" => 6,
            _ => 10
        };
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }
}
