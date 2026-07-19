using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YouTubeSyncTray;

internal sealed class SyncService
{
    private const string HighestQualityFormatSelector =
        "bv*[ext=mp4][protocol^=http]+ba[ext=m4a][protocol^=http]/" +
        "bv*[ext=webm][protocol^=http]+ba[ext=webm][protocol^=http]/" +
        "b[ext=mp4][protocol^=http]/b[ext=webm][protocol^=http]/" +
        "bv*[protocol^=http]+ba[protocol^=http]/b[protocol^=http]/" +
        "bv*[ext=mp4]+ba[ext=m4a]/bv*[ext=webm]+ba[ext=webm]/b[ext=mp4]/b[ext=webm]/bv*+ba/b";
    private const int YtDlpRetryCount = 3;

    private readonly YoutubeSyncPaths _paths;
    private readonly ChromiumCookieExporter _chromiumCookieExporter;
    private readonly BrowserAccountDiscoveryService _accountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly AccountScopeResolver _accountScopeResolver;
    private readonly CookieExportMetadataStore _cookieExportMetadataStore;
    private readonly KnownLibraryScopeStore _knownLibraryScopeStore;
    private readonly LibraryCatalogStore _libraryCatalogStore;
    private readonly IBrowserLoginPrompt? _browserLoginPrompt;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _freshCookieRetryAttempted;
    private AuthConfig? _refreshedAuth;
    private string? _lastFreshCookieRetryStatus;

    public SyncService(
        YoutubeSyncPaths paths,
        IBrowserLoginPrompt? browserLoginPrompt = null,
        KnownLibraryScopeStore? knownLibraryScopeStore = null,
        LibraryCatalogStore? libraryCatalogStore = null)
    {
        _paths = paths;
        _chromiumCookieExporter = new ChromiumCookieExporter(paths);
        _knownLibraryScopeStore = knownLibraryScopeStore ?? new KnownLibraryScopeStore(paths);
        _libraryCatalogStore = libraryCatalogStore ?? new LibraryCatalogStore(paths);
        _accountDiscovery = new BrowserAccountDiscoveryService(_knownLibraryScopeStore);
        _youTubeAccountDiscovery = new YouTubeAccountDiscoveryService(paths, _knownLibraryScopeStore);
        _accountScopeResolver = new AccountScopeResolver(paths, _accountDiscovery, _youTubeAccountDiscovery, _knownLibraryScopeStore);
        _cookieExportMetadataStore = new CookieExportMetadataStore(paths);
        _browserLoginPrompt = browserLoginPrompt;
    }

