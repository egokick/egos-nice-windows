namespace YouTubeSyncTray;

internal sealed class LibraryBrowserState
{
    internal enum SyncAuthState
    {
        Missing,
        Ready,
        Failed
    }

    private const int MaxRecentMessages = 8;

    private readonly object _syncRoot = new();
    private readonly Queue<string> _recentMessages = new();
    private HashSet<string> _downloadedVideoIds = new(StringComparer.Ordinal);
    private List<string> _syncTargetVideoIds = [];
    private Dictionary<string, int> _watchLaterOrderByVideoId = new(StringComparer.Ordinal);
    private bool _isBusy;
    private string _status;
    private SyncAuthState _syncAuthState = SyncAuthState.Missing;
    private string _syncAuthMessage = "Authentication has not been verified yet for the selected account.";
    private int _videoCount;
    private int? _watchLaterTotalCount;
    private long _libraryVersion = 1;
    private DateTimeOffset _updatedAtUtc;

    public LibraryBrowserState(IReadOnlyCollection<string> initialVideoIds)
    {
        _downloadedVideoIds = NormalizeVideoIds(initialVideoIds);
        _videoCount = _downloadedVideoIds.Count;
        _status = $"{_videoCount} downloaded videos";
        _updatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetBusy(bool isBusy, string status)
    {
        lock (_syncRoot)
        {
            _isBusy = isBusy;
            if (!string.IsNullOrWhiteSpace(status))
            {
                _status = status.Trim();
                if (_isBusy)
                {
                    AppendRecentMessage(_status);
                }
            }

            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void ReportProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Trim();
        lock (_syncRoot)
        {
            _status = normalized;
            AppendRecentMessage(normalized);
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetVideoIds(IReadOnlyCollection<string> videoIds)
    {
        var normalizedVideoIds = NormalizeVideoIds(videoIds);
        lock (_syncRoot)
        {
            if (!HasSameVideoIds(_downloadedVideoIds, normalizedVideoIds))
            {
                _downloadedVideoIds = normalizedVideoIds;
                _videoCount = _downloadedVideoIds.Count;
                _libraryVersion++;
            }

            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetSyncAuthState(SyncAuthState syncAuthState, string? message = null)
    {
        lock (_syncRoot)
        {
            _syncAuthState = syncAuthState;
            if (!string.IsNullOrWhiteSpace(message))
            {
                _syncAuthMessage = message.Trim();
            }
            else
            {
                _syncAuthMessage = syncAuthState switch
                {
                    SyncAuthState.Ready => "Authentication is working for the selected account.",
                    SyncAuthState.Failed => "Authentication failed for the selected account.",
                    _ => "Authentication has not been verified yet for the selected account."
                };
            }

            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetWatchLaterTotalCount(int? watchLaterTotalCount)
    {
        lock (_syncRoot)
        {
            _watchLaterTotalCount = watchLaterTotalCount.HasValue
                ? Math.Max(watchLaterTotalCount.Value, 0)
                : null;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetWatchLaterOrder(IReadOnlyList<string> orderedVideoIds)
    {
        var nextOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextIndex = 1;
        foreach (var videoId in orderedVideoIds)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var normalizedVideoId = videoId.Trim();
            if (nextOrder.ContainsKey(normalizedVideoId))
            {
                continue;
            }

            nextOrder[normalizedVideoId] = nextIndex;
            nextIndex++;
        }

        lock (_syncRoot)
        {
            if (HasSameWatchLaterOrder(_watchLaterOrderByVideoId, nextOrder))
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _watchLaterOrderByVideoId = nextOrder;
            _libraryVersion++;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetSyncTargetIds(IReadOnlyList<string> syncTargetVideoIds)
    {
        var normalizedVideoIds = NormalizeOrderedVideoIds(syncTargetVideoIds);
        lock (_syncRoot)
        {
            if (HasSameOrderedVideoIds(_syncTargetVideoIds, normalizedVideoIds))
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _syncTargetVideoIds = normalizedVideoIds;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void ClearSyncTargetIds()
    {
        lock (_syncRoot)
        {
            if (_syncTargetVideoIds.Count == 0)
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _syncTargetVideoIds = [];
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void ClearWatchLaterOrder()
    {
        lock (_syncRoot)
        {
            if (_watchLaterOrderByVideoId.Count == 0)
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _watchLaterOrderByVideoId = new Dictionary<string, int>(StringComparer.Ordinal);
            _libraryVersion++;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyDictionary<string, int> GetWatchLaterOrderSnapshot()
    {
        lock (_syncRoot)
        {
            return new Dictionary<string, int>(_watchLaterOrderByVideoId, StringComparer.Ordinal);
        }
    }

    public void MarkLibraryChanged(IReadOnlyCollection<string>? videoIds = null)
    {
        lock (_syncRoot)
        {
            if (videoIds is not null)
            {
                _downloadedVideoIds = NormalizeVideoIds(videoIds);
                _videoCount = _downloadedVideoIds.Count;
            }

            _libraryVersion++;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public BrowserLibraryStatusSnapshot GetSnapshot(AppSettings settings)
    {
        settings.Normalize();

        lock (_syncRoot)
        {
            var syncScope = CalculateSyncScopeCounts(
                _downloadedVideoIds,
                _syncTargetVideoIds);
            return new BrowserLibraryStatusSnapshot(
                _isBusy,
                _status,
                _videoCount,
                _libraryVersion,
                settings.DownloadCount,
                _watchLaterTotalCount,
                syncScope.DownloadedCount,
                syncScope.TargetCount,
                syncScope.FailedCount,
                _syncAuthState,
                _syncAuthMessage,
                ChromiumBrowserLocator.GetDisplayName(settings.BrowserCookies),
                settings.BrowserProfile,
                _recentMessages.ToArray(),
                _updatedAtUtc);
        }
    }

    internal readonly record struct BrowserLibraryStatusSnapshot(
        bool IsBusy,
        string Status,
        int VideoCount,
        long LibraryVersion,
        int ConfiguredDownloadCount,
        int? WatchLaterTotalCount,
        int? SyncScopeDownloadedCount,
        int? SyncScopeTargetCount,
        int? SyncScopeFailedCount,
        SyncAuthState SyncAuthState,
        string SyncAuthMessage,
        string BrowserName,
        string BrowserProfile,
        IReadOnlyList<string> RecentMessages,
        DateTimeOffset UpdatedAtUtc);

    internal static SyncScopeCounts CalculateSyncScopeCounts(
        IReadOnlyCollection<string> downloadedVideoIds,
        IReadOnlyList<string> syncTargetVideoIds)
    {
        if (syncTargetVideoIds.Count == 0)
        {
            return new SyncScopeCounts(null, null, null);
        }

        var normalizedDownloadedVideoIds = downloadedVideoIds as HashSet<string>
            ?? new HashSet<string>(downloadedVideoIds, StringComparer.Ordinal);
        var downloadedCount = 0;
        foreach (var videoId in syncTargetVideoIds)
        {
            if (normalizedDownloadedVideoIds.Contains(videoId))
            {
                downloadedCount++;
            }
        }

        var targetCount = syncTargetVideoIds.Count;
        return new SyncScopeCounts(
            downloadedCount,
            targetCount,
            Math.Max(targetCount - downloadedCount, 0));
    }

    private void AppendRecentMessage(string message)
    {
        if (_recentMessages.Count > 0 && string.Equals(_recentMessages.Last(), message, StringComparison.Ordinal))
        {
            return;
        }

        _recentMessages.Enqueue(message);
        while (_recentMessages.Count > MaxRecentMessages)
        {
            _recentMessages.Dequeue();
        }
    }

    private static bool HasSameWatchLaterOrder(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var rightValue) || rightValue != entry.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> NormalizeVideoIds(IEnumerable<string> videoIds)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var videoId in videoIds)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            normalized.Add(videoId.Trim());
        }

        return normalized;
    }

    private static List<string> NormalizeOrderedVideoIds(IEnumerable<string> videoIds)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var videoId in videoIds)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var trimmedVideoId = videoId.Trim();
            if (!seen.Add(trimmedVideoId))
            {
                continue;
            }

            normalized.Add(trimmedVideoId);
        }

        return normalized;
    }

    private static bool HasSameVideoIds(
        IReadOnlySet<string> left,
        IReadOnlySet<string> right)
    {
        return left.SetEquals(right);
    }

    private static bool HasSameOrderedVideoIds(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal readonly record struct SyncScopeCounts(
        int? DownloadedCount,
        int? TargetCount,
        int? FailedCount);
}
