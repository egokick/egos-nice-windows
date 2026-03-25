using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class LibraryVideoStateStore
{
    private readonly string _stateRootPath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public LibraryVideoStateStore(YoutubeSyncPaths paths)
    {
        _stateRootPath = Path.Combine(paths.RootPath, "library-video-state");
        Directory.CreateDirectory(_stateRootPath);
    }

    public IReadOnlyDictionary<string, VideoWatchState> Load(string scopeFolderName)
    {
        lock (_gate)
        {
            var file = LoadFile(scopeFolderName);
            return file.Videos.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var entry = pair.Value;
                    return new VideoWatchState(
                        entry.WatchedAtUtc,
                        entry.HiddenAtUtc,
                        entry.PlaybackPositionSeconds,
                        entry.PlaybackDurationSeconds,
                        entry.PlaybackUpdatedAtUtc);
                },
                StringComparer.Ordinal);
        }
    }

    public int MarkVideos(string scopeFolderName, IEnumerable<string> videoIds, bool markHidden)
    {
        lock (_gate)
        {
            var file = LoadFile(scopeFolderName);
            var now = DateTimeOffset.UtcNow;
            var changedCount = 0;

            foreach (var videoId in videoIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                if (!file.Videos.TryGetValue(videoId, out var entry))
                {
                    entry = new StoredVideoWatchState();
                }

                var changed = false;
                if (!entry.WatchedAtUtc.HasValue)
                {
                    entry.WatchedAtUtc = now;
                    changed = true;
                }

                if (markHidden && !entry.HiddenAtUtc.HasValue)
                {
                    entry.HiddenAtUtc = now;
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                file.Videos[videoId] = entry;
                changedCount++;
            }

            if (changedCount > 0)
            {
                SaveFile(scopeFolderName, file);
            }

            return changedCount;
        }
    }

    public int RestoreVideos(string scopeFolderName, IEnumerable<string> videoIds)
    {
        lock (_gate)
        {
            var file = LoadFile(scopeFolderName);
            var changedCount = 0;

            foreach (var videoId in videoIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                if (!file.Videos.TryGetValue(videoId, out var entry))
                {
                    continue;
                }

                if (!entry.WatchedAtUtc.HasValue && !entry.HiddenAtUtc.HasValue)
                {
                    continue;
                }

                entry.WatchedAtUtc = null;
                entry.HiddenAtUtc = null;
                if (HasAnyState(entry))
                {
                    file.Videos[videoId] = entry;
                }
                else
                {
                    file.Videos.Remove(videoId);
                }

                changedCount++;
            }

            if (changedCount > 0)
            {
                SaveFile(scopeFolderName, file);
            }

            return changedCount;
        }
    }

    public bool SavePlaybackProgress(
        string scopeFolderName,
        string videoId,
        double playbackPositionSeconds,
        double playbackDurationSeconds)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return false;
        }

        lock (_gate)
        {
            var file = LoadFile(scopeFolderName);
            var normalizedVideoId = videoId.Trim();
            file.Videos.TryGetValue(normalizedVideoId, out var entry);
            entry ??= new StoredVideoWatchState();

            var (normalizedPosition, normalizedDuration) = NormalizePlaybackProgress(
                playbackPositionSeconds,
                playbackDurationSeconds);
            var changed =
                !Nullable.Equals(entry.PlaybackPositionSeconds, normalizedPosition)
                || !Nullable.Equals(entry.PlaybackDurationSeconds, normalizedDuration);

            if (!changed)
            {
                return false;
            }

            entry.PlaybackPositionSeconds = normalizedPosition;
            entry.PlaybackDurationSeconds = normalizedDuration;
            entry.PlaybackUpdatedAtUtc = normalizedPosition.HasValue
                ? DateTimeOffset.UtcNow
                : null;

            if (HasAnyState(entry))
            {
                file.Videos[normalizedVideoId] = entry;
            }
            else
            {
                file.Videos.Remove(normalizedVideoId);
            }

            SaveFile(scopeFolderName, file);
            return true;
        }
    }

    private VideoStateFile LoadFile(string scopeFolderName)
    {
        var path = GetStatePath(scopeFolderName);
        try
        {
            if (!File.Exists(path))
            {
                return new VideoStateFile();
            }

            var file = JsonSerializer.Deserialize<VideoStateFile>(File.ReadAllText(path)) ?? new VideoStateFile();
            Normalize(file);
            return file;
        }
        catch
        {
            return new VideoStateFile();
        }
    }

    private void SaveFile(string scopeFolderName, VideoStateFile file)
    {
        Normalize(file);
        Directory.CreateDirectory(_stateRootPath);
        File.WriteAllText(
            GetStatePath(scopeFolderName),
            JsonSerializer.Serialize(file, _jsonOptions));
    }

    private string GetStatePath(string scopeFolderName) =>
        Path.Combine(_stateRootPath, scopeFolderName + ".json");

    private static void Normalize(VideoStateFile file)
    {
        file.Videos = file.Videos
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair =>
                {
                    var entry = pair.Value ?? new StoredVideoWatchState();
                    var (position, duration) = NormalizePlaybackProgress(
                        entry.PlaybackPositionSeconds,
                        entry.PlaybackDurationSeconds);
                    entry.PlaybackPositionSeconds = position;
                    entry.PlaybackDurationSeconds = duration;
                    entry.PlaybackUpdatedAtUtc = position.HasValue ? entry.PlaybackUpdatedAtUtc : null;
                    return entry;
                },
                StringComparer.Ordinal);
    }

    private static (double? PlaybackPositionSeconds, double? PlaybackDurationSeconds) NormalizePlaybackProgress(
        double? playbackPositionSeconds,
        double? playbackDurationSeconds)
    {
        if (!playbackPositionSeconds.HasValue
            || !playbackDurationSeconds.HasValue
            || double.IsNaN(playbackPositionSeconds.Value)
            || double.IsInfinity(playbackPositionSeconds.Value)
            || double.IsNaN(playbackDurationSeconds.Value)
            || double.IsInfinity(playbackDurationSeconds.Value)
            || playbackDurationSeconds.Value <= 0)
        {
            return (null, null);
        }

        var normalizedDuration = Math.Max(playbackDurationSeconds.Value, 0.001d);
        var normalizedPosition = Math.Clamp(playbackPositionSeconds.Value, 0d, normalizedDuration);
        return normalizedPosition <= 0d
            ? (null, null)
            : (normalizedPosition, normalizedDuration);
    }

    private static bool HasAnyState(StoredVideoWatchState entry) =>
        entry.WatchedAtUtc.HasValue
        || entry.HiddenAtUtc.HasValue
        || entry.PlaybackPositionSeconds.HasValue;

    private sealed class VideoStateFile
    {
        public Dictionary<string, StoredVideoWatchState> Videos { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredVideoWatchState
    {
        public DateTimeOffset? WatchedAtUtc { get; set; }

        public DateTimeOffset? HiddenAtUtc { get; set; }

        public double? PlaybackPositionSeconds { get; set; }

        public double? PlaybackDurationSeconds { get; set; }

        public DateTimeOffset? PlaybackUpdatedAtUtc { get; set; }
    }
}

internal readonly record struct VideoWatchState(
    DateTimeOffset? WatchedAtUtc,
    DateTimeOffset? HiddenAtUtc,
    double? PlaybackPositionSeconds,
    double? PlaybackDurationSeconds,
    DateTimeOffset? PlaybackUpdatedAtUtc)
{
    public bool IsWatched => WatchedAtUtc.HasValue || HiddenAtUtc.HasValue;

    public bool IsHidden => HiddenAtUtc.HasValue;

    public double? PlaybackProgress =>
        PlaybackPositionSeconds.HasValue
        && PlaybackDurationSeconds.HasValue
        && PlaybackDurationSeconds.Value > 0
            ? Math.Clamp(PlaybackPositionSeconds.Value / PlaybackDurationSeconds.Value, 0d, 1d)
            : null;
}