    public Task<AuthConfig> EnsureAuthAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settings.Normalize();
        TrayLog.Write(_paths, $"EnsureAuthAsync using {settings.BrowserCookies}:{settings.BrowserProfile}");
        _freshCookieRetryAttempted = false;
        _refreshedAuth = null;
        _lastFreshCookieRetryStatus = null;
        return Task.FromResult(AuthConfig.ForBrowser(settings.BrowserCookies, settings.BrowserProfile));
    }

    public async Task<int> GetWatchLaterTotalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            settings.Normalize();
            var auth = await EnsureAuthAsync(settings, cancellationToken);
            auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress: null, cancellationToken);
            return await GetWatchLaterTotalAsync(settings, auth, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetWatchLaterOrderedIdsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            settings.Normalize();
            var auth = await EnsureAuthAsync(settings, cancellationToken);
            auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress: null, cancellationToken);
            var snapshot = await GetWatchLaterSnapshotAsync(settings, auth, cancellationToken);
            return BuildMostRecentFirstWatchLaterIds(snapshot.Videos.Select(video => video.VideoId).ToList());
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<int> GetWatchLaterTotalAsync(
        AppSettings settings,
        AuthConfig auth,
        CancellationToken cancellationToken)
    {
        settings.Normalize();

        return (await GetWatchLaterSnapshotAsync(settings, auth, cancellationToken)).TotalCount;
    }

    private async Task<WatchLaterSnapshot> GetWatchLaterSnapshotAsync(
        AppSettings settings,
        AuthConfig auth,
        CancellationToken cancellationToken)
    {
        settings.Normalize();
        var watchLaterUrl = BuildWatchLaterUrl(settings);
        var result = await RunYtDlpWithRetryAsync(
            settings,
            auth,
            $"{BuildYouTubeExtractorArguments()} --flat-playlist --dump-single-json \"{watchLaterUrl}\"",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var effectiveAuth = _refreshedAuth ?? auth;
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "Could not inspect the current Watch Later playlist.",
                effectiveAuth,
                result));
        }

        var snapshot = ParseWatchLaterSnapshotJson(result.StdOut);
        if (!snapshot.HasValue)
        {
            throw new InvalidOperationException("yt-dlp did not return valid Watch Later playlist metadata.");
        }

        TrayLog.Write(
            _paths,
            $"Watch Later snapshot returned {snapshot.Value.Videos.Count} entries and total {snapshot.Value.TotalCount}.");
        return snapshot.Value;
    }

    public async Task<SyncSummary> SyncRecentAsync(
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<int>? watchLaterTotalProgress = null,
        IProgress<IReadOnlyList<string>>? watchLaterOrderProgress = null,
        IProgress<IReadOnlyList<WatchLaterVideo>>? syncTargetProgress = null)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await SyncRecentCoreAsync(
                settings,
                progress,
                cancellationToken,
                watchLaterTotalProgress,
                watchLaterOrderProgress,
                syncTargetProgress);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<SyncSummary> SyncRecentCoreAsync(
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<int>? watchLaterTotalProgress,
        IProgress<IReadOnlyList<string>>? watchLaterOrderProgress,
        IProgress<IReadOnlyList<WatchLaterVideo>>? syncTargetProgress)
    {
        settings.Normalize();
        TrayLog.Write(_paths, $"SyncRecentAsync started. Requested count: {settings.DownloadCount}");
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress, cancellationToken);
        var accountScope = _accountScopeResolver.Resolve(settings);
        var clampedCount = Math.Clamp(settings.DownloadCount, 1, 5000);
        var watchLaterUrl = BuildWatchLaterUrl(settings);
        progress?.Report("Refreshing the Watch Later snapshot...");
        var watchLaterSnapshot = await GetWatchLaterSnapshotAsync(settings, auth, cancellationToken);
        var watchLaterTotalCount = watchLaterSnapshot.TotalCount;
        watchLaterTotalProgress?.Report(watchLaterTotalCount);

        progress?.Report("Checking downloaded videos before probing Watch Later...");
        var existingItemsBefore = _libraryCatalogStore.LoadOrScan(accountScope.FolderName, accountScope.DownloadsPath);
        var existingIdsBefore = existingItemsBefore
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
        _knownLibraryScopeStore.UpdateScopeInventory(accountScope, existingIdsBefore.Count);
        TrayLog.Write(_paths, $"Existing on-disk video count before sync: {existingIdsBefore.Count}");
        var completeWatchLaterOrder = BuildMostRecentFirstWatchLaterIds(
            watchLaterSnapshot.Videos.Select(video => video.VideoId).ToList());
        if (completeWatchLaterOrder.Count > 0)
        {
            watchLaterOrderProgress?.Report(completeWatchLaterOrder);
        }

        var targetVideos = watchLaterSnapshot.Videos
            .Take(clampedCount)
            .ToList();
        syncTargetProgress?.Report(targetVideos);
        var targetIds = targetVideos.Select(video => video.VideoId).ToList();

        TrayLog.Write(_paths, $"Target id count: {targetIds.Count}");
        if (targetIds.Count == 0)
        {
            return new SyncSummary(
                RequestedCount: clampedCount,
                TargetCount: 0,
                DownloadedCount: 0,
                AlreadyPresentCount: 0,
                ArchiveRepairedCount: 0,
                MissingAfterSyncCount: 0,
                WatchLaterTotalCount: watchLaterTotalCount,
                NonFatalIssue: null,
                NonFatalIssueDetail: null,
                LogPath: null);
        }

        var missingTargetIds = targetIds
            .Where(id => !existingIdsBefore.Contains(id))
            .ToList();
        var repairedArchiveCount = RepairArchiveEntriesForMissingVideos(accountScope.ArchivePath, missingTargetIds);
        TrayLog.Write(_paths, $"Missing target count before sync: {missingTargetIds.Count}. Archive entries repaired: {repairedArchiveCount}");
        progress?.Report($"{missingTargetIds.Count} of {targetIds.Count} Watch Later videos are not on disk. Starting yt-dlp...");

        var targetBatchPath = Path.Combine(_paths.TempPath, $"sync-targets-{Guid.NewGuid():N}.txt");
        AtomicFile.WriteAllText(
            targetBatchPath,
            string.Join(Environment.NewLine, targetIds.Select(BuildVideoUrl)) + Environment.NewLine,
            retainJsonBackup: false);

        ProcessResult result;
        try
        {
            var args = new StringBuilder();
            args.Append(BuildYouTubeExtractorArguments());
            args.Append(" --progress --newline");
            args.Append(BuildRetryArguments());
            args.Append(" --retry-sleep http:exp=1:20 --retry-sleep fragment:exp=1:20");
            args.Append(" --sleep-requests 1 --sleep-interval 2 --max-sleep-interval 8 --concurrent-fragments 4");
            args.Append($" --batch-file \"{targetBatchPath}\"");
            args.Append($" --download-archive \"{accountScope.ArchivePath}\"");
            args.Append($" --paths home:\"{accountScope.DownloadsPath}\" --paths temp:\"{accountScope.DownloadsPath}\"");
            args.Append(" --output \"%(title).200B [%(id)s].%(ext)s\"");
            args.Append(" --windows-filenames --continue --part --no-overwrites --no-mtime --write-info-json --write-thumbnail");
            args.Append(BuildSubtitleDownloadArguments());
            args.Append(BuildHighestQualityFormatArguments());

            result = await RunYtDlpWithRetryAsync(settings, auth, args.ToString(), cancellationToken, progress);
        }
        finally
        {
            TryDeleteFile(targetBatchPath);
        }
        TrayLog.Write(_paths, $"yt-dlp sync exit code: {result.ExitCode}");
        string? nonFatalIssue = null;
        string? nonFatalIssueDetail = null;
        var effectiveAuth = _refreshedAuth ?? auth;
        var syncLogPath = WriteLatestSyncLog(
            settings,
            effectiveAuth,
            clampedCount,
            watchLaterUrl,
            result,
            result.ExitCode != 0 && !LooksLikeAuthFailure(result)
                ? DescribeNonFatalSyncIssue(result)
                : null);
        if (result.ExitCode != 0)
        {
            if (LooksLikeAuthFailure(result))
            {
                throw new InvalidOperationException(BuildYtDlpFailureMessage(
                    "yt-dlp sync failed.",
                    effectiveAuth,
                    result,
                    syncLogPath));
            }

            var issue = DescribeNonFatalSyncIssue(result);
            nonFatalIssue = issue.Summary;
            nonFatalIssueDetail = issue.Detail;
            if (!string.IsNullOrWhiteSpace(nonFatalIssue))
            {
                var detailSuffix = string.IsNullOrWhiteSpace(nonFatalIssueDetail)
                    ? string.Empty
                    : $" Detail: {nonFatalIssueDetail}";
                TrayLog.Write(_paths, $"yt-dlp reported non-fatal item errors: {nonFatalIssue}.{detailSuffix}");
            }
        }

        var existingItemsAfter = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
        var existingIdsAfter = existingItemsAfter
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
        _knownLibraryScopeStore.UpdateScopeInventory(accountScope, existingIdsAfter.Count, DateTimeOffset.UtcNow);
        var downloadedIds = targetIds
            .Where(id => existingIdsAfter.Contains(id) && !existingIdsBefore.Contains(id))
            .ToList();
        var presentIds = targetIds
            .Where(id => existingIdsAfter.Contains(id))
            .ToList();
        var stillMissingCount = targetIds.Count(id => !existingIdsAfter.Contains(id));
        TrayLog.Write(_paths, $"Sync finished. Downloaded: {downloadedIds.Count}, already present: {presentIds.Count - downloadedIds.Count}, still missing: {stillMissingCount}");

        return new SyncSummary(
            RequestedCount: clampedCount,
            TargetCount: targetIds.Count,
            DownloadedCount: downloadedIds.Count,
            AlreadyPresentCount: presentIds.Count - downloadedIds.Count,
            ArchiveRepairedCount: repairedArchiveCount,
            MissingAfterSyncCount: stillMissingCount,
            WatchLaterTotalCount: watchLaterTotalCount,
            NonFatalIssue: nonFatalIssue,
            NonFatalIssueDetail: nonFatalIssueDetail,
            LogPath: syncLogPath);
    }

    public async Task<RedownloadSummary> RedownloadVideosAsync(
        AppSettings settings,
        IReadOnlyList<string> videoIds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await RedownloadVideosCoreAsync(settings, videoIds, progress, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RedownloadSummary> RedownloadVideosCoreAsync(
        AppSettings settings,
        IReadOnlyList<string> videoIds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        settings.Normalize();
        var normalizedIds = videoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedIds.Count == 0)
        {
            return new RedownloadSummary(0, 0, 0, null);
        }

        TrayLog.Write(_paths, $"RedownloadVideosAsync started for {normalizedIds.Count} video(s).");
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress, cancellationToken);
        var accountScope = _accountScopeResolver.Resolve(settings);
        Directory.CreateDirectory(accountScope.DownloadsPath);

        var existingItemsBefore = _libraryCatalogStore.LoadOrScan(accountScope.FolderName, accountScope.DownloadsPath);
        var existingItemsById = existingItemsBefore
            .GroupBy(item => item.VideoId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var backupRoot = Path.Combine(_paths.TempPath, "redownload-backups", Guid.NewGuid().ToString("N"));
        List<RedownloadBackup> backups = [];
        var backupCompleted = false;
        var canDeleteBackupRoot = false;

        try
        {
            progress?.Report($"Preparing {normalizedIds.Count} video(s) for redownload...");
            BackupExistingVideoBundles(
                normalizedIds
                    .Where(existingItemsById.ContainsKey)
                    .Select(id => existingItemsById[id]),
                backupRoot,
                backups);
            backupCompleted = true;
            RemoveArchiveEntries(accountScope.ArchivePath, normalizedIds);

            var args = new StringBuilder();
            args.Append(BuildYouTubeExtractorArguments());
            args.Append(" --progress --newline");
            args.Append(BuildRetryArguments());
            args.Append(" --retry-sleep http:exp=1:20 --retry-sleep fragment:exp=1:20");
            args.Append(" --sleep-requests 1 --sleep-interval 2 --max-sleep-interval 8 --concurrent-fragments 4");
            args.Append($" --download-archive \"{accountScope.ArchivePath}\"");
            args.Append($" --paths home:\"{accountScope.DownloadsPath}\" --paths temp:\"{accountScope.DownloadsPath}\"");
            args.Append(" --output \"%(title).200B [%(id)s].%(ext)s\"");
            args.Append(" --windows-filenames --continue --part --no-overwrites --no-mtime --write-info-json --write-thumbnail");
            args.Append(BuildSubtitleDownloadArguments());

            args.Append(BuildHighestQualityFormatArguments());
            foreach (var videoId in normalizedIds)
            {
                args.Append($" \"{BuildVideoUrl(videoId)}\"");
            }

            progress?.Report($"Redownloading {normalizedIds.Count} video(s) at best quality...");
            var result = await RunYtDlpWithRetryAsync(settings, auth, args.ToString(), cancellationToken, progress);
            string? nonFatalIssue = null;
            if (result.ExitCode != 0)
            {
                var effectiveAuth = _refreshedAuth ?? auth;
                if (LooksLikeAuthFailure(result))
                {
                    throw new InvalidOperationException(BuildYtDlpFailureMessage(
                        "yt-dlp redownload failed.",
                        effectiveAuth,
                        result));
                }

                nonFatalIssue = DescribeNonFatalSyncIssue(result).Summary;
                if (!string.IsNullOrWhiteSpace(nonFatalIssue))
                {
                    TrayLog.Write(_paths, $"yt-dlp reported non-fatal redownload issues: {nonFatalIssue}");
                }
            }

            var itemsAfterDownload = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
            PreservePlaylistIndexesForRedownloadedVideos(existingItemsById, itemsAfterDownload);

            var itemsAfterMetadataRepair = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
            var presentAfterDownload = itemsAfterMetadataRepair
                .Select(item => item.VideoId)
                .ToHashSet(StringComparer.Ordinal);
            var downloadedIds = normalizedIds
                .Where(presentAfterDownload.Contains)
                .ToList();
            var failedIds = normalizedIds
                .Where(id => !presentAfterDownload.Contains(id))
                .ToList();

            if (failedIds.Count > 0)
            {
                var failedIdSet = failedIds.ToHashSet(StringComparer.Ordinal);
                RestoreBackups(backups.Where(backup => failedIdSet.Contains(backup.VideoId)));
                var itemsAfterRestore = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
                var restoredIds = itemsAfterRestore
                    .Select(item => item.VideoId)
                    .Where(failedIdSet.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                EnsureArchiveEntries(accountScope.ArchivePath, restoredIds);
                _knownLibraryScopeStore.UpdateScopeInventory(accountScope, itemsAfterRestore.Count, DateTimeOffset.UtcNow);
            }
            else
            {
                _knownLibraryScopeStore.UpdateScopeInventory(accountScope, itemsAfterMetadataRepair.Count, DateTimeOffset.UtcNow);
            }

            TrayLog.Write(_paths, $"Redownload finished. Requested: {normalizedIds.Count}, downloaded: {downloadedIds.Count}, failed: {failedIds.Count}");
            canDeleteBackupRoot = true;
            return new RedownloadSummary(
                RequestedCount: normalizedIds.Count,
                RedownloadedCount: downloadedIds.Count,
                FailedCount: failedIds.Count,
                NonFatalIssue: nonFatalIssue);
        }
        catch (Exception operationException)
        {
            try
            {
                if (backupCompleted)
                {
                    RestoreMissingBackups(accountScope, backups);
                }
                else
                {
                    // Backup creation itself failed. Restore every move recorded so far,
                    // including sidecars for a video whose media file never moved.
                    RestoreBackups(backups);
                }

                canDeleteBackupRoot = true;
            }
            catch (Exception restoreException)
            {
                TrayLog.Write(
                    _paths,
                    $"Redownload failed and backup restoration also failed. Backups retained at {backupRoot}. Restore error: {restoreException}");
                throw new AggregateException(
                    $"Redownload failed and the previous files could not be fully restored. Recoverable backups were retained at {backupRoot}.",
                    operationException,
                    restoreException);
            }

            throw;
        }
        finally
        {
            if (canDeleteBackupRoot)
            {
                TryDeleteDirectory(backupRoot);
            }
        }
    }

    private async Task<AuthConfig> PrepareManagedChromiumAuthAsync(
        AppSettings settings,
        AuthConfig auth,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!RequiresManagedChromiumCookies(auth))
        {
            return auth;
        }

        if (HasSavedCookieFile(auth))
        {
            _refreshedAuth = AuthConfig.ForCookieFile(_paths.CookiesPath, auth.Browser, auth.Profile);
            progress?.Report($"Using the saved {DescribeBrowser(auth.Browser)} cookie export.");
            TrayLog.Write(_paths, $"Using saved cookies file {_paths.CookiesPath} for {auth.Browser}:{auth.Profile}.");
            return _refreshedAuth.Value;
        }

        progress?.Report($"No saved {DescribeBrowser(auth.Browser)} cookies were found. Opening the managed browser sign-in flow...");
        var refreshedAuth = await TryRefreshCookiesAsync(settings, cancellationToken, progress);
        if (refreshedAuth.HasValue)
        {
            return refreshedAuth.Value;
        }

        throw new InvalidOperationException(
            _lastFreshCookieRetryStatus ??
            $"Managed {DescribeBrowser(auth.Browser)} sign-in did not complete, so YouTube access could not be prepared.");
    }

    private string BuildCommonArgs(AuthConfig auth, AppSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("--js-runtimes node --remote-components ejs:github");
        if (auth.Mode == AuthMode.Browser)
        {
            builder.Append($" --cookies-from-browser {DescribeBrowser(auth.Browser)}:{EscapeCookieProfile(auth.Profile)}");
        }
        else
        {
            builder.Append($" --cookies \"{auth.CookiesPath}\"");
        }
        var ffmpegLocation = GetFfmpegLocation();
        if (!string.IsNullOrWhiteSpace(ffmpegLocation))
        {
            builder.Append($" --ffmpeg-location \"{ffmpegLocation}\"");
        }

        var selectedBrowserAuthUserIndex = _accountDiscovery.ResolveSelectedAuthUserIndex(settings);
        var selectedYouTubeAccount = _youTubeAccountDiscovery.ResolveSelectedAccount(settings, selectedBrowserAuthUserIndex);
        if (selectedYouTubeAccount.HasValue)
        {
            builder.Append($" --add-header \"X-Goog-AuthUser: {selectedYouTubeAccount.Value.AuthUserIndex}\"");
            if (!string.IsNullOrWhiteSpace(selectedYouTubeAccount.Value.PageId))
            {
                builder.Append($" --add-header \"X-Goog-PageId: {selectedYouTubeAccount.Value.PageId}\"");
            }
        }
        else
        {
            builder.Append($" --add-header \"X-Goog-AuthUser: {selectedBrowserAuthUserIndex}\"");
        }

        return builder.ToString();
    }

    internal static string BuildHighestQualityFormatArguments()
    {
        // Reset any earlier/custom sort so yt-dlp can choose the best available streams.
        return $" --format-sort-reset --format \"{HighestQualityFormatSelector}\"";
    }

    internal static string BuildYouTubeExtractorArguments() =>
        " --extractor-args \"youtube:lang=en;formats=missing_pot\"";

    internal static string BuildSubtitleDownloadArguments()
    {
        // The browser player already loads caption sidecars separately; embedding subtitles
        // tends to push yt-dlp/ffmpeg toward mkv outputs, which are less reliable in HTML5 playback.
        return " --write-subs --write-auto-subs --sub-langs \"en.*,en,-live_chat\" --convert-subs srt";
    }

    internal static string BuildRetryArguments() =>
        $" --ignore-errors --no-abort-on-error --retries {YtDlpRetryCount} --fragment-retries {YtDlpRetryCount} --file-access-retries {YtDlpRetryCount}";

    private string? GetFfmpegLocation()
    {
        if (string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
        {
            return null;
        }

        var binPath = Path.Combine(_paths.FfmpegRootPath, "bin");
        if (Directory.Exists(binPath))
        {
            return binPath;
        }

        return _paths.FfmpegRootPath;
    }

    private static string DescribeBrowser(BrowserCookieSource browser)
    {
        return browser switch
        {
            BrowserCookieSource.Firefox => "firefox",
            BrowserCookieSource.Edge => "edge",
            BrowserCookieSource.Chrome => "chrome",
            _ => "firefox"
        };
    }

    private static string EscapeCookieProfile(string profile)
    {
        return profile.Replace('"', '_');
    }

    private async Task<ProcessResult> RunYtDlpWithRetryAsync(
        AppSettings settings,
        AuthConfig auth,
        string arguments,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        auth = auth.Mode == AuthMode.CookieFile ? auth : _refreshedAuth ?? auth;
        var result = await RunProcessAsync(
            _paths.YtDlpPath,
            $"{BuildCommonArgs(auth, settings)} {arguments}",
            _paths.RootPath,
            cancellationToken,
            progress);

        if (!LooksLikeAuthFailure(result))
        {
            return result;
        }

        var refreshedAuth = await TryRefreshCookiesAsync(settings, cancellationToken, progress);
        if (refreshedAuth is null)
        {
            return result;
        }

        progress?.Report("Authentication failed; retrying with freshly exported cookies...");
        return await RunProcessAsync(
            _paths.YtDlpPath,
            $"{BuildCommonArgs(refreshedAuth.Value, settings)} {arguments}",
            _paths.RootPath,
            cancellationToken,
            progress);
    }

    private async Task<AuthConfig?> TryRefreshCookiesAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_freshCookieRetryAttempted)
        {
            return null;
        }

        settings.Normalize();
        if (!ChromiumBrowserLocator.SupportsManagedProfile(settings.BrowserCookies))
        {
            return null;
        }

        var availableBrowsers = ChromiumBrowserLocator.GetInstalledBrowsers();
        if (availableBrowsers.Count == 0)
        {
            _lastFreshCookieRetryStatus =
                "Fresh cookie export was skipped because Google Chrome or Microsoft Edge was not detected on this PC.";
            TrayLog.Write(_paths, "Fresh cookie export skipped because no supported Chromium browser was detected.");
            return null;
        }

        var selectedBrowser = await ResolveBrowserForFreshCookieExportAsync(settings, availableBrowsers, cancellationToken);
        if (!selectedBrowser.HasValue)
        {
            _lastFreshCookieRetryStatus = "Fresh cookie export was cancelled before the browser window was opened.";
            TrayLog.Write(_paths, "Fresh cookie export cancelled by the user before launching the managed browser.");
            return null;
        }

        _freshCookieRetryAttempted = true;
        progress?.Report($"Authentication failed; attempting fresh {DescribeBrowser(selectedBrowser.Value)} cookie export...");
        TrayLog.Write(_paths, $"Attempting fresh cookie export for {selectedBrowser.Value}:{settings.BrowserProfile}");

        try
        {
            progress?.Report($"Opening app-managed {DescribeBrowser(selectedBrowser.Value)} for YouTube sign-in. Close the browser window when sign-in is complete.");
            await _chromiumCookieExporter.PrimeProfileAsync(
                selectedBrowser.Value,
                settings.BrowserProfile,
                progress,
                cancellationToken);

            var exportResult = await _chromiumCookieExporter.ExportAsync(
                selectedBrowser.Value,
                settings.BrowserProfile,
                progress,
                cancellationToken);

            TrayLog.Write(_paths, $"Fresh cookie export succeeded. Cookies path: {exportResult.CookiesPath}; cookies={exportResult.CookieCount}");
            _lastFreshCookieRetryStatus = $"Fresh cookie export succeeded and produced {exportResult.CookiesPath}.";
            _refreshedAuth = AuthConfig.ForCookieFile(exportResult.CookiesPath, selectedBrowser.Value, settings.BrowserProfile);
            return _refreshedAuth;
        }
        catch (Exception ex)
        {
            _lastFreshCookieRetryStatus = "Fresh cookie export was attempted but failed." +
                Environment.NewLine +
                ex.Message;
            TrayLog.Write(_paths, $"Fresh cookie export failed: {ex}");
            return null;
        }
    }

    private async Task<BrowserCookieSource?> ResolveBrowserForFreshCookieExportAsync(
        AppSettings settings,
        IReadOnlyList<BrowserCookieSource> availableBrowsers,
        CancellationToken cancellationToken)
    {
        var defaultBrowser = ChromiumBrowserLocator.GetDefaultBrowser(availableBrowsers);
        var initialBrowser = defaultBrowser
            ?? (availableBrowsers.Contains(settings.BrowserCookies) ? settings.BrowserCookies : availableBrowsers[0]);

        if (_browserLoginPrompt is null)
        {
            return availableBrowsers.Contains(settings.BrowserCookies)
                ? settings.BrowserCookies
                : initialBrowser;
        }

        var request = new BrowserLoginPromptRequest(availableBrowsers, initialBrowser, settings.BrowserProfile);
        var decision = await _browserLoginPrompt.ConfirmAsync(request, cancellationToken);
        return decision?.SelectedBrowser;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        var outputGate = new object();
        var stoppedForAuthFailure = false;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var shouldStop = false;
            lock (outputGate)
            {
                stdOut.AppendLine(e.Data);
                if (!stoppedForAuthFailure && LooksLikeAuthFailureOutput(e.Data))
                {
                    stoppedForAuthFailure = true;
                    shouldStop = true;
                }
            }

            progress?.Report(e.Data);
            if (shouldStop)
            {
                TryStopProcess(process);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var shouldStop = false;
            lock (outputGate)
            {
                stdErr.AppendLine(e.Data);
                if (!stoppedForAuthFailure && LooksLikeAuthFailureOutput(e.Data))
                {
                    stoppedForAuthFailure = true;
                    shouldStop = true;
                }
            }

            progress?.Report(e.Data);
            if (shouldStop)
            {
                TryStopProcess(process);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancellationRegistration = cancellationToken.Register(() => TryStopProcess(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryStopProcess(process);
            }
        }

        lock (outputGate)
        {
            return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // Best-effort cleanup during cancellation/shutdown.
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public string CombinedOutput => StdOut + Environment.NewLine + StdErr;

        public string GetTail()
        {
            var combined = CombinedOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(25);
            return string.Join(Environment.NewLine, combined);
        }
    }

    private string BuildYtDlpFailureMessage(string prefix, AuthConfig auth, ProcessResult result, string? logPath = null)
    {
        var details = new List<string> { prefix };
        details.Add($"Authentication source: {DescribeAuth(auth)}");

        var isAuthFailure = LooksLikeAuthFailure(result);
        if (isAuthFailure)
        {
            details.Add(
                "YouTube rejected the Watch Later request. This usually means the selected browser profile is not signed into YouTube or the browser cookies could not be read by yt-dlp.");
        }

        if (isAuthFailure && auth.Browser is BrowserCookieSource.Chrome or BrowserCookieSource.Edge)
        {
            details.Add(
                "Chrome and Edge on Windows no longer expose the live default profile reliably. When needed, the app opens an app-managed browser profile and exports fresh cookies from that session.");
            details.Add(
                $"Open the app-managed {DescribeBrowser(auth.Browser)} window when prompted, sign into YouTube again, then retry the sync.");
        }

        if (isAuthFailure && !string.IsNullOrWhiteSpace(_lastFreshCookieRetryStatus))
        {
            details.Add(_lastFreshCookieRetryStatus);
        }

        details.Add(result.GetTail());
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            details.Add($"Full sync log: {logPath}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, details);
    }

    private static string DescribeAuth(AuthConfig auth)
    {
        return auth.Mode switch
        {
            AuthMode.CookieFile when !string.IsNullOrWhiteSpace(auth.Profile)
                => $"cookies file at {auth.CookiesPath} refreshed from {DescribeBrowser(auth.Browser)} profile '{auth.Profile}'",
            AuthMode.CookieFile => $"cookies file at {auth.CookiesPath}",
            _ => $"browser cookies from {DescribeBrowser(auth.Browser)} profile '{auth.Profile}'"
        };
    }

    private string WriteLatestSyncLog(
        AppSettings settings,
        AuthConfig auth,
        int requestedCount,
        string watchLaterUrl,
        ProcessResult result,
        NonFatalSyncIssue? issue)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsPath);
            var logPath = Path.Combine(_paths.LogsPath, "latest-sync.log");
            var builder = new StringBuilder();
            builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine("Operation: Watch Later sync");
            builder.AppendLine($"Requested count: {requestedCount}");
            builder.AppendLine($"Browser: {settings.BrowserCookies}:{settings.BrowserProfile}");
            builder.AppendLine($"Watch Later URL: {watchLaterUrl}");
            builder.AppendLine($"Authentication source: {DescribeAuth(auth)}");
            builder.AppendLine($"yt-dlp exit code: {result.ExitCode}");
            if (LooksLikeAuthFailure(result))
            {
                builder.AppendLine("Detected issue type: authentication");
            }
            else if (issue.HasValue)
            {
                builder.AppendLine($"Detected issue summary: {issue.Value.Summary}");
                if (!string.IsNullOrWhiteSpace(issue.Value.Detail))
                {
                    builder.AppendLine($"Detected issue detail: {issue.Value.Detail}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("STDOUT");
            builder.AppendLine("------");
            builder.AppendLine(string.IsNullOrWhiteSpace(result.StdOut) ? "<empty>" : result.StdOut.TrimEnd());
            builder.AppendLine();
            builder.AppendLine("STDERR");
            builder.AppendLine("------");
            builder.AppendLine(string.IsNullOrWhiteSpace(result.StdErr) ? "<empty>" : result.StdErr.TrimEnd());
            File.WriteAllText(logPath, builder.ToString());
            return logPath;
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Could not write latest sync log: {ex}");
            return string.Empty;
        }
    }

    private static NonFatalSyncIssue DescribeNonFatalSyncIssue(ProcessResult result) =>
        DescribeNonFatalSyncIssueOutput(result.CombinedOutput);

    internal static NonFatalSyncIssue DescribeNonFatalSyncIssueOutput(string combinedOutput)
    {
        var summary = combinedOutput.Contains("drm protected", StringComparison.OrdinalIgnoreCase)
            || combinedOutput.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                ? "Some videos were skipped because YouTube only exposed DRM-protected or unavailable formats."
                : combinedOutput.Contains("Did not get any data blocks", StringComparison.OrdinalIgnoreCase)
                    || combinedOutput.Contains("fragment not found", StringComparison.OrdinalIgnoreCase)
                    || combinedOutput.Contains("HTTP Error", StringComparison.OrdinalIgnoreCase)
                        ? "Some videos were skipped because YouTube rejected yt-dlp's media download URLs even though the playlist metadata was accessible."
                        : "yt-dlp reported item-level errors, but the successfully downloaded videos were kept.";
        return new NonFatalSyncIssue(summary, ExtractMostRelevantIssueDetail(combinedOutput));
    }

    private static string? ExtractMostRelevantIssueDetail(string combinedOutput)
    {
        if (string.IsNullOrWhiteSpace(combinedOutput))
        {
            return null;
        }

        string? bestLine = null;
        var bestScore = int.MinValue;
        var lines = combinedOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var normalized = NormalizeIssueDetailLine(lines[index]);
            if (string.IsNullOrWhiteSpace(normalized) || !LooksLikeMeaningfulIssueLine(normalized))
            {
                continue;
            }

            var score = ScoreIssueDetailLine(normalized) + index;
            if (score < bestScore)
            {
                continue;
            }

            bestScore = score;
            bestLine = normalized;
        }

        return string.IsNullOrWhiteSpace(bestLine) ? null : TruncateIssueDetail(bestLine);
    }

    private static string NormalizeIssueDetailLine(string line)
    {
        var normalized = line.Trim();
        if (normalized.StartsWith("[download] ", StringComparison.Ordinal))
        {
            normalized = normalized["[download] ".Length..].Trim();
        }

        var gotErrorIndex = normalized.IndexOf("Got error:", StringComparison.OrdinalIgnoreCase);
        if (gotErrorIndex >= 0)
        {
            normalized = normalized[(gotErrorIndex + "Got error:".Length)..].Trim();
        }

        var retryingIndex = normalized.IndexOf("Retrying fragment", StringComparison.OrdinalIgnoreCase);
        if (retryingIndex > 0)
        {
            normalized = normalized[..retryingIndex].TrimEnd('.', ' ');
        }

        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool LooksLikeMeaningfulIssueLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.StartsWith("[download]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Deleting original file", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Merger", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || line.Contains("drm protected", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Did not get any data blocks", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fragment not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("HTTP Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This video is unavailable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreIssueDetailLine(string line)
    {
        var score = 0;
        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        else if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
        {
            score += 70;
        }

        if (line.Contains("[youtube]", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (line.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This video is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }

        if (line.Contains("drm protected", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Did not get any data blocks", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fragment not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("HTTP Error", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }

    private static string TruncateIssueDetail(string value)
    {
        const int maxLength = 180;
        return value.Length <= maxLength
            ? value.TrimEnd('.', ' ')
            : value[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static bool LooksLikeAuthFailure(ProcessResult result) =>
        LooksLikeAuthFailureOutput(result.CombinedOutput);

    internal static bool LooksLikeAuthFailureOutput(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(LooksLikeAuthFailureLine);
    }

    private static bool LooksLikeAuthFailureLine(string line)
    {
        if (!line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (line.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || line.Contains("requested content is not available", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This video is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains("playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Please sign in", StringComparison.OrdinalIgnoreCase)
            || line.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This video may be inappropriate", StringComparison.OrdinalIgnoreCase)
            || LooksLikeCookieFailureLine(line);
    }

    private static bool LooksLikeCookieFailureLine(string line)
    {
        return line.Contains("failed to decrypt cookies", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to decrypt cookie", StringComparison.OrdinalIgnoreCase)
            || line.Contains("dpapi cookie", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to load cookies", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to read cookies", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unable to load cookies", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unable to read cookies", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cookies are expired", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cookies have expired", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cookies are invalid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not copy chrome cookie database", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not copy edge cookie database", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresManagedChromiumCookies(AuthConfig auth) =>
        auth.Mode == AuthMode.Browser
        && (auth.Browser == BrowserCookieSource.Chrome || auth.Browser == BrowserCookieSource.Edge);

    private bool HasSavedCookieFile(AuthConfig auth)
    {
        try
        {
            var info = new FileInfo(_paths.CookiesPath);
            return info.Exists
                && info.Length > 0
                && _cookieExportMetadataStore.Matches(auth.Browser, auth.Profile);
        }
        catch
        {
            return false;
        }
    }

    internal static WatchLaterSnapshot? ParseWatchLaterSnapshotJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var jsonLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var videos = new List<WatchLaterVideo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var video = ParseWatchLaterVideoElement(entry);
                    if (video.HasValue && seen.Add(video.Value.VideoId))
                    {
                        videos.Add(video.Value);
                    }
                }
            }

            var totalCount = root.TryGetProperty("playlist_count", out var countProperty)
                && countProperty.TryGetInt32(out var parsedCount)
                ? Math.Max(parsedCount, 0)
                : videos.Count;
            totalCount = Math.Max(totalCount, videos.Count);
            return new WatchLaterSnapshot(totalCount, videos);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static WatchLaterVideo? ParseWatchLaterVideoJson(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return ParseWatchLaterVideoElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WatchLaterVideo? ParseWatchLaterVideoElement(JsonElement root)
    {
        string? videoId;
        string? title;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
        {
            videoId = root[0].ValueKind == JsonValueKind.String ? root[0].GetString()?.Trim() : null;
            title = root[1].ValueKind == JsonValueKind.String ? root[1].GetString()?.Trim() : null;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            videoId = root.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()?.Trim()
                : null;
            title = root.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String
                ? titleProperty.GetString()?.Trim()
                : null;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(videoId) || videoId.Length < 6)
        {
            return null;
        }

        return new WatchLaterVideo(videoId, string.IsNullOrWhiteSpace(title) ? "Untitled video" : title);
    }

    private static IReadOnlyList<WatchLaterVideo> BuildMostRecentFirstWatchLaterVideos(
        IReadOnlyList<WatchLaterVideo> ytDlpOrderedVideos)
    {
        var orderedIds = BuildMostRecentFirstWatchLaterIds(ytDlpOrderedVideos.Select(video => video.VideoId).ToList());
        var titlesById = ytDlpOrderedVideos.ToDictionary(video => video.VideoId, video => video.Title, StringComparer.Ordinal);
        return orderedIds.Select(videoId => new WatchLaterVideo(videoId, titlesById[videoId])).ToList();
    }

    internal static IReadOnlyList<string> BuildMostRecentFirstWatchLaterIds(IReadOnlyList<string> ytDlpOrderedIds)
    {
        if (ytDlpOrderedIds.Count == 0)
        {
            return [];
        }

        var orderedIds = new List<string>(ytDlpOrderedIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var videoId in ytDlpOrderedIds)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var normalizedVideoId = videoId.Trim();
            if (!seen.Add(normalizedVideoId))
            {
                continue;
            }

            orderedIds.Add(normalizedVideoId);
        }

        return orderedIds;
    }

    private string BuildWatchLaterUrl(AppSettings settings)
    {
        var selectedBrowserAuthUserIndex = _accountDiscovery.ResolveSelectedAuthUserIndex(settings);
        var selectedYouTubeAccount = _youTubeAccountDiscovery.ResolveSelectedAccount(settings, selectedBrowserAuthUserIndex);
        if (selectedYouTubeAccount.HasValue)
        {
            return YouTubeWatchLaterUrl.Build(selectedYouTubeAccount.Value.AuthUserIndex, selectedYouTubeAccount.Value.PageId);
        }

        return YouTubeWatchLaterUrl.Build(selectedBrowserAuthUserIndex);
    }

    private static string BuildVideoUrl(string videoId) =>
        $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";

    private static void BackupExistingVideoBundles(
        IEnumerable<VideoItem> items,
        string backupRoot,
        ICollection<RedownloadBackup> backups)
    {
        foreach (var item in items)
        {
            var sourcePaths = GetBundleFilePaths(item);
            if (sourcePaths.Count == 0)
            {
                continue;
            }

            BackupFileBundle(item.VideoId, sourcePaths, backupRoot, backups);
        }
    }

    internal static void BackupFileBundle(
        string videoId,
        IReadOnlyList<string> sourcePaths,
        string backupRoot,
        ICollection<RedownloadBackup> backups)
    {
        var itemBackupRoot = Path.Combine(backupRoot, videoId);
        Directory.CreateDirectory(itemBackupRoot);
        var backedUpFiles = new List<RedownloadBackupFile>();
        backups.Add(new RedownloadBackup(videoId, backedUpFiles));
        foreach (var sourcePath in sourcePaths)
        {
            var destinationPath = Path.Combine(itemBackupRoot, Path.GetFileName(sourcePath));
            var suffix = 1;
            while (File.Exists(destinationPath))
            {
                destinationPath = Path.Combine(itemBackupRoot, $"{suffix++}_{Path.GetFileName(sourcePath)}");
            }

            var backupFile = new RedownloadBackupFile(sourcePath, destinationPath);
            backedUpFiles.Add(backupFile);
            File.Move(sourcePath, destinationPath);
        }
    }

    private void RestoreMissingBackups(
        AccountScopeResolver.ResolvedAccountScope accountScope,
        IReadOnlyList<RedownloadBackup> backups)
    {
        if (backups.Count == 0)
        {
            return;
        }

        var currentItems = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
        var currentVideoIds = currentItems
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
        var backupsToRestore = backups
            .Where(backup => !currentVideoIds.Contains(backup.VideoId))
            .ToList();
        if (backupsToRestore.Count == 0)
        {
            return;
        }

        RestoreBackups(backupsToRestore);
        var restoredItems = _libraryCatalogStore.Refresh(accountScope.FolderName, accountScope.DownloadsPath);
        var restoredIds = restoredItems
            .Select(item => item.VideoId)
            .Where(videoId => backupsToRestore.Any(backup => string.Equals(backup.VideoId, videoId, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        EnsureArchiveEntries(accountScope.ArchivePath, restoredIds);
        _knownLibraryScopeStore.UpdateScopeInventory(accountScope, restoredItems.Count, DateTimeOffset.UtcNow);
    }

    internal static void RestoreBackups(IEnumerable<RedownloadBackup> backups)
    {
        foreach (var backup in backups)
        {
            foreach (var file in backup.Files)
            {
                if (!File.Exists(file.BackupPath))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.SourcePath)!);
                if (File.Exists(file.SourcePath))
                {
                    File.Delete(file.SourcePath);
                }

                File.Move(file.BackupPath, file.SourcePath);
            }
        }
    }

    private static IReadOnlyList<string> GetBundleFilePaths(VideoItem item)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfExists(paths, item.VideoPath);
        AddIfExists(paths, item.InfoPath);
        AddIfExists(paths, item.ThumbnailPath);
        foreach (var captionTrack in item.CaptionTracks)
        {
            AddIfExists(paths, captionTrack.SourcePath);
        }

        var basePath = ResolveBundleBasePath(item);
        var directory = Path.GetDirectoryName(basePath);
        var baseFileName = Path.GetFileName(basePath);
        if (!string.IsNullOrWhiteSpace(directory)
            && !string.IsNullOrWhiteSpace(baseFileName)
            && Directory.Exists(directory))
        {
            foreach (var path in Directory.GetFiles(directory, baseFileName + ".*", SearchOption.TopDirectoryOnly))
            {
                AddIfExists(paths, path);
            }
        }

        return paths.ToList();
    }

    private static string ResolveBundleBasePath(VideoItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.InfoPath)
            && item.InfoPath.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase))
        {
            return item.InfoPath[..^".info.json".Length];
        }

        if (!string.IsNullOrWhiteSpace(item.VideoPath))
        {
            return Path.Combine(
                Path.GetDirectoryName(item.VideoPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(item.VideoPath));
        }

        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath))
        {
            return Path.Combine(
                Path.GetDirectoryName(item.ThumbnailPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(item.ThumbnailPath));
        }

        return string.Empty;
    }

    private static void AddIfExists(ISet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            paths.Add(path);
        }
    }

    private static void PreservePlaylistIndexesForRedownloadedVideos(
        IReadOnlyDictionary<string, VideoItem> previousItemsById,
        IReadOnlyList<VideoItem> currentItems)
    {
        foreach (var item in currentItems)
        {
            if (!previousItemsById.TryGetValue(item.VideoId, out var previousItem)
                || previousItem.PlaylistIndex <= 0
                || string.IsNullOrWhiteSpace(item.InfoPath)
                || !File.Exists(item.InfoPath))
            {
                continue;
            }

            TryWritePlaylistIndex(item.InfoPath, previousItem.PlaylistIndex);
        }
    }

    private static void TryWritePlaylistIndex(string infoPath, int playlistIndex)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(infoPath)) as JsonObject;
            if (node is null)
            {
                return;
            }

            node["playlist_index"] = playlistIndex;
            File.WriteAllText(infoPath, node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Keep the fresh download even if the cosmetic playlist index metadata could not be preserved.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary backup folders.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for a unique temporary target batch.
        }
    }

    private static int RemoveArchiveEntries(string archivePath, IEnumerable<string> videoIds)
    {
        if (!File.Exists(archivePath))
        {
            return 0;
        }

        var ids = videoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        var keptLines = new List<string>();
        foreach (var line in File.ReadAllLines(archivePath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("youtube ", StringComparison.Ordinal))
            {
                var id = trimmed["youtube ".Length..];
                if (ids.Contains(id))
                {
                    removed++;
                    continue;
                }
            }

            keptLines.Add(line);
        }

        if (removed > 0)
        {
            File.WriteAllLines(archivePath, keptLines);
        }

        return removed;
    }

    private static void EnsureArchiveEntries(string archivePath, IEnumerable<string> videoIds)
    {
        var ids = videoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var existingIds = File.Exists(archivePath)
            ? File.ReadAllLines(archivePath)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("youtube ", StringComparison.Ordinal))
                .Select(line => line["youtube ".Length..])
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var linesToAdd = ids
            .Where(id => !existingIds.Contains(id))
            .Select(id => "youtube " + id)
            .ToList();
        if (linesToAdd.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (File.Exists(archivePath) && new FileInfo(archivePath).Length > 0)
        {
            File.AppendAllLines(archivePath, linesToAdd);
            return;
        }

        File.WriteAllLines(archivePath, linesToAdd);
    }

    private int RepairArchiveEntriesForMissingVideos(string archivePath, IEnumerable<string> missingVideoIds)
    {
        var removed = RemoveArchiveEntries(archivePath, missingVideoIds);
        if (removed > 0)
        {
            TrayLog.Write(_paths, $"Removed {removed} stale archive entr{(removed == 1 ? "y" : "ies")}.");
        }

        return removed;
    }

    internal enum AuthMode
    {
        Browser,
        CookieFile
    }

    internal readonly record struct AuthConfig(AuthMode Mode, BrowserCookieSource Browser, string Profile, string CookiesPath)
    {
        public static AuthConfig ForBrowser(BrowserCookieSource browser, string profile) =>
            new(AuthMode.Browser, browser, profile, string.Empty);

        public static AuthConfig ForCookieFile(string cookiesPath, BrowserCookieSource browser = BrowserCookieSource.Chrome, string profile = "") =>
            new(AuthMode.CookieFile, browser, profile, cookiesPath);
    }
    internal readonly record struct SyncSummary(
        int RequestedCount,
        int TargetCount,
        int DownloadedCount,
        int AlreadyPresentCount,
        int ArchiveRepairedCount,
        int MissingAfterSyncCount,
        int? WatchLaterTotalCount,
        string? NonFatalIssue,
        string? NonFatalIssueDetail,
        string? LogPath);

    internal readonly record struct WatchLaterVideo(string VideoId, string Title);

    internal readonly record struct WatchLaterSnapshot(int TotalCount, IReadOnlyList<WatchLaterVideo> Videos);

    internal readonly record struct RedownloadSummary(
        int RequestedCount,
        int RedownloadedCount,
        int FailedCount,
        string? NonFatalIssue);

    internal readonly record struct NonFatalSyncIssue(string Summary, string? Detail);

    internal readonly record struct RedownloadBackupFile(string SourcePath, string BackupPath);

    internal readonly record struct RedownloadBackup(string VideoId, IReadOnlyList<RedownloadBackupFile> Files);
}
