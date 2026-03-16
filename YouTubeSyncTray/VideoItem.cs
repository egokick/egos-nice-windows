using System.Text.Json;
using System.Text.RegularExpressions;

namespace YouTubeSyncTray;

internal sealed class VideoItem
{
    private static readonly Regex IdRegex = new(@"\[(?<id>[A-Za-z0-9_-]{6,})\]\.[^.]+$", RegexOptions.Compiled);
    private static readonly string[] VideoExtensions =
    [
        ".mp4",
        ".mkv",
        ".webm",
        ".m4v",
        ".mov"
    ];
    private static readonly string[] ThumbnailExtensions =
    [
        ".webp",
        ".jpg",
        ".jpeg",
        ".png"
    ];

    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public required string UploaderName { get; init; }
    public required string VideoPath { get; init; }
    public required string InfoPath { get; init; }
    public required string ThumbnailPath { get; init; }
    public int PlaylistIndex { get; init; }
    public string DisplayIndex => PlaylistIndex > 0 ? PlaylistIndex.ToString("000") : "---";

    public static IReadOnlyList<VideoItem> LoadFromDownloads(string downloadsPath)
    {
        if (!Directory.Exists(downloadsPath))
        {
            return [];
        }

        var items = new List<VideoItem>();
        foreach (var infoPath in Directory.GetFiles(downloadsPath, "*.info.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = document.RootElement;
                var title = root.TryGetProperty("title", out var titleProperty) ? titleProperty.GetString() : null;
                var uploaderName = ReadUploaderName(root);
                var videoId = root.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                var extension = root.TryGetProperty("ext", out var extProperty) ? extProperty.GetString() : null;
                var playlistIndex = root.TryGetProperty("playlist_index", out var indexProperty) && indexProperty.TryGetInt32(out var index)
                    ? index
                    : 0;
                var videoPath = ResolveVideoPath(infoPath, extension);
                if (string.IsNullOrWhiteSpace(videoPath))
                {
                    continue;
                }

                var thumbPath = ResolveThumbnailPath(infoPath);

                if (string.IsNullOrWhiteSpace(videoId))
                {
                    videoId = TryParseVideoId(Path.GetFileName(videoPath));
                }

                if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                items.Add(new VideoItem
                {
                    VideoId = videoId,
                    Title = title,
                    UploaderName = uploaderName,
                    VideoPath = videoPath,
                    InfoPath = infoPath,
                    ThumbnailPath = thumbPath,
                    PlaylistIndex = playlistIndex
                });
            }
            catch
            {
            }
        }

        return items
            .OrderBy(item => item.PlaylistIndex == 0 ? int.MaxValue : item.PlaylistIndex)
            .ThenBy(item => Path.GetFileName(item.VideoPath), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveVideoPath(string infoPath, string? extensionFromInfo)
    {
        var basePath = infoPath[..^".info.json".Length];
        if (!string.IsNullOrWhiteSpace(extensionFromInfo))
        {
            var exactPath = basePath + "." + extensionFromInfo.TrimStart('.');
            if (File.Exists(exactPath))
            {
                return exactPath;
            }
        }

        foreach (var extension in VideoExtensions)
        {
            var candidate = basePath + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveThumbnailPath(string infoPath)
    {
        var basePath = infoPath[..^".info.json".Length];
        foreach (var extension in ThumbnailExtensions)
        {
            var candidate = basePath + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return basePath + ".webp";
    }

    private static string? TryParseVideoId(string fileName)
    {
        var match = IdRegex.Match(fileName);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string ReadUploaderName(JsonElement root)
    {
        foreach (var propertyName in new[] { "uploader", "channel", "creator", "channel_id", "uploader_id" })
        {
            if (root.TryGetProperty(propertyName, out var property))
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return string.Empty;
    }
}
