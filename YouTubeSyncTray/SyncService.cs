using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class SyncService
{
    private readonly YoutubeSyncPaths _paths;
    private readonly ChromiumCookieExporter _chromiumCookieExporter;
    private readonly IBrowserLoginPrompt? _browserLoginPrompt;
    private bool _freshCookieRetryAttempted;
    private AuthConfig? _refreshedAuth;
    private string? _lastFreshCookieRetryStatus;

    public SyncService(YoutubeSyncPaths paths, IBrowserLoginPrompt? browserLoginPrompt = null)
    {
        _paths = paths;
        _chromiumCookieExporter = new ChromiumCookieExporter(paths);
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
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress: null, cancellationToken);

        var result = await RunYtDlpWithRetryAsync(
            settings,
            auth,
            "--flat-playlist --dump-single-json \"https://www.youtube.com/playlist?list=WL\"",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "Could not fetch Watch Later total.",
                auth,
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

    public async Task<SyncSummary> SyncRecentAsync(AppSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        TrayLog.Write(_paths, $"SyncRecentAsync started. Requested count: {settings.DownloadCount}");
        var auth = await EnsureAuthAsync(settings, cancellationToken);
        auth = await PrepareManagedChromiumAuthAsync(settings, auth, progress, cancellationToken);
        var clampedCount = Math.Clamp(settings.DownloadCount, 1, 5000);
        progress?.Report("Inspecting the current Watch Later items...");
        var targetIds = await GetTargetWatchLaterIdsAsync(clampedCount, cancellationToken, auth);
        TrayLog.Write(_paths, $"Target id count: {targetIds.Count}");
        var existingIdsBefore = VideoItem.LoadFromDownloads(_paths.DownloadsPath)
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
        TrayLog.Write(_paths, $"Existing on-disk video count before sync: {existingIdsBefore.Count}");
        var missingTargetIds = targetIds
            .Where(id => !existingIdsBefore.Contains(id))
            .ToList();
        var repairedArchiveCount = RepairArchiveEntriesForMissingVideos(missingTargetIds);
        TrayLog.Write(_paths, $"Missing target count before sync: {missingTargetIds.Count}. Archive entries repaired: {repairedArchiveCount}");
        progress?.Report($"{missingTargetIds.Count} of {targetIds.Count} Watch Later videos are not on disk. Starting yt-dlp...");

        var args = new StringBuilder();
        args.Append(" --extractor-args \"youtube:lang=en\"");
        args.Append(" --progress --newline");
        args.Append(" --ignore-errors --no-abort-on-error --retries 10 --fragment-retries 10 --file-access-retries 10");
        args.Append(" --retry-sleep http:exp=1:20 --retry-sleep fragment:exp=1:20");
        args.Append(" --sleep-requests 1 --sleep-interval 2 --max-sleep-interval 8 --concurrent-fragments 4");
        args.Append($" --playlist-items 1:{clampedCount}");
        args.Append($" --download-archive \"{_paths.ArchivePath}\"");
        args.Append($" --paths home:\"{_paths.DownloadsPath}\" --paths temp:\"{_paths.DownloadsPath}\"");
        args.Append(" --output \"%(playlist_index)03d - %(title).200B [%(id)s].%(ext)s\"");
        args.Append(" --windows-filenames --continue --part --no-overwrites --no-mtime --write-info-json --write-thumbnail");
        args.Append(" --write-subs --write-auto-subs --sub-langs \"en.*,en,-live_chat\" --convert-subs srt");
        if (!string.IsNullOrWhiteSpace(_paths.FfmpegRootPath))
        {
            args.Append(" --embed-subs");
        }
        args.Append(" --format-sort \"res:1080,+size,+br,+fps\"");
        args.Append(" --format \"b[language^=en][ext=mp4][height<=1080]/b[language^=en][height<=1080]/95/96/18/b[ext=mp4][height<=1080]/b[height<=1080]/b[ext=mp4]/b\"");
        args.Append(" \"https://www.youtube.com/playlist?list=WL\"");

        var result = await RunYtDlpWithRetryAsync(settings, auth, args.ToString(), cancellationToken, progress);
        TrayLog.Write(_paths, $"yt-dlp sync exit code: {result.ExitCode}");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "yt-dlp sync failed.",
                auth,
                result));
        }

        var existingIdsAfter = VideoItem.LoadFromDownloads(_paths.DownloadsPath)
            .Select(item => item.VideoId)
            .ToHashSet(StringComparer.Ordinal);
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
            MissingAfterSyncCount: stillMissingCount);
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

        if (HasSavedCookieFile())
        {
            _refreshedAuth = AuthConfig.ForCookieFile(_paths.CookiesPath);
            progress?.Report($"Using the saved {DescribeBrowser(auth.Browser)} cookie export.");
            TrayLog.Write(_paths, $"Using saved cookies file {_paths.CookiesPath} for {auth.Browser}:{auth.Profile}.");
            return auth;
        }

        progress?.Report($"No saved {DescribeBrowser(auth.Browser)} cookies were found. Opening the managed browser sign-in flow...");
        var refreshedAuth = await TryRefreshCookiesAsync(settings, auth, cancellationToken, progress);
        if (refreshedAuth.HasValue)
        {
            return auth;
        }

        throw new InvalidOperationException(
            _lastFreshCookieRetryStatus ??
            $"Managed {DescribeBrowser(auth.Browser)} sign-in did not complete, so YouTube access could not be prepared.");
    }

    private string BuildCommonArgs(AuthConfig auth)
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

        return builder.ToString();
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
        auth = _refreshedAuth ?? auth;
        var result = await RunProcessAsync(
            _paths.YtDlpPath,
            $"{BuildCommonArgs(auth)} {arguments}",
            _paths.RootPath,
            cancellationToken,
            progress);

        if (!LooksLikeAuthFailure(result))
        {
            return result;
        }

        var refreshedAuth = await TryRefreshCookiesAsync(settings, auth, cancellationToken, progress);
        if (refreshedAuth is null)
        {
            return result;
        }

        progress?.Report("Authentication failed; retrying with freshly exported cookies...");
        return await RunProcessAsync(
            _paths.YtDlpPath,
            $"{BuildCommonArgs(refreshedAuth.Value)} {arguments}",
            _paths.RootPath,
            cancellationToken,
            progress);
    }

    private async Task<AuthConfig?> TryRefreshCookiesAsync(
        AppSettings settings,
        AuthConfig auth,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_freshCookieRetryAttempted)
        {
            return null;
        }

        if (auth.Mode != AuthMode.Browser)
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
            _refreshedAuth = AuthConfig.ForCookieFile(exportResult.CookiesPath);
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
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
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

        if (LooksLikeAuthFailure(result))
        {
            details.Add(
                "YouTube rejected the Watch Later request. This usually means the selected browser profile is not signed into YouTube or the browser cookies could not be read by yt-dlp.");
        }

        if (auth.Mode == AuthMode.Browser && auth.Browser is BrowserCookieSource.Chrome or BrowserCookieSource.Edge)
        {
            details.Add(
                "Chrome and Edge on Windows no longer expose the live default profile reliably. When needed, the app opens an app-managed browser profile and exports fresh cookies from that session.");
        }

        if (!string.IsNullOrWhiteSpace(_lastFreshCookieRetryStatus))
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
            AuthMode.CookieFile => $"cookies file at {auth.CookiesPath}",
            _ => $"browser cookies from {DescribeBrowser(auth.Browser)} profile '{auth.Profile}'"
        };
    }

    private static bool LooksLikeAuthFailure(ProcessResult result)
    {
        var combined = result.CombinedOutput;
        return combined.Contains("playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("This playlist does not exist", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
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

    private bool HasSavedCookieFile()
    {
        try
        {
            var info = new FileInfo(_paths.CookiesPath);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<string>> GetTargetWatchLaterIdsAsync(int downloadCount, CancellationToken cancellationToken, AuthConfig auth)
    {
        var result = await RunYtDlpWithRetryAsync(
            new AppSettings
            {
                DownloadCount = downloadCount,
                BrowserCookies = auth.Browser,
                BrowserProfile = auth.Profile
            },
            auth,
            $"--flat-playlist --playlist-items 1:{downloadCount} --print \"%(id)s\" \"https://www.youtube.com/playlist?list=WL\"",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildYtDlpFailureMessage(
                "Could not inspect the current Watch Later items before syncing.",
                auth,
                result));
        }

        var ids = result.StdOut
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length >= 6 && !line.StartsWith("[", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        TrayLog.Write(_paths, $"GetTargetWatchLaterIdsAsync returned {ids.Count} ids.");
        return ids;
    }

    private int RepairArchiveEntriesForMissingVideos(IEnumerable<string> missingVideoIds)
    {
        if (!File.Exists(_paths.ArchivePath))
        {
            return 0;
        }

        var missingSet = missingVideoIds.ToHashSet(StringComparer.Ordinal);
        if (missingSet.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        var keptLines = new List<string>();
        foreach (var line in File.ReadAllLines(_paths.ArchivePath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("youtube ", StringComparison.Ordinal))
            {
                var id = trimmed["youtube ".Length..];
                if (missingSet.Contains(id))
                {
                    removed++;
                    continue;
                }
            }

            keptLines.Add(line);
        }

        if (removed > 0)
        {
            File.WriteAllLines(_paths.ArchivePath, keptLines);
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

        public static AuthConfig ForCookieFile(string cookiesPath) =>
            new(AuthMode.CookieFile, BrowserCookieSource.Chrome, string.Empty, cookiesPath);
    }
    internal readonly record struct SyncSummary(
        int RequestedCount,
        int TargetCount,
        int DownloadedCount,
        int AlreadyPresentCount,
        int ArchiveRepairedCount,
        int MissingAfterSyncCount);
}
