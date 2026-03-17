using System.Globalization;
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
    private static readonly string[] CaptionExtensions =
    [
        ".vtt",
        ".srt"
    ];

    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public required string UploaderName { get; init; }
    public required string VideoPath { get; init; }
    public required string InfoPath { get; init; }
    public required string ThumbnailPath { get; init; }
    public required IReadOnlyList<VideoCaptionTrack> CaptionTracks { get; init; }
    public int PlaylistIndex { get; init; }
    public string DisplayIndex => PlaylistIndex > 0 ? PlaylistIndex.ToString("000") : "---";

    public static IReadOnlyList<VideoItem> LoadFromDownloads(
        string downloadsPath,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId = null)
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
                    CaptionTracks = ResolveCaptionTracks(infoPath),
                    PlaylistIndex = playlistIndex
                });
            }
            catch
            {
            }
        }

        watchLaterOrderByVideoId ??= new Dictionary<string, int>(StringComparer.Ordinal);

        return items
            .OrderBy(item => watchLaterOrderByVideoId.ContainsKey(item.VideoId) ? 0 : 1)
            .ThenBy(item => watchLaterOrderByVideoId.TryGetValue(item.VideoId, out var currentPlaylistIndex)
                ? currentPlaylistIndex
                : int.MaxValue)
            .ThenBy(item => item.PlaylistIndex == 0 ? int.MaxValue : item.PlaylistIndex)
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

    private static IReadOnlyList<VideoCaptionTrack> ResolveCaptionTracks(string infoPath)
    {
        var basePath = infoPath[..^".info.json".Length];
        var directory = Path.GetDirectoryName(infoPath);
        var baseFileName = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseFileName))
        {
            return [];
        }

        var rawTracks = new List<RawCaptionTrack>();
        foreach (var path in Directory.GetFiles(directory, baseFileName + ".*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(path);
            if (!CaptionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
            if (!fileNameWithoutExtension.StartsWith(baseFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = fileNameWithoutExtension[baseFileName.Length..];
            if (!suffix.StartsWith(".", StringComparison.Ordinal) || suffix.Length <= 1)
            {
                continue;
            }

            var trackKey = suffix[1..].Trim();
            if (string.IsNullOrWhiteSpace(trackKey))
            {
                continue;
            }

            rawTracks.Add(new RawCaptionTrack(
                trackKey,
                path,
                extension.TrimStart('.').ToLowerInvariant()));
        }

        var availableTrackKeys = rawTracks
            .Select(track => track.TrackKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracksByKey = new Dictionary<string, VideoCaptionTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawTrack in rawTracks)
        {
            var descriptor = DescribeCaptionTrack(rawTrack.TrackKey, availableTrackKeys);
            var candidate = new VideoCaptionTrack(
                rawTrack.TrackKey,
                descriptor.Label,
                descriptor.LanguageCode,
                rawTrack.SourcePath,
                rawTrack.Format,
                descriptor.SortOrder);

            if (!tracksByKey.TryGetValue(rawTrack.TrackKey, out var existingTrack)
                || ShouldPreferCandidate(existingTrack, candidate))
            {
                tracksByKey[rawTrack.TrackKey] = candidate;
            }
        }

        return tracksByKey.Values
            .OrderBy(track => track.SortOrder)
            .ThenBy(track => track.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldPreferCandidate(VideoCaptionTrack existingTrack, VideoCaptionTrack candidate)
    {
        if (string.Equals(existingTrack.Format, candidate.Format, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(candidate.Format, "vtt", StringComparison.OrdinalIgnoreCase);
    }

    private static CaptionTrackDescriptor DescribeCaptionTrack(string trackKey, ISet<string> availableTrackKeys)
    {
        if (trackKey.EndsWith("-orig", StringComparison.OrdinalIgnoreCase))
        {
            var baseLanguageKey = trackKey[..^"-orig".Length];
            var baseLanguage = GetLanguageInfoOrFallback(baseLanguageKey);
            return new CaptionTrackDescriptor(
                $"{baseLanguage.DisplayName} (Original)",
                baseLanguage.LanguageCode,
                1);
        }

        if (TrySplitTranslatedTrack(trackKey, availableTrackKeys, out var sourceLanguageKey, out var targetLanguageKey))
        {
            var sourceLanguage = GetLanguageInfoOrFallback(sourceLanguageKey);
            var targetLanguage = GetLanguageInfoOrFallback(targetLanguageKey);
            return new CaptionTrackDescriptor(
                $"{targetLanguage.DisplayName} (Translated from {sourceLanguage.DisplayName})",
                targetLanguage.LanguageCode,
                2);
        }

        if (TryGetLanguageInfo(trackKey, out var languageInfo))
        {
            return new CaptionTrackDescriptor(languageInfo.DisplayName, languageInfo.LanguageCode, 0);
        }

        var separatorIndex = trackKey.IndexOf('-', StringComparison.Ordinal);
        var fallbackLanguageKey = separatorIndex >= 0 ? trackKey[..separatorIndex] : trackKey;
        var fallbackLanguage = GetLanguageInfoOrFallback(fallbackLanguageKey);
        return new CaptionTrackDescriptor(
            $"{fallbackLanguage.DisplayName} ({trackKey})",
            fallbackLanguage.LanguageCode,
            3);
    }

    private static bool TrySplitTranslatedTrack(
        string trackKey,
        ISet<string> availableTrackKeys,
        out string sourceLanguageKey,
        out string targetLanguageKey)
    {
        sourceLanguageKey = string.Empty;
        targetLanguageKey = string.Empty;

        for (var index = 0; index < trackKey.Length; index++)
        {
            if (trackKey[index] != '-')
            {
                continue;
            }

            var sourceCandidate = trackKey[..index];
            var targetCandidate = trackKey[(index + 1)..];
            if (string.IsNullOrWhiteSpace(sourceCandidate)
                || string.IsNullOrWhiteSpace(targetCandidate)
                || string.Equals(targetCandidate, "orig", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasSourceTrack =
                availableTrackKeys.Contains(sourceCandidate)
                || availableTrackKeys.Contains(sourceCandidate + "-orig");
            if (!hasSourceTrack)
            {
                continue;
            }

            if (!TryGetLanguageInfo(sourceCandidate, out _)
                || !TryGetLanguageInfo(targetCandidate, out _))
            {
                continue;
            }

            sourceLanguageKey = sourceCandidate;
            targetLanguageKey = targetCandidate;
            return true;
        }

        return false;
    }

    private static LanguageInfo GetLanguageInfoOrFallback(string languageCode)
    {
        return TryGetLanguageInfo(languageCode, out var languageInfo)
            ? languageInfo
            : new LanguageInfo(languageCode, languageCode.ToUpperInvariant());
    }

    private static bool TryGetLanguageInfo(string languageCode, out LanguageInfo languageInfo)
    {
        languageInfo = default;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode.Trim());
            languageInfo = new LanguageInfo(culture.Name, culture.EnglishName);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
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

    private readonly record struct CaptionTrackDescriptor(string Label, string LanguageCode, int SortOrder);

    private readonly record struct LanguageInfo(string LanguageCode, string DisplayName);

    private readonly record struct RawCaptionTrack(string TrackKey, string SourcePath, string Format);
}
