using YouTubeSyncTray;

var options = PrimeOptions.Parse(args);
var settings = new AppSettings
{
    BrowserCookies = options.Browser,
    BrowserProfile = options.Profile,
    DownloadCount = 1
};
settings.Normalize();

var paths = PrimePaths.Create(settings);
var exporter = new ChromiumCookieExporter(paths);
var progress = new Progress<string>(message => Console.WriteLine(message));

Console.WriteLine($"Browser: {settings.BrowserCookies}");
Console.WriteLine($"Profile: {settings.BrowserProfile}");
Console.WriteLine($"Managed root: {paths.RootPath}");

if (!exporter.Supports(settings.BrowserCookies))
{
    Console.Error.WriteLine("Only Chrome and Edge are supported by the priming tool.");
    return 1;
}

if (options.Reset)
{
    Console.WriteLine("Resetting the managed profile directory before opening the browser.");
    PrimePaths.Reset(paths.RootPath);
    paths = PrimePaths.Create(settings);
    exporter = new ChromiumCookieExporter(paths);
}

Console.WriteLine();
Console.WriteLine("Step 1: the managed browser profile will open in normal mode.");
Console.WriteLine("Step 2: sign into YouTube there.");
Console.WriteLine("Step 3: close that browser window when you are finished.");
Console.WriteLine();

await exporter.PrimeProfileAsync(settings.BrowserCookies, settings.BrowserProfile, progress, CancellationToken.None);

Console.WriteLine();
Console.WriteLine("Browser closed. Verifying that the managed profile can export YouTube cookies...");

var exportResult = await exporter.ExportAsync(settings.BrowserCookies, settings.BrowserProfile, progress, CancellationToken.None);

Console.WriteLine($"Cookie export succeeded: {exportResult.CookiesPath}");
Console.WriteLine($"Cookie count: {exportResult.CookieCount}");
Console.WriteLine();
Console.WriteLine("Next step:");
Console.WriteLine($"  $env:YOUTUBE_SYNC_RUN_INTERACTIVE='1'; $env:YOUTUBE_SYNC_BROWSER='{settings.BrowserCookies}'; $env:YOUTUBE_SYNC_PROFILE='{settings.BrowserProfile}'; $env:YOUTUBE_SYNC_DOWNLOAD_COUNT='1'; dotnet test .\\YouTubeSyncTray\\IntegrationTests\\YouTubeSyncTray.IntegrationTests.csproj --no-build --filter ChromeManagedCookieRefresh_CanFetchWatchLaterAndRunSingleItemSync -v n");
return 0;

internal sealed record PrimeOptions(BrowserCookieSource Browser, string Profile, bool Reset)
{
    public static PrimeOptions Parse(string[] args)
    {
        var browser = BrowserCookieSource.Chrome;
        var profile = "Default";
        var reset = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--reset", StringComparison.OrdinalIgnoreCase))
            {
                reset = true;
                continue;
            }

            if (string.Equals(arg, "--browser", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                i++;
                if (!Enum.TryParse<BrowserCookieSource>(args[i], ignoreCase: true, out browser))
                {
                    throw new ArgumentException($"Unsupported browser '{args[i]}'.");
                }

                continue;
            }

            if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                profile = args[++i];
                continue;
            }

            throw new ArgumentException($"Unknown argument '{arg}'.");
        }

        return new PrimeOptions(browser, string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim(), reset);
    }
}

internal static class PrimePaths
{
    public static YoutubeSyncPaths Create(AppSettings settings)
    {
        var projectRoot = FindProjectRoot();
        var bundledRoot = Path.Combine(projectRoot, "youtube-sync");
        var integrationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouTubeSyncTray",
            "IntegrationTests",
            settings.BrowserCookies.ToString(),
            SanitizePathSegment(settings.BrowserProfile));

        var ffmpegRoot = Directory.Exists(Path.Combine(bundledRoot, "tools"))
            ? Directory.GetDirectories(Path.Combine(bundledRoot, "tools"), "ffmpeg-*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

        var paths = new YoutubeSyncPaths
        {
            RootPath = integrationRoot,
            BrowserProfilesPath = Path.Combine(integrationRoot, "browser-profiles"),
            DownloadsPath = Path.Combine(integrationRoot, "downloads"),
            YtDlpPath = Path.Combine(bundledRoot, "yt-dlp.exe"),
            CookiesPath = Path.Combine(integrationRoot, "youtube-cookies.txt"),
            CookiesMetadataPath = Path.Combine(integrationRoot, "youtube-cookies.metadata.json"),
            ArchivePath = Path.Combine(integrationRoot, "watch-later.archive.txt"),
            TempPath = Path.Combine(integrationRoot, "temp"),
            LogsPath = Path.Combine(integrationRoot, "logs"),
            ThumbnailCachePath = Path.Combine(integrationRoot, "thumb-cache"),
            FfmpegRootPath = ffmpegRoot
        };

        foreach (var directory in new[]
        {
            paths.RootPath,
            paths.BrowserProfilesPath,
            paths.DownloadsPath,
            paths.TempPath,
            paths.LogsPath,
            paths.ThumbnailCachePath
        })
        {
            Directory.CreateDirectory(directory);
        }

        return paths;
    }

    public static void Reset(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        Directory.Delete(rootPath, recursive: true);
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "YouTubeSyncTray.csproj"))
                && Directory.Exists(Path.Combine(current.FullName, "youtube-sync")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the YouTube Sync project root from the tool output directory.");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            builder[i] = invalid.Contains(value[i]) ? '_' : value[i];
        }

        return new string(builder);
    }
}
