using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class WatchLaterOrderStoreTests
{
    [Fact]
    public void Load_ReturnsEmpty_WhenScopeHasNoSavedOrder()
    {
        var root = CreateRoot();
        try
        {
            var store = new WatchLaterOrderStore(CreatePaths(root));

            var orderedIds = store.Load("scope-a");

            Assert.Empty(orderedIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_AndLoad_NormalizesAndPreservesOrder()
    {
        var root = CreateRoot();
        try
        {
            var store = new WatchLaterOrderStore(CreatePaths(root));

            var orderedIds = store.Save("scope-a", [" a ", "b", "a", "", "c"]);

            Assert.Equal(["a", "b", "c"], orderedIds);
            Assert.Equal(["a", "b", "c"], store.Load("scope-a"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeTopRange_PromotesCurrentSyncWindow_WithoutLosingOlderTailOrder()
    {
        var root = CreateRoot();
        try
        {
            var store = new WatchLaterOrderStore(CreatePaths(root));

            store.Save("scope-a", ["new-03", "new-02", "new-01", "old-02", "old-01"]);

            var mergedOrder = store.MergeTopRange("scope-a", ["new-05", "new-04", "new-03", "new-02", "new-01", "old-02"]);

            Assert.Equal(
                ["new-05", "new-04", "new-03", "new-02", "new-01", "old-02", "old-01"],
                mergedOrder);
            Assert.Equal(mergedOrder, store.Load("scope-a"));
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
