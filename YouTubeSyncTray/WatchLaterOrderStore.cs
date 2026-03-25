using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class WatchLaterOrderStore
{
    private readonly string _rootPath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public WatchLaterOrderStore(YoutubeSyncPaths paths)
    {
        _rootPath = Path.Combine(paths.RootPath, "watch-later-order");
        Directory.CreateDirectory(_rootPath);
    }

    public IReadOnlyList<string> Load(string scopeFolderName)
    {
        lock (_gate)
        {
            try
            {
                return GetOrderedVideoIds(LoadRanks(scopeFolderName));
            }
            catch
            {
                return [];
            }
        }
    }

    public IReadOnlyList<string> Save(string scopeFolderName, IReadOnlyList<string> orderedVideoIds)
    {
        var normalizedIds = NormalizeOrderedVideoIds(orderedVideoIds);
        lock (_gate)
        {
            var ranks = BuildCanonicalRanks(normalizedIds);
            SaveFile(scopeFolderName, ranks);
            return GetOrderedVideoIds(ranks);
        }
    }

    public IReadOnlyList<string> MergeTopRange(string scopeFolderName, IReadOnlyList<string> newestFirstOrderedVideoIds)
    {
        var normalizedIds = NormalizeOrderedVideoIds(newestFirstOrderedVideoIds);
        if (normalizedIds.Count == 0)
        {
            return Load(scopeFolderName);
        }

        lock (_gate)
        {
            var ranks = LoadRanks(scopeFolderName);
            if (ranks.Count == 0)
            {
                ranks = BuildCanonicalRanks(normalizedIds);
                SaveFile(scopeFolderName, ranks);
                return GetOrderedVideoIds(ranks);
            }

            var nextRank = ranks.Values.Count == 0
                ? 0L
                : ranks.Values.Max();
            for (var index = normalizedIds.Count - 1; index >= 0; index--)
            {
                ranks[normalizedIds[index]] = ++nextRank;
            }

            SaveFile(scopeFolderName, ranks);
            return GetOrderedVideoIds(ranks);
        }
    }

    private string GetPath(string scopeFolderName) =>
        Path.Combine(_rootPath, scopeFolderName + ".json");

    private Dictionary<string, long> LoadRanks(string scopeFolderName)
    {
        var path = GetPath(scopeFolderName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var file = JsonSerializer.Deserialize<OrderFile>(File.ReadAllText(path), _jsonOptions) ?? new OrderFile();
        var normalizedRanks = NormalizeRanks(file.VideoRanks);
        if (normalizedRanks.Count > 0)
        {
            return normalizedRanks;
        }

        return BuildCanonicalRanks(NormalizeOrderedVideoIds(file.VideoIds));
    }

    private void SaveFile(string scopeFolderName, IReadOnlyDictionary<string, long> ranks)
    {
        Directory.CreateDirectory(_rootPath);
        var orderedVideoIds = GetOrderedVideoIds(ranks);
        var file = new OrderFile
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            VideoIds = [.. orderedVideoIds],
            VideoRanks = new Dictionary<string, long>(ranks, StringComparer.Ordinal)
        };

        File.WriteAllText(
            GetPath(scopeFolderName),
            JsonSerializer.Serialize(file, _jsonOptions));
    }

    private static Dictionary<string, long> BuildCanonicalRanks(IReadOnlyList<string> newestFirstOrderedVideoIds)
    {
        var ranks = new Dictionary<string, long>(StringComparer.Ordinal);
        long nextRank = 1;
        for (var index = newestFirstOrderedVideoIds.Count - 1; index >= 0; index--)
        {
            ranks[newestFirstOrderedVideoIds[index]] = nextRank++;
        }

        return ranks;
    }

    private static Dictionary<string, long> NormalizeRanks(IDictionary<string, long>? ranks)
    {
        if (ranks is null)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var normalizedRanks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in ranks)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var normalizedId = pair.Key.Trim();
            if (normalizedId.Length == 0 || pair.Value <= 0)
            {
                continue;
            }

            normalizedRanks[normalizedId] = pair.Value;
        }

        return normalizedRanks;
    }

    private static IReadOnlyList<string> GetOrderedVideoIds(IReadOnlyDictionary<string, long> ranks)
    {
        if (ranks.Count == 0)
        {
            return [];
        }

        return ranks
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeOrderedVideoIds(IEnumerable<string>? orderedVideoIds)
    {
        if (orderedVideoIds is null)
        {
            return [];
        }

        var normalizedIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var videoId in orderedVideoIds)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var normalizedId = videoId.Trim();
            if (seen.Add(normalizedId))
            {
                normalizedIds.Add(normalizedId);
            }
        }

        return normalizedIds;
    }

    private sealed class OrderFile
    {
        public DateTimeOffset SavedAtUtc { get; set; }

        public List<string> VideoIds { get; set; } = [];

        public Dictionary<string, long> VideoRanks { get; set; } = new(StringComparer.Ordinal);
    }
}
