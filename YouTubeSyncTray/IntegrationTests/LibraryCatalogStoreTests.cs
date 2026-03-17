using System.Text.Json;
using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class LibraryCatalogStoreTests
{
    [Fact]
    public void LoadOrScan_UsesPersistedCatalog_WhenInfoJsonIsRemoved()
    {
        var root = CreateRoot();
        try
        {
            var paths = CreatePaths(root);
            var downloadsPath = Path.Combine(paths.DownloadsPath, "scope-a");
            Directory.CreateDirectory(downloadsPath);

            var basePath = Path.Combine(downloadsPath, "001 - Offline Clip [abc123]");
            File.WriteAllText(basePath + ".mp4", "video");
            File.WriteAllText(basePath + ".jpg", "thumb");
            File.WriteAllText(
                basePath + ".info.json",
                JsonSerializer.Serialize(new
                {
                    id = "abc123",
                    title = "Offline Clip",
                    uploader = "Offline Creator",
                    ext = "mp4",
                    playlist_index = 1
                }));

            var store = new LibraryCatalogStore(paths);
            var initialItems = store.Refresh("scope-a", downloadsPath);
            Assert.Single(initialItems);

            File.Delete(basePath + ".info.json");
            var catalogPath = Path.Combine(paths.RootPath, "library-catalog", "scope-a.json");
            File.SetLastWriteTimeUtc(catalogPath, DateTime.UtcNow.AddMinutes(5));

            var reloadedItems = store.LoadOrScan("scope-a", downloadsPath);
            var item = Assert.Single(reloadedItems);
            Assert.Equal("abc123", item.VideoId);
            Assert.Equal("Offline Clip", item.Title);
            Assert.Equal(basePath + ".mp4", item.VideoPath);
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
