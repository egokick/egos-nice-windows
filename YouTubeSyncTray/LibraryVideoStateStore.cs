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
                        entry.HiddenAtUtc);
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

                file.Videos.Remove(videoId);
                changedCount++;
            }

            if (changedCount > 0)
            {
                SaveFile(scopeFolderName, file);
            }

            return changedCount;
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
                pair => pair.Value ?? new StoredVideoWatchState(),
                StringComparer.Ordinal);
    }

    private sealed class VideoStateFile
    {
        public Dictionary<string, StoredVideoWatchState> Videos { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredVideoWatchState
    {
        public DateTimeOffset? WatchedAtUtc { get; set; }

        public DateTimeOffset? HiddenAtUtc { get; set; }
    }
}

internal readonly record struct VideoWatchState(
    DateTimeOffset? WatchedAtUtc,
    DateTimeOffset? HiddenAtUtc)
{
    public bool IsWatched => WatchedAtUtc.HasValue || HiddenAtUtc.HasValue;

    public bool IsHidden => HiddenAtUtc.HasValue;
}
