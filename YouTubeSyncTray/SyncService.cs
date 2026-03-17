using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YouTubeSyncTray;

internal sealed class SyncService
{
    private const string HighestQualityFormatSelector = "bv*+ba/b";

    private readonly YoutubeSyncPaths _paths;
    private readonly ChromiumCookieExporter _chromiumCookieExporter;
    private readonly BrowserAccountDiscoveryService _accountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly AccountScopeResolver _accountScopeResolver;
    private readonly CookieExportMetadataStore _cookieExportMetadataStore;
    private readonly KnownLibraryScopeStore _knownLibraryScopeStore;
    private readonly LibraryCatalogStore _libraryCatalogStore;
    private readonly IBrowserLoginPrompt? _browserLoginPrompt;
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
        settings.Normalize();
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress: null, cancellationToken);
        return await GetWatchLaterTotalAsync(settings, auth, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetWatchLaterOrderedIdsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        settings.Normalize();
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress: null, cancellationToken);
        return await GetWatchLaterIdsAsync(
            settings,
            cancellationToken,
            auth,
            playlistItemsRange: null,
            newestFirst: true);
    }

    private async Task<int> GetWatchLaterTotalAsync(
        AppSettings settings,
        AuthConfig auth,
        CancellationToken cancellationToken)
    {
        settings.Normalize();

        var watchLaterUrl = BuildWatchLaterUrl(settings);
        var result = await RunYtDlpWithRetryAsync(
            settings,
            auth,
            $"--flat-playlist --dump-single-json \"{watchLaterUrl}\"",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var effectiveAuth = _refreshedAuth ?? auth;
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "Could not fetch Watch Later total.",
                effectiveAuth,
                result));
        }

        var jsonLine = result.StdOut
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new InvalidOperationException("yt-dlp did not return playlist metadata.");
        }

        using var document = JsonDocument.Parse(jsonLine);
        if (document.RootElement.TryGetProperty("playlist_count", out var countProperty) && countProperty.TryGetInt32(out var count))
        {
            return count;
        }

        throw new InvalidOperationException("Playlist count was not present in yt-dlp output.");
    }

    public async Task<SyncSummary> SyncRecentAsync(
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<int>? watchLaterTotalProgress = null,
        IProgress<IReadOnlyList<string>>? watchLaterOrderProgress = null,
        IProgress<IReadOnlyList<string>>? syncTargetProgress = null)
    {
        settings.Normalize();
        TrayLog.Write(_paths, $"SyncRecentAsync started. Requested count: {settings.DownloadCount}");
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress, cancellationToken);
        var accountScope = _accountScopeResolver.Resolve(settings);
        var clampedCount = Math.Clamp(settings.DownloadCount, 1, 5000);
        var watchLaterUrl = BuildWatchLaterUrl(settings);
        progress?.Report("Inspecting the current Watch Later items...");
        int? watchLaterTotalCount = null;
        try
        {
            watchLaterTotalCount = await GetWatchLaterTotalAsync(settings, auth, cancellationToken);
            if (watchLaterTotalCount.HasValue)
            {
                watchLaterTotalProgress?.Report(watchLaterTotalCount.Value);
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Could not refresh Watch Later total during sync: {ex.Message}");
        }

        var targetIds = await GetTargetWatchLaterIdsAsync(settings, clampedCount, cancellationToken, auth);
        syncTargetProgress?.Report(targetIds);
        if (watchLaterTotalCount.HasValue
            && targetIds.Count > 0
            && targetIds.Count >= watchLaterTotalCount.Value)
        {
            watchLaterOrderProgress?.Report([.. targetIds.AsEnumerable().Reverse()]);
        }
        TrayLog.Write(_paths, $"Target id count: {targetIds.Count}");
        var existingItemsBefore = _libraryCatalogStore.LoadOrScan(accountScope.FolderName, accountScope.DownloadsPath);
        var existingIdsBefore = existingItemsBefore
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
        _knownLibraryScopeStore.UpdateScopeInventory(accountScope, existingIdsBefore.Count);
        TrayLog.Write(_paths, $"Existing on-disk video count before sync: {existingIdsBefore.Count}");
        var missingTargetIds = targetIds
            .Where(id => !existingIdsBefore.Contains(id))
            .ToList();
        var repairedArchiveCount = RepairArchiveEntriesForMissingVideos(accountScope.ArchivePath, missingTargetIds);
        TrayLog.Write(_paths, $"Missing target count before sync: {missingTargetIds.Count}. Archive entries repaired: {repairedArchiveCount}");
        progress?.Report($"{missingTargetIds.Count} of {targetIds.Count} Watch Later videos are not on disk. Starting yt-dlp...");

        var args = new StringBuilder();
        args.Append(" --extractor-args \"youtube:lang=en\"");
        args.Append(" --progress --newline");
        args.Append(" --ignore-errors --no-abort-on-error --retries 10 --fragment-retries 10 --file-access-retries 10");
        args.Append(" --retry-sleep http:exp=1:20 --retry-sleep fragment:exp=1:20");
        args.Append(" --sleep-requests 1 --sleep-interval 2 --max-sleep-interval 8 --concurrent-fragments 4");
        args.Append($" --playlist-items 1:{clampedCount}");
        args.Append($" --download-archive \"{accountScope.ArchivePath}\"");
        args.Append($" --paths home:\"{accountScope.DownloadsPath}\" --paths temp:\"{accountScope.DownloadsPath}\"");
        args.Append(" --output \"%(playlist_index)03d - %(title).200B [%(id)s].%(ext)s\"");
        args.Append(" --windows-filenames --continue --part --no-overwrites --no-mtime --write-info-json --write-thumbnail");
        args.Append(" --write-subs --write-auto-subs --sub-langs \"en.*,en,-live_chat\" --convert-subs srt");
        if (!string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
        {
            args.Append(" --embed-subs");
        }
        args.Append(BuildHighestQualityFormatArguments());
        args.Append($" \"{watchLaterUrl}\"");

        var result = await RunYtDlpWithRetryAsync(settings, auth, args.ToString(), cancellationToken, progress);
        TrayLog.Write(_paths, $"yt-dlp sync exit code: {result.ExitCode}");
        string? nonFatalIssue = null;
        if (result.ExitCode != 0)
        {
            var effectiveAuth = _refreshedAuth ?? auth;
            if (LooksLikeAuthFailure(result))
            {
                throw new InvalidOperationException(BuildYtDlpFailureMessage(
                    "yt-dlp sync failed.",
                    effectiveAuth,
                    result));
            }

            nonFatalIssue = SummarizeNonFatalSyncIssue(result);
            if (!string.IsNullOrWhiteSpace(nonFatalIssue))
            {
                TrayLog.Write(_paths, $"yt-dlp reported non-fatal item errors: {nonFatalIssue}");
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
            NonFatalIssue: nonFatalIssue);
    }

    public async Task<RedownloadSummary> RedownloadVideosAsync(
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

        try
        {
            progress?.Report($"Preparing {normalizedIds.Count} video(s) for redownload...");
            backups = BackupExistingVideoBundles(
                normalizedIds
                    .Where(existingItemsById.ContainsKey)
                    .Select(id => existingItemsById[id]),
                backupRoot);
            RemoveArchiveEntries(accountScope.ArchivePath, normalizedIds);

            var args = new StringBuilder();
            args.Append(" --extractor-args \"youtube:lang=en\"");
            args.Append(" --progress --newline");
            args.Append(" --ignore-errors --no-abort-on-error --retries 10 --fragment-retries 10 --file-access-retries 10");
            args.Append(" --retry-sleep http:exp=1:20 --retry-sleep fragment:exp=1:20");
            args.Append(" --sleep-requests 1 --sleep-interval 2 --max-sleep-interval 8 --concurrent-fragments 4");
            args.Append($" --download-archive \"{accountScope.ArchivePath}\"");
            args.Append($" --paths home:\"{accountScope.DownloadsPath}\" --paths temp:\"{accountScope.DownloadsPath}\"");
            args.Append(" --output \"%(title).200B [%(id)s].%(ext)s\"");
            args.Append(" --windows-filenames --continue --part --no-overwrites --no-mtime --write-info-json --write-thumbnail");
            args.Append(" --write-subs --write-auto-subs --sub-langs \"en.*,en,-live_chat\" --convert-subs srt");
            if (!string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
            {
                args.Append(" --embed-subs");
            }

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

                nonFatalIssue = SummarizeNonFatalSyncIssue(result);
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
            return new RedownloadSummary(
                RequestedCount: normalizedIds.Count,
                RedownloadedCount: downloadedIds.Count,
                FailedCount: failedIds.Count,
                NonFatalIssue: nonFatalIssue);
        }
        catch
        {
            RestoreMissingBackups(accountScope, backups);
            throw;
        }
        finally
        {
            TryDeleteDirectory(backupRoot);
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

        settings.BrowserCookies = selectedBrowser.Value;
        settings.Normalize();
        _freshCookieRetryAttempted = true;
        progress?.Report($"Authentication failed; attempting fresh {DescribeBrowser(selectedBrowser.Value)} cookie export...");
        TrayLog.Write(_paths, $"Attempting fresh cookie export for {selectedBrowser.Value}:{settings.BrowserProfile}");

        try
        {
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
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdOut.AppendLine(e.Data);
            progress?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdErr.AppendLine(e.Data);
            progress?.Report(e.Data);
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

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
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

    private string BuildYtDlpFailureMessage(string prefix, AuthConfig auth, ProcessResult result)
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
        }

        if (isAuthFailure && !string.IsNullOrWhiteSpace(_lastFreshCookieRetryStatus))
        {
            details.Add(_lastFreshCookieRetryStatus);
        }

        details.Add(result.GetTail());
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

    private static string? SummarizeNonFatalSyncIssue(ProcessResult result)
    {
        var combined = result.CombinedOutput;
        if (combined.Contains("drm protected", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
        {
            return "Some videos were skipped because YouTube only exposed DRM-protected or unavailable formats.";
        }

        if (combined.Contains("Did not get any data blocks", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("fragment not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Some videos were skipped because YouTube did not return playable media for every item.";
        }

        return "yt-dlp reported item-level errors, but the successfully downloaded videos were kept.";
    }

    private static bool LooksLikeAuthFailure(ProcessResult result) =>
        LooksLikeAuthFailureOutput(result.CombinedOutput);

    internal static bool LooksLikeAuthFailureOutput(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return combined.Contains("playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("This playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Please sign in", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("This video may be inappropriate", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("requested content is not available", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("cookies", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("cookie database", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("failed to decrypt", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dpapi", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("could not copy chrome cookie database", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("could not copy edge cookie database", StringComparison.OrdinalIgnoreCase);
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

    private async Task<List<string>> GetTargetWatchLaterIdsAsync(
        AppSettings settings,
        int downloadCount,
        CancellationToken cancellationToken,
        AuthConfig auth)
    {
        return await GetWatchLaterIdsAsync(settings, cancellationToken, auth, $"1:{downloadCount}");
    }

    private async Task<List<string>> GetWatchLaterIdsAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        AuthConfig auth,
        string? playlistItemsRange,
        bool newestFirst = false)
    {
        var watchLaterUrl = BuildWatchLaterUrl(settings);
        var playlistItemsArg = string.IsNullOrWhiteSpace(playlistItemsRange)
            ? string.Empty
            : $" --playlist-items {playlistItemsRange}";
        var result = await RunYtDlpWithRetryAsync(
            new AppSettings
            {
                DownloadCount = settings.DownloadCount,
                BrowserCookies = auth.Browser,
                BrowserProfile = auth.Profile,
                SelectedBrowserAccountKey = settings.SelectedBrowserAccountKey,
                SelectedYouTubeAccountKey = settings.SelectedYouTubeAccountKey
            },
            auth,
            $"--flat-playlist{playlistItemsArg} --print \"%(id)s\" \"{watchLaterUrl}\"",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var effectiveAuth = _refreshedAuth ?? auth;
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "Could not inspect the current Watch Later items.",
                effectiveAuth,
                result));
        }

        var ids = result.StdOut
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length >= 6 && !line.StartsWith("[", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (newestFirst)
        {
            ids.Reverse();
        }

        TrayLog.Write(_paths, $"GetWatchLaterIdsAsync returned {ids.Count} ids.");
        return ids;
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

    private static List<RedownloadBackup> BackupExistingVideoBundles(
        IEnumerable<VideoItem> items,
        string backupRoot)
    {
        var backups = new List<RedownloadBackup>();
        foreach (var item in items)
        {
            var sourcePaths = GetBundleFilePaths(item);
            if (sourcePaths.Count == 0)
            {
                continue;
            }

            var itemBackupRoot = Path.Combine(backupRoot, item.VideoId);
            Directory.CreateDirectory(itemBackupRoot);
            var backedUpFiles = new List<RedownloadBackupFile>();
            foreach (var sourcePath in sourcePaths)
            {
                var destinationPath = Path.Combine(itemBackupRoot, Path.GetFileName(sourcePath));
                var suffix = 1;
                while (File.Exists(destinationPath))
                {
                    destinationPath = Path.Combine(itemBackupRoot, $"{suffix++}_{Path.GetFileName(sourcePath)}");
                }

                File.Move(sourcePath, destinationPath);
                backedUpFiles.Add(new RedownloadBackupFile(sourcePath, destinationPath));
            }

            backups.Add(new RedownloadBackup(item.VideoId, backedUpFiles));
        }

        return backups;
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

    private static void RestoreBackups(IEnumerable<RedownloadBackup> backups)
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
        string? NonFatalIssue);

    internal readonly record struct RedownloadSummary(
        int RequestedCount,
        int RedownloadedCount,
        int FailedCount,
        string? NonFatalIssue);

    private readonly record struct RedownloadBackupFile(string SourcePath, string BackupPath);

    private readonly record struct RedownloadBackup(string VideoId, IReadOnlyList<RedownloadBackupFile> Files);
}
