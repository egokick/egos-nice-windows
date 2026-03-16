using System.Text;

namespace YouTubeSyncTray;

internal sealed class ChromiumCookieExporter
{
    private const string ExportUrl = "https://www.youtube.com/robots.txt";
    private static readonly string[] AllowedDomains =
    [
        "youtube.com",
        ".youtube.com",
        "google.com",
        ".google.com",
        "googleusercontent.com",
        ".googleusercontent.com",
        "youtu.be",
        ".youtu.be"
    ];
    private static readonly HashSet<string> AuthCookieNames =
    [
        "SAPISID",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "SID",
        "SSID",
        "APISID",
        "HSID"
    ];
    private readonly YoutubeSyncPaths _paths;
    private readonly ChromiumManagedBrowser _managedBrowser;

    public ChromiumCookieExporter(YoutubeSyncPaths paths)
    {
        _paths = paths;
        _managedBrowser = new ChromiumManagedBrowser(paths);
    }

    public bool Supports(BrowserCookieSource browser) => _managedBrowser.Supports(browser);

    public async Task PrimeProfileAsync(
        BrowserCookieSource browser,
        string profile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await _managedBrowser.PrimeProfileAsync(browser, profile, progress, cancellationToken);
    }

    public async Task<CookieExportResult> ExportAsync(
        BrowserCookieSource browser,
        string profile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return await _managedBrowser.RunWithAuthenticatedSessionAsync(
            browser,
            profile,
            progress,
            async (session, token) =>
            {
                progress?.Report("Preparing a fresh YouTube cookie export...");
                await session.NavigateAsync(ExportUrl, token);
                await Task.Delay(TimeSpan.FromSeconds(1.5), token);

                var cookies = await session.ReadCookiesAsync(token);
                var filtered = FilterCookies(cookies).ToList();
                if (filtered.Count == 0)
                {
                    throw new InvalidOperationException("No YouTube or Google cookies were returned from the app-managed browser profile.");
                }

                EnsureAuthCookies(filtered);
                await WriteCookieFileAsync(filtered, token);
                return new CookieExportResult(_paths.CookiesPath, filtered.Count);
            },
            cancellationToken);
    }

    private static bool HasAuthCookies(IEnumerable<ChromiumCookie> cookies) =>
        cookies.Any(cookie => AuthCookieNames.Contains(cookie.Name));

    private static IEnumerable<ChromiumCookie> FilterCookies(IEnumerable<ChromiumCookie> cookies) =>
        cookies.Where(cookie => AllowedDomains.Any(domain => cookie.Domain.EndsWith(domain, StringComparison.OrdinalIgnoreCase)));

    private static void EnsureAuthCookies(IEnumerable<ChromiumCookie> cookies)
    {
        if (!HasAuthCookies(cookies))
        {
            throw new InvalidOperationException("The browser returned cookies, but none of the expected YouTube or Google auth cookies were present.");
        }
    }

    private async Task WriteCookieFileAsync(IEnumerable<ChromiumCookie> cookies, CancellationToken cancellationToken)
    {
        var lines = new List<string> { "# Netscape HTTP Cookie File" };
        lines.AddRange(cookies.Select(ToNetscapeLine));
        await File.WriteAllLinesAsync(_paths.CookiesPath, lines, new UTF8Encoding(false), cancellationToken);
    }

    private static string ToNetscapeLine(ChromiumCookie cookie)
    {
        var includeSubdomains = cookie.Domain.StartsWith(".", StringComparison.Ordinal) ? "TRUE" : "FALSE";
        var secure = cookie.Secure ? "TRUE" : "FALSE";
        var expires = cookie.Expires > 0 ? ((long)cookie.Expires).ToString() : "0";
        return string.Join('\t',
            cookie.Domain,
            includeSubdomains,
            string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
            secure,
            expires,
            cookie.Name,
            cookie.Value);
    }

    internal readonly record struct CookieExportResult(string CookiesPath, int CookieCount);
}
