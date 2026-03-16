namespace YouTubeSyncTray;

internal sealed class LibraryBrowserState
{
    private const int MaxRecentMessages = 8;

    private readonly object _syncRoot = new();
    private readonly Queue<string> _recentMessages = new();
    private bool _isBusy;
    private string _status;
    private int _videoCount;
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
        int DownloadCount,
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
}
