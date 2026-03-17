using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class BrowserAccountDiscoveryServiceTests
{
    [Fact]
    public void DiscoverAccounts_IncludesKnownLocalBrowserScopes_WhenLiveDiscoveryMisses()
    {
        var root = CreateRoot();
        try
        {
            var paths = CreatePaths(root);
            var store = new KnownLibraryScopeStore(paths);
            var scope = new AccountScopeResolver.ResolvedAccountScope(
                ScopeKey: KnownLibraryScopeStore.BuildScopeKey("Chrome|Default|foo@example.com", "yt|page|123", "scope-a"),
                BrowserAccount: null,
                YouTubeAccount: null,
                BrowserAccountKey: "Chrome|Default|foo@example.com",
                BrowserDisplayName: "Foo",
                BrowserEmail: "foo@example.com",
                BrowserProfile: "Default",
                BrowserAuthUserIndex: 0,
                YouTubeAccountKey: "yt|page|123",
                YouTubeDisplayName: "Foo Tube",
                YouTubeHandle: "@footube",
                YouTubeAuthUserIndex: 0,
                FolderName: "scope-a",
                DownloadsPath: Path.Combine(paths.DownloadsPath, "scope-a"),
                ThumbnailCachePath: Path.Combine(paths.ThumbnailCachePath, "scope-a"),
                ArchivePath: Path.Combine(paths.RootPath, "archives", "scope-a.watch-later.archive.txt"));

            Directory.CreateDirectory(scope.DownloadsPath);
            store.Register(scope);

            var service = new BrowserAccountDiscoveryService(store);
            var settings = new AppSettings
            {
                BrowserCookies = BrowserCookieSource.Chrome,
                BrowserProfile = "Default",
                SelectedBrowserAccountKey = scope.BrowserAccountKey
            };

            var accounts = service.DiscoverAccounts(settings);
            var selected = service.ResolveSelectedAccount(settings);

            Assert.Contains(accounts, account => account.AccountKey == scope.BrowserAccountKey);
            Assert.True(selected.HasValue);
            Assert.Equal(scope.BrowserAccountKey, selected.Value.AccountKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static YoutubeSyncPaths CreatePaths(string root)
    {
        var assetRoot = Path.Combine(root, "assets");
        Directory.CreateDirectory(assetRoot);
        return new YoutubeSyncPaths
        {
            RootPath = root,
            BrowserProfilesPath = Path.Combine(root, "browser-profiles"),
            DownloadsPath = Path.Combine(root, "downloads"),
            YtDlpPath = Path.Combine(assetRoot, "yt-dlp.exe"),
            CookiesPath = Path.Combine(root, "youtube-cookies.txt"),
            CookiesMetadataPath = Path.Combine(root, "youtube-cookies.metadata.json"),
            ArchivePath = Path.Combine(root, "watch-later.archive.txt"),
            TempPath = Path.Combine(root, "temp"),
            LogsPath = Path.Combine(root, "logs"),
            ThumbnailCachePath = Path.Combine(root, "thumb-cache"),
            FfmpegRootPath = null
        };
    }
}
