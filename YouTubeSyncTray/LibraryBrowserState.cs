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
    private List<PendingVideo> _pendingSyncVideos = [];
    private Dictionary<string, int> _watchLaterOrderByVideoId = new(StringComparer.Ordinal);
    private bool _isBusy;
    private string _status;
    private SyncAuthState _syncAuthState = SyncAuthState.Missing;
    private string _syncAuthMessage = "Authentication has not been verified yet for the selected account.";
    private int _videoCount;
    private int? _watchLaterTotalCount;
    private DateTimeOffset? _watchLaterTotalUpdatedAtUtc;
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
            _watchLaterTotalUpdatedAtUtc = watchLaterTotalCount.HasValue
                ? DateTimeOffset.UtcNow
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

    public void SetSyncTargets(IReadOnlyList<SyncService.WatchLaterVideo> syncTargets)
    {
        var normalizedTargets = NormalizeSyncTargets(syncTargets);
        var normalizedVideoIds = normalizedTargets.Select(target => target.VideoId).ToList();
        lock (_syncRoot)
        {
            if (HasSameSyncTargets(_pendingSyncVideos, normalizedTargets))
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _syncTargetVideoIds = normalizedVideoIds;
            _pendingSyncVideos = normalizedTargets;
            _libraryVersion++;
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
            _pendingSyncVideos = [];
            _libraryVersion++;
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<PendingVideo> GetPendingSyncVideosSnapshot()
    {
        lock (_syncRoot)
        {
            return _pendingSyncVideos.ToList();
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
                _watchLaterTotalUpdatedAtUtc,
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
        DateTimeOffset? WatchLaterTotalUpdatedAtUtc,
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

    private static List<PendingVideo> NormalizeSyncTargets(IEnumerable<SyncService.WatchLaterVideo> syncTargets)
    {
        var normalized = new List<PendingVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in syncTargets)
        {
            var videoId = target.VideoId?.Trim();
            if (string.IsNullOrWhiteSpace(videoId) || !seen.Add(videoId))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(target.Title) ? "Untitled video" : target.Title.Trim();
            normalized.Add(new PendingVideo(videoId, title));
        }

        return normalized;
    }

    private static bool HasSameVideoIds(
        IReadOnlySet<string> left,
        IReadOnlySet<string> right)
    {
        return left.SetEquals(right);
    }

    private static bool HasSameSyncTargets(
        IReadOnlyList<PendingVideo> left,
        IReadOnlyList<PendingVideo> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    internal readonly record struct PendingVideo(string VideoId, string Title);

    internal readonly record struct SyncScopeCounts(
        int? DownloadedCount,
        int? TargetCount,
        int? FailedCount);
}
