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

    [Fact]
    public void LoadFromDownloads_DiscoversCaptionTracks_AndPrefersVttWhenAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var basePath = Path.Combine(root, "001 - Example Clip [abc123]");
            File.WriteAllText(basePath + ".mp4", "video");
            File.WriteAllText(basePath + ".jpg", "thumb");
            File.WriteAllText(basePath + ".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello");
            File.WriteAllText(basePath + ".en.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello");
            File.WriteAllText(basePath + ".en-orig.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello");
            File.WriteAllText(basePath + ".en-ja.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello");
            File.WriteAllText(
                basePath + ".info.json",
                JsonSerializer.Serialize(new
                {
                    id = "abc123",
                    title = "Example Clip",
                    uploader = "Example Creator",
                    ext = "mp4",
                    playlist_index = 1
                }));

            var item = Assert.Single(VideoItem.LoadFromDownloads(root));

            Assert.Collection(
                item.CaptionTracks,
                english =>
                {
                    Assert.Equal("en", english.TrackKey);
                    Assert.Equal("English", english.Label);
                    Assert.Equal("en", english.LanguageCode);
                    Assert.Equal("vtt", english.Format);
                },
                original =>
                {
                    Assert.Equal("en-orig", original.TrackKey);
                    Assert.Equal("English (Original)", original.Label);
                    Assert.Equal("en", original.LanguageCode);
                    Assert.Equal("srt", original.Format);
                },
                translated =>
                {
                    Assert.Equal("en-ja", translated.TrackKey);
                    Assert.Equal("Japanese (Translated from English)", translated.Label);
                    Assert.Equal("ja", translated.LanguageCode);
                    Assert.Equal("srt", translated.Format);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
