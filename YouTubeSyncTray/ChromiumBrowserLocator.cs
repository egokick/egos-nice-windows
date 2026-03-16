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
}
