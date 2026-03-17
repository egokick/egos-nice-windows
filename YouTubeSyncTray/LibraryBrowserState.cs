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
    private Dictionary<string, int> _watchLaterOrderByVideoId = new(StringComparer.Ordinal);
    private bool _isBusy;
    private string _status;
    private SyncAuthState _syncAuthState = SyncAuthState.Missing;
    private string _syncAuthMessage = "Authentication has not been verified yet for the selected account.";
    private int _videoCount;
    private int? _watchLaterTotalCount;
    private long _libraryVersion = 1;
    private DateTimeOffset _updatedAtUtc;

    public LibraryBrowserState(int initialVideoCount)
    {
        _videoCount = initialVideoCount;
        _status = $"{initialVideoCount} downloaded videos";
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

    public void SetVideoCount(int videoCount)
    {
        lock (_syncRoot)
        {
            if (_videoCount != videoCount)
            {
                _videoCount = videoCount;
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

    public void MarkLibraryChanged(int? videoCount = null)
    {
        lock (_syncRoot)
        {
            if (videoCount.HasValue)
            {
                _videoCount = videoCount.Value;
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
            return new BrowserLibraryStatusSnapshot(
                _isBusy,
                _status,
                _videoCount,
                _libraryVersion,
                settings.DownloadCount,
                _watchLaterTotalCount,
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
        SyncAuthState SyncAuthState,
        string SyncAuthMessage,
        string BrowserName,
        string BrowserProfile,
        IReadOnlyList<string> RecentMessages,
        DateTimeOffset UpdatedAtUtc);

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
}
