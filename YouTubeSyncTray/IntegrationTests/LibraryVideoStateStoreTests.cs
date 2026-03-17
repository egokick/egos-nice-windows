using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class LibraryVideoStateStoreTests
{
    [Fact]
    public void Load_ReturnsEmptyState_WhenScopeHasNoSavedFile()
    {
        var root = CreateRoot();
        try
        {
            var store = new LibraryVideoStateStore(CreatePaths(root));

            var state = store.Load("scope-a");

            Assert.Empty(state);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MarkVideos_PersistsWatchedAndHiddenFlags()
    {
        var root = CreateRoot();
        try
        {
            var store = new LibraryVideoStateStore(CreatePaths(root));

            var watchedCount = store.MarkVideos("scope-a", ["video-1"], markHidden: false);
            var hiddenCount = store.MarkVideos("scope-a", ["video-2"], markHidden: true);
            var state = store.Load("scope-a");

            Assert.Equal(1, watchedCount);
            Assert.Equal(1, hiddenCount);
            Assert.True(state["video-1"].IsWatched);
            Assert.False(state["video-1"].IsHidden);
            Assert.True(state["video-2"].IsWatched);
            Assert.True(state["video-2"].IsHidden);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MarkVideos_IgnoresDuplicateIds_WhenPersisting()
    {
        var root = CreateRoot();
        try
        {
            var store = new LibraryVideoStateStore(CreatePaths(root));

            var changedCount = store.MarkVideos("scope-a", ["video-1", "video-1", "video-1"], markHidden: false);

            Assert.Equal(1, changedCount);
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
