using System.Text.Json;
using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class VideoItemTests
{
    [Fact]
    public void LoadFromDownloads_SupportsNonMp4VideoFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var basePath = Path.Combine(root, "001 - Example Clip [abc123]");
            File.WriteAllText(basePath + ".webm", "video");
            File.WriteAllText(basePath + ".jpg", "thumb");
            File.WriteAllText(
                basePath + ".info.json",
                JsonSerializer.Serialize(new
                {
                    id = "abc123",
                    title = "Example Clip",
                    uploader = "Example Creator",
                    ext = "webm",
                    playlist_index = 1
                }));

            var items = VideoItem.LoadFromDownloads(root);

            var item = Assert.Single(items);
            Assert.Equal(basePath + ".webm", item.VideoPath);
            Assert.Equal(basePath + ".jpg", item.ThumbnailPath);
            Assert.Equal("abc123", item.VideoId);
            Assert.Equal("Example Creator", item.UploaderName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDownloads_FallsBackToChannelNameWhenUploaderIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var basePath = Path.Combine(root, "001 - Example Clip [abc123]");
            File.WriteAllText(basePath + ".mp4", "video");
            File.WriteAllText(basePath + ".jpg", "thumb");
            File.WriteAllText(
                basePath + ".info.json",
                JsonSerializer.Serialize(new
                {
                    id = "abc123",
                    title = "Example Clip",
                    channel = "Channel Fallback",
                    ext = "mp4",
                    playlist_index = 1
                }));

            var item = Assert.Single(VideoItem.LoadFromDownloads(root));
            Assert.Equal("Channel Fallback", item.UploaderName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
