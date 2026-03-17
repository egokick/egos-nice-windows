using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class KnownLibraryScopeStoreTests
{
    [Fact]
    public void Register_AndRememberSelectedYouTubeAccount_PersistAcrossReloads()
    {
        var root = CreateRoot();
        try
        {
            var paths = CreatePaths(root);
            var store = new KnownLibraryScopeStore(paths);
            var scope = CreateScope(paths, "primary-scope", "Chrome|Default|foo@example.com", "yt|page|1234567890");

            Directory.CreateDirectory(scope.DownloadsPath);
            store.Register(scope);
            store.UpdateScopeInventory(scope, downloadedVideoCount: 7, lastSuccessfulSyncAtUtc: DateTimeOffset.UtcNow);
            store.RememberSelectedYouTubeAccount(scope.BrowserAccountKey, scope.YouTubeAccountKey);

            var reloadedStore = new KnownLibraryScopeStore(paths);
            var scopes = reloadedStore.LoadScopes();
            var persistedScope = Assert.Single(scopes);

            Assert.Equal(scope.ScopeKey, persistedScope.ScopeKey);
            Assert.Equal(scope.FolderName, persistedScope.FolderName);
            Assert.Equal(7, persistedScope.DownloadedVideoCount);
            Assert.True(persistedScope.IsAvailableOnDisk);
            Assert.Equal(scope.YouTubeAccountKey, reloadedStore.GetRememberedYouTubeAccountKey(scope.BrowserAccountKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetMostRecentYouTubeScope_PrefersLatestSuccessfulSyncForBrowser()
    {
        var root = CreateRoot();
        try
        {
            var paths = CreatePaths(root);
            var store = new KnownLibraryScopeStore(paths);
            var browserAccountKey = "Chrome|Default|foo@example.com";
            var olderScope = CreateScope(paths, "older-scope", browserAccountKey, "yt|page|1111111111");
            var newerScope = CreateScope(paths, "newer-scope", browserAccountKey, "yt|page|2222222222");

            Directory.CreateDirectory(olderScope.DownloadsPath);
            Directory.CreateDirectory(newerScope.DownloadsPath);

            store.Register(olderScope);
            store.UpdateScopeInventory(
                olderScope,
                downloadedVideoCount: 4,
                lastSuccessfulSyncAtUtc: DateTimeOffset.UtcNow.AddHours(-3));
            store.Register(newerScope);
            store.UpdateScopeInventory(
                newerScope,
                downloadedVideoCount: 9,
                lastSuccessfulSyncAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

            var mostRecentScope = store.GetMostRecentYouTubeScope(browserAccountKey);
            Assert.NotNull(mostRecentScope);
            Assert.Equal(newerScope.YouTubeAccountKey, mostRecentScope.YouTubeAccountKey);
            Assert.True(store.HasAvailableYouTubeScope(browserAccountKey, newerScope.YouTubeAccountKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AccountScopeResolver.ResolvedAccountScope CreateScope(
        YoutubeSyncPaths paths,
        string folderName,
        string browserAccountKey,
        string youTubeAccountKey)
    {
        return new AccountScopeResolver.ResolvedAccountScope(
            ScopeKey: KnownLibraryScopeStore.BuildScopeKey(browserAccountKey, youTubeAccountKey, folderName),
            BrowserAccount: null,
            YouTubeAccount: null,
            BrowserAccountKey: browserAccountKey,
            BrowserDisplayName: "Foo",
            BrowserEmail: "foo@example.com",
            BrowserProfile: "Default",
            BrowserAuthUserIndex: 0,
            YouTubeAccountKey: youTubeAccountKey,
            YouTubeDisplayName: "Foo Tube",
            YouTubeHandle: "@footube",
            YouTubeAuthUserIndex: 0,
            FolderName: folderName,
            DownloadsPath: Path.Combine(paths.DownloadsPath, folderName),
            ThumbnailCachePath: Path.Combine(paths.ThumbnailCachePath, folderName),
            ArchivePath: Path.Combine(paths.RootPath, "archives", folderName + ".watch-later.archive.txt"));
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
