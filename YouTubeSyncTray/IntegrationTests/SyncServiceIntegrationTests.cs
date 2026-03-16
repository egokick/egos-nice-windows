using Xunit.Abstractions;
using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class SyncServiceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public SyncServiceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [InteractiveFact]
    [Trait("Category", "Integration")]
    public async Task ChromeManagedCookieRefresh_CanFetchWatchLaterAndRunSingleItemSync()
    {
        var settings = IntegrationTestSettings.Load();
        var paths = IntegrationTestSettings.CreatePaths(settings);
        var service = new SyncService(paths);
        var progress = new Progress<string>(message => _output.WriteLine(message));

        _output.WriteLine($"Browser: {settings.BrowserCookies}");
        _output.WriteLine($"Profile: {settings.BrowserProfile}");
        _output.WriteLine($"Root: {paths.RootPath}");

        var total = await service.GetWatchLaterTotalAsync(settings, CancellationToken.None);
        _output.WriteLine($"Watch Later total: {total}");
        Assert.True(total > 0, "Expected at least one item in Watch Later.");
        Assert.True(File.Exists(paths.CookiesPath), $"Expected cookies file at '{paths.CookiesPath}'.");

        var summary = await service.SyncRecentAsync(settings, progress, CancellationToken.None);
        _output.WriteLine(
            $"Sync summary: requested={summary.RequestedCount}, target={summary.TargetCount}, downloaded={summary.DownloadedCount}, alreadyPresent={summary.AlreadyPresentCount}, missingAfter={summary.MissingAfterSyncCount}");

        Assert.True(summary.TargetCount > 0, "Expected at least one target video.");
        Assert.True(
            summary.DownloadedCount + summary.AlreadyPresentCount > 0,
            "Expected the sync to either download or find an existing video in the integration test library.");
    }

    private static class IntegrationTestSettings
    {
        private const string BrowserEnv = "YOUTUBE_SYNC_BROWSER";
        private const string ProfileEnv = "YOUTUBE_SYNC_PROFILE";
        private const string CountEnv = "YOUTUBE_SYNC_DOWNLOAD_COUNT";

        public static AppSettings Load()
        {
            var browser = ParseBrowser(Environment.GetEnvironmentVariable(BrowserEnv));
            var profile = Environment.GetEnvironmentVariable(ProfileEnv);
            var downloadCount = ParseDownloadCount(Environment.GetEnvironmentVariable(CountEnv));

            var settings = new AppSettings
            {
                BrowserCookies = browser,
                BrowserProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim(),
                DownloadCount = downloadCount
            };
            settings.Normalize();
            return settings;
        }

        public static YoutubeSyncPaths CreatePaths(AppSettings settings)
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

            if (!File.Exists(paths.YtDlpPath))
            {
                throw new InvalidOperationException($"yt-dlp was not found at '{paths.YtDlpPath}'.");
            }

            return paths;
        }

        private static BrowserCookieSource ParseBrowser(string? value)
        {
            if (Enum.TryParse<BrowserCookieSource>(value, ignoreCase: true, out var browser))
            {
                return browser;
            }

            return BrowserCookieSource.Chrome;
        }

        private static int ParseDownloadCount(string? value)
        {
            return int.TryParse(value, out var count) ? Math.Clamp(count, 1, 3) : 1;
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

            throw new InvalidOperationException("Could not locate the YouTube Sync project root from the test output directory.");
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
}
