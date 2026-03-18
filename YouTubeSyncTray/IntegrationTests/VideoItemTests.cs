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
    public void LoadFromDownloads_FallsBackToRawVideoFile_WhenInfoJsonIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var basePath = Path.Combine(root, "007 - Missing Sidecar Clip [raw123]");
            File.WriteAllText(basePath + ".mp4", "video");
            File.WriteAllText(basePath + ".jpg", "thumb");

            var item = Assert.Single(VideoItem.LoadFromDownloads(root));
            Assert.Equal("raw123", item.VideoId);
            Assert.Equal("Missing Sidecar Clip", item.Title);
            Assert.Equal(basePath + ".mp4", item.VideoPath);
            Assert.Equal(basePath + ".jpg", item.ThumbnailPath);
            Assert.Equal("007", item.DisplayIndex);
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

    [Fact]
    public void LoadFromDownloads_PrefersCurrentWatchLaterOrderOverStoredPlaylistIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            WriteVideo(root, "001 - Older Entry [older01]", "older01", "Older Entry", 1);
            WriteVideo(root, "002 - Newer Entry [newer01]", "newer01", "Newer Entry", 2);

            var items = VideoItem.LoadFromDownloads(
                root,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["newer01"] = 1,
                    ["older01"] = 2
                });

            Assert.Collection(
                items,
                first => Assert.Equal("newer01", first.VideoId),
                second => Assert.Equal("older01", second.VideoId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDownloads_FallsBackToLowestStoredPlaylistIndexFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            WriteVideo(root, "001 - Newer Entry [newer01]", "newer01", "Newer Entry", 1);
            WriteVideo(root, "002 - Older Entry [older01]", "older01", "Older Entry", 2);

            var items = VideoItem.LoadFromDownloads(root);

            Assert.Collection(
                items,
                first => Assert.Equal("newer01", first.VideoId),
                second => Assert.Equal("older01", second.VideoId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetDisplayIndex_PrefersCurrentWatchLaterOrder_WhenAvailable()
    {
        var item = new VideoItem
        {
            VideoId = "video01",
            Title = "Example",
            UploaderName = "Creator",
            VideoPath = "video01.mp4",
            InfoPath = "video01.info.json",
            ThumbnailPath = "video01.webp",
            CaptionTracks = [],
            PlaylistIndex = 42
        };

        var displayIndex = item.GetDisplayIndex(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["video01"] = 3
        });

        Assert.Equal("003", displayIndex);
    }

    private static void WriteVideo(string root, string fileName, string videoId, string title, int playlistIndex)
    {
        var basePath = Path.Combine(root, fileName);
        File.WriteAllText(basePath + ".mp4", "video");
        File.WriteAllText(basePath + ".jpg", "thumb");
        File.WriteAllText(
            basePath + ".info.json",
            JsonSerializer.Serialize(new
            {
                id = videoId,
                title,
                uploader = "Example Creator",
                ext = "mp4",
                playlist_index = playlistIndex
            }));
    }
}
