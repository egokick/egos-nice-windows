namespace YouTubeSyncTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly YoutubeSyncPaths _paths;
    private readonly SyncService _syncService;
    private readonly YoutubeRemovalService _removalService;
    private readonly ThumbnailCacheService _thumbnailCacheService;
    private readonly UiThreadDispatcher _uiDispatcher;
    private readonly LibraryBrowserState _libraryState;
    private readonly LibraryWebServer _libraryWebServer;
    private readonly BrowserAccountDiscoveryService _accountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly AccountScopeResolver _accountScopeResolver;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _syncNowMenuItem;
    private readonly object _pendingRemovalGate = new();
    private readonly object _watchLaterTotalRefreshGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private AppSettings _settings;
    private bool _isBusy;
    private bool _isShuttingDown;
    private HashSet<string> _pendingRemovalIds = new(StringComparer.Ordinal);
    private HashSet<string> _watchLaterTotalRefreshSelectionsInFlight = new(StringComparer.Ordinal);

    public TrayApplicationContext()
    {
        _paths = YoutubeSyncPaths.Discover();
        _settings = SettingsStore.Load();
        _syncService = new SyncService(_paths, new WinFormsBrowserLoginPrompt(() => Form.ActiveForm));
        _removalService = new YoutubeRemovalService(_paths);
        _thumbnailCacheService = new ThumbnailCacheService(_paths);
        _uiDispatcher = new UiThreadDispatcher();
        _accountDiscovery = new BrowserAccountDiscoveryService();
        _youTubeAccountDiscovery = new YouTubeAccountDiscoveryService(_paths);
        _accountScopeResolver = new AccountScopeResolver(_paths, _accountDiscovery, _youTubeAccountDiscovery);
        _libraryState = new LibraryBrowserState(GetCurrentVideoCount());
        _libraryWebServer = new LibraryWebServer(
            _paths,
            _thumbnailCacheService,
            _libraryState,
            GetSettingsSnapshot,
            _accountDiscovery,
            _youTubeAccountDiscovery,
            _accountScopeResolver,
            QueueSyncFromBrowserAsync,
            QueueRemoveFromBrowserAsync,
            QueueSelectBrowserAccountFromBrowserAsync,
            QueueSelectYouTubeAccountFromBrowserAsync,
            QueueOpenSettingsFromBrowserAsync);

        _startupMenuItem = new ToolStripMenuItem("Run at Windows startup") { CheckOnClick = true };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        _settingsMenuItem = new ToolStripMenuItem("Settings");
        _settingsMenuItem.Click += async (_, _) => await OpenSettingsAsync();

        _syncNowMenuItem = new ToolStripMenuItem("Sync Now");
        _syncNowMenuItem.Click += async (_, _) => await RunSyncAsync(showSuccessBalloon: true, initiatedManually: true);

        var openLibraryMenuItem = new ToolStripMenuItem("Open Library In Browser");
        openLibraryMenuItem.Click += async (_, _) => await ShowLibraryAsync();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreatePlayIcon(),
            Visible = true,
            Text = "YouTube Sync Tray",
            ContextMenuStrip = new ContextMenuStrip()
        };
        _notifyIcon.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                await ShowLibraryAsync();
            }
        };

        _notifyIcon.ContextMenuStrip.Items.Add(openLibraryMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_syncNowMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(_startupMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(_settingsMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);

        RefreshMenu();
        _ = InitializeAsync();
    }

    protected override void ExitThreadCore()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _shutdownCts.Cancel();
        CloseOpenForms();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _libraryWebServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _uiDispatcher.Dispose();
        _shutdownCts.Dispose();
        base.ExitThreadCore();
    }

    private async Task InitializeAsync()
    {
        try
        {
            TrayLog.Write(_paths, "Tray initialization started.");
            await _libraryWebServer.EnsureStartedAsync(_shutdownCts.Token);
            if (_settings.AutoSyncArmed)
            {
                await RunSyncAsync(showSuccessBalloon: false);
            }
            else
            {
                SetBusy(false, BuildAutomaticSyncPausedStatus());
            }
        }
        catch (Exception ex)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            TrayLog.Write(_paths, $"Tray initialization failed: {ex}");
            _libraryState.SetBusy(false, $"Initialization failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
        }
    }

    private async Task ShowLibraryAsync()
    {
        try
        {
            await _libraryWebServer.EnsureStartedAsync(_shutdownCts.Token);
            _libraryWebServer.OpenInBrowser();
            QueueWatchLaterTotalRefresh();
        }
        catch (Exception ex)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            TrayLog.Write(_paths, $"ShowLibraryAsync failed: {ex}");
            ShowErrorBalloon($"Could not open the browser library: {ex.Message}");
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupService.SetRunAtStartup(_startupMenuItem.Checked);
            RefreshMenu();
        }
        catch (Exception ex)
        {
            ShowErrorBalloon($"Startup update failed: {ex.Message}");
            RefreshMenu();
        }
    }

    private async Task OpenSettingsAsync()
    {
        using var form = new SettingsForm
        {
            DownloadCount = _settings.DownloadCount,
            BrowserCookies = _settings.BrowserCookies,
            BrowserProfile = _settings.BrowserProfile
        };
        form.RefreshTotalRequested += async (_, _) =>
        {
            if (_isBusy)
            {
                form.SetBusy(false, "The app is busy. You can still change settings now; refresh the Watch Later total after the current operation finishes.");
                return;
            }

            await PopulateSettingsSummaryAsync(form);
        };

        if (_isBusy)
        {
            form.SetBusy(false, "The app is busy. You can still change settings now; Watch Later total refresh resumes when the current operation finishes.");
        }
        else if (!_settings.AutoSyncArmed)
        {
            form.SetBusy(false, "Automatic sync is paused. Click Sync Now to check again, or Refresh Total to inspect Watch Later without starting a sync.");
        }
        else
        {
            await PopulateSettingsSummaryAsync(form);
        }

        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var shouldClearWatchLaterTotalCount =
            _settings.BrowserCookies != form.BrowserCookies
            || !string.Equals(_settings.BrowserProfile, form.BrowserProfile, StringComparison.Ordinal);
        _settings.DownloadCount = form.DownloadCount;
        if (shouldClearWatchLaterTotalCount)
        {
            _settings.SelectedAccountKey = null;
            _settings.SelectedBrowserAccountKey = null;
            _settings.SelectedYouTubeAccountKey = null;
        }
        _settings.BrowserCookies = form.BrowserCookies;
        _settings.BrowserProfile = form.BrowserProfile;
        SettingsStore.Save(_settings);
        RefreshLibraryForCurrentSelection(clearWatchLaterTotalCount: shouldClearWatchLaterTotalCount);
        ShowInfoBalloon(
            _isBusy
                ? $"Settings saved. The current operation will finish first; the next sync will use up to {_settings.DownloadCount} videos on {_settings.BrowserCookies}:{_settings.BrowserProfile}."
                : _settings.AutoSyncArmed
                    ? $"Sync scope is set to up to the most recent {_settings.DownloadCount} videos using {_settings.BrowserCookies}:{_settings.BrowserProfile}."
                    : $"Sync scope is set to up to the most recent {_settings.DownloadCount} videos using {_settings.BrowserCookies}:{_settings.BrowserProfile}. Automatic sync stays paused until you click Sync Now.");
        RefreshMenu();
    }

    private async Task PopulateSettingsSummaryAsync(SettingsForm form)
    {
        try
        {
            form.SetBusy(true, "Refreshing Watch Later total...");
            var settings = new AppSettings
            {
                DownloadCount = form.DownloadCount,
                BrowserCookies = form.BrowserCookies,
                BrowserProfile = form.BrowserProfile,
                SelectedBrowserAccountKey =
                    form.BrowserCookies == _settings.BrowserCookies
                    && string.Equals(form.BrowserProfile, _settings.BrowserProfile, StringComparison.OrdinalIgnoreCase)
                        ? _settings.SelectedBrowserAccountKey
                        : null,
                SelectedYouTubeAccountKey =
                    form.BrowserCookies == _settings.BrowserCookies
                    && string.Equals(form.BrowserProfile, _settings.BrowserProfile, StringComparison.OrdinalIgnoreCase)
                        ? _settings.SelectedYouTubeAccountKey
                        : null
            };
            settings.Normalize();
            var total = await _syncService.GetWatchLaterTotalAsync(settings, _shutdownCts.Token);
            if (form.IsDisposed || _shutdownCts.IsCancellationRequested)
            {
                return;
            }

            form.BrowserCookies = settings.BrowserCookies;
            form.SetBusy(false, string.Empty);
            if (MatchesLibrarySelection(settings, _settings))
            {
                SetWatchLaterTotalCount(total);
            }

            form.SetPlaylistSummary(form.DownloadCount, total);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!form.IsDisposed)
            {
                form.SetBusy(false, $"Could not refresh Watch Later total: {ex.Message}");
            }
        }
    }

    private async Task RunSyncAsync(bool showSuccessBalloon, bool initiatedManually = false)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (initiatedManually)
            {
                ArmAutomaticSync();
            }

            TrayLog.Write(_paths, $"RunSyncAsync started. showSuccessBalloon={showSuccessBalloon}");
            SetBusy(true, $"Syncing up to the most recent {_settings.DownloadCount} videos...");
            var syncSettings = _settings.CreateSnapshot();
            var startingSettings = syncSettings.CreateSnapshot();
            if (showSuccessBalloon)
            {
                ShowInfoBalloon($"Sync started for up to the most recent {_settings.DownloadCount} Watch Later videos.");
            }
            var progress = new Progress<string>(message => _libraryState.ReportProgress(TruncateProgress(message)));
            var watchLaterTotalProgress = new Progress<int>(total =>
            {
                if (MatchesLibrarySelection(syncSettings, _settings))
                {
                    SetWatchLaterTotalCount(total);
                }
            });
            var summary = await _syncService.SyncRecentAsync(
                syncSettings,
                progress,
                _shutdownCts.Token,
                watchLaterTotalProgress);
            var selectionChanged = TryPersistPromptSelectedBrowser(startingSettings, syncSettings);
            var automaticSyncPaused = PauseAutomaticSyncIfSatisfied(summary);
            RefreshLibraryForCurrentSelection(clearWatchLaterTotalCount: selectionChanged, queueWatchLaterTotalRefresh: !automaticSyncPaused);
            if (summary.WatchLaterTotalCount.HasValue && MatchesLibrarySelection(syncSettings, _settings))
            {
                SetWatchLaterTotalCount(summary.WatchLaterTotalCount.Value);
            }

            var status = BuildSyncStatus(summary, automaticSyncPaused);
            TrayLog.Write(_paths, $"RunSyncAsync completed. Status: {status}");
            SetBusy(false, status);
            if (showSuccessBalloon)
            {
                ShowInfoBalloon(status);
            }

            StartQueuedRemovalIfIdle();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            TrayLog.Write(_paths, "RunSyncAsync cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"RunSyncAsync failed: {ex}");
            SetBusy(false, $"Sync failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
            StartQueuedRemovalIfIdle();
        }
    }

    private async Task RemoveSelectedFromWatchLaterAsync(IReadOnlyList<string> selectedIds)
    {
        if (selectedIds.Count == 0)
        {
            return;
        }

        try
        {
            var removalSettings = _settings.CreateSnapshot();
            SetBusy(true, "Removing selected videos from Watch Later...");
            var progress = new Progress<string>(message => _libraryState.ReportProgress(TruncateProgress(message)));
            await _removalService.RemoveFromWatchLaterAsync(removalSettings, selectedIds, progress, _shutdownCts.Token);
            RefreshLibraryForCurrentSelection(clearWatchLaterTotalCount: true);
            SetBusy(false, $"{GetCurrentVideoCount()} downloaded videos");
            QueueWatchLaterTotalRefresh();
            ShowInfoBalloon($"Requested removal for {selectedIds.Count} video(s).");
            StartQueuedRemovalIfIdle();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            TrayLog.Write(_paths, "RemoveSelectedFromWatchLaterAsync cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Removal failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
            StartQueuedRemovalIfIdle();
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        RefreshMenu();
        _libraryState.SetBusy(isBusy, status);
    }

    private void ArmAutomaticSync()
    {
        if (_settings.AutoSyncArmed)
        {
            return;
        }

        _settings.AutoSyncArmed = true;
        SettingsStore.Save(_settings);
    }

    private bool PauseAutomaticSyncIfSatisfied(SyncService.SyncSummary summary)
    {
        if (!ShouldPauseAutomaticSync(summary))
        {
            return false;
        }

        if (_settings.AutoSyncArmed)
        {
            _settings.AutoSyncArmed = false;
            SettingsStore.Save(_settings);
        }

        return true;
    }

    private void RefreshMenu()
    {
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        _settingsMenuItem.Enabled = true;
        _syncNowMenuItem.Enabled = !_isBusy;
        _notifyIcon.Text = _isBusy
            ? "YouTube Sync Tray: busy"
            : $"YouTube Sync Tray: up to {_settings.DownloadCount} recent videos";
    }

    private void ShowInfoBalloon(string message)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(1500, "YouTube Sync Tray", message, ToolTipIcon.Info);
    }

    private void ShowErrorBalloon(string message)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(3000, "YouTube Sync Tray", Truncate(message), ToolTipIcon.Error);
    }

    private static string Truncate(string value)
    {
        return value.Length <= 220 ? value : value[..220];
    }

    private static string TruncateProgress(string value)
    {
        return value.Length <= 320 ? value : value[..320];
    }

    internal static bool ShouldPauseAutomaticSync(SyncService.SyncSummary summary) =>
        summary.MissingAfterSyncCount == 0;

    internal static string BuildSyncStatus(SyncService.SyncSummary summary, bool automaticSyncPaused = false)
    {
        string status;
        if (summary.TargetCount == 0)
        {
            status = "No Watch Later videos were found for the current sync range.";
            return automaticSyncPaused ? AppendAutomaticSyncPauseNotice(status) : status;
        }

        if (summary.DownloadedCount == 0 && summary.MissingAfterSyncCount == 0)
        {
            status = summary.AlreadyPresentCount == summary.TargetCount
                ? $"No new videos to download. The first {summary.TargetCount} Watch Later videos are already on disk."
                : $"Sync checked {summary.TargetCount} videos. Everything already available is on disk.";
            return automaticSyncPaused ? AppendAutomaticSyncPauseNotice(status) : status;
        }

        var parts = new List<string> { $"Downloaded {summary.DownloadedCount} video(s)" };
        if (summary.ArchiveRepairedCount > 0)
        {
            parts.Add($"repaired {summary.ArchiveRepairedCount} stale archive entr{(summary.ArchiveRepairedCount == 1 ? "y" : "ies")}");
        }

        if (summary.AlreadyPresentCount > 0)
        {
            parts.Add($"{summary.AlreadyPresentCount} already present");
        }

        if (summary.MissingAfterSyncCount > 0)
        {
            parts.Add($"{summary.MissingAfterSyncCount} still missing");
        }

        if (!string.IsNullOrWhiteSpace(summary.NonFatalIssue))
        {
            parts.Add(summary.NonFatalIssue.TrimEnd('.'));
        }

        status = string.Join("; ", parts) + ".";
        return automaticSyncPaused ? AppendAutomaticSyncPauseNotice(status) : status;
    }

    private static string AppendAutomaticSyncPauseNotice(string status)
    {
        var normalized = status.Trim().TrimEnd('.');
        return $"{normalized}. Automatic sync is paused until you click Sync Now again.";
    }

    private string BuildAutomaticSyncPausedStatus()
    {
        var currentVideoCount = GetCurrentVideoCount();
        var noun = currentVideoCount == 1 ? "video" : "videos";
        return $"{currentVideoCount} downloaded {noun}. Automatic sync is paused until you click Sync Now.";
    }

    private AppSettings GetSettingsSnapshot()
    {
        return new AppSettings
        {
            DownloadCount = _settings.DownloadCount,
            BrowserCookies = _settings.BrowserCookies,
            BrowserProfile = _settings.BrowserProfile,
            SelectedAccountKey = _settings.SelectedAccountKey,
            SelectedBrowserAccountKey = _settings.SelectedBrowserAccountKey,
            SelectedYouTubeAccountKey = _settings.SelectedYouTubeAccountKey
        };
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueSyncFromBrowserAsync()
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (_isBusy)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "The tray app is already busy.");
            }

            _ = RunSyncAsync(showSuccessBalloon: true, initiatedManually: true);
            return new LibraryWebServer.LibraryCommandResponse(
                true,
                $"Sync started for up to the most recent {_settings.DownloadCount} Watch Later videos.");
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueRemoveFromBrowserAsync(IReadOnlyList<string> videoIds)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (videoIds.Count == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Select one or more videos first.");
            }

            if (_isBusy)
            {
                var queuedCount = QueuePendingRemoval(videoIds);
                return new LibraryWebServer.LibraryCommandResponse(
                    true,
                    $"The tray app is busy. Queued removal for {videoIds.Count} video(s). {queuedCount} total queued; removal will start after the current task finishes.");
            }

            _ = RemoveSelectedFromWatchLaterAsync(videoIds);
            return new LibraryWebServer.LibraryCommandResponse(
                true,
                $"Queued removal for {videoIds.Count} selected video(s).");
        });
    }

    private int QueuePendingRemoval(IReadOnlyList<string> videoIds)
    {
        lock (_pendingRemovalGate)
        {
            foreach (var videoId in videoIds)
            {
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    _pendingRemovalIds.Add(videoId);
                }
            }

            return _pendingRemovalIds.Count;
        }
    }

    private void StartQueuedRemovalIfIdle()
    {
        string[] queuedIds;
        lock (_pendingRemovalGate)
        {
            if (_isBusy || _pendingRemovalIds.Count == 0)
            {
                return;
            }

            queuedIds = _pendingRemovalIds.ToArray();
            _pendingRemovalIds.Clear();
        }

        TrayLog.Write(_paths, $"Starting queued removal for {queuedIds.Length} video(s).");
        _ = RemoveSelectedFromWatchLaterAsync(queuedIds);
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueOpenSettingsFromBrowserAsync()
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            _ = OpenSettingsAsync();
            return new LibraryWebServer.LibraryCommandResponse(true, "Opened the native settings dialog.");
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueSelectBrowserAccountFromBrowserAsync(string accountKey)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            var accounts = _accountDiscovery.DiscoverAccounts(_settings);
            var selectedAccount = accounts.FirstOrDefault(option =>
                string.Equals(option.AccountKey, accountKey, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(selectedAccount.AccountKey))
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "That browser account is no longer available.");
            }

            _settings.BrowserCookies = selectedAccount.Browser;
            _settings.BrowserProfile = selectedAccount.Profile;
            _settings.SelectedAccountKey = null;
            _settings.SelectedBrowserAccountKey = selectedAccount.AccountKey;
            NormalizeSelectedYouTubeAccountForCurrentBrowser();
            SettingsStore.Save(_settings);
            RefreshLibraryForCurrentSelection(clearWatchLaterTotalCount: true);

            var message = _isBusy
                ? $"Saved {selectedAccount.DisplayName} on {selectedAccount.BrowserName} ({selectedAccount.Profile}). The current sync will finish first, and the next sync will use this account."
                : $"Now using {selectedAccount.DisplayName} on {selectedAccount.BrowserName} ({selectedAccount.Profile}).";

            return new LibraryWebServer.LibraryCommandResponse(
                true,
                message);
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueSelectYouTubeAccountFromBrowserAsync(string accountKey)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(_settings);
            var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(_settings, selectedBrowserAccount?.AuthUserIndex);
            var selectedYouTubeAccount = youTubeAccounts.FirstOrDefault(option =>
                string.Equals(option.AccountKey, accountKey, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(selectedYouTubeAccount.AccountKey))
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "That YouTube account is no longer available.");
            }

            _settings.SelectedAccountKey = null;
            _settings.SelectedYouTubeAccountKey = selectedYouTubeAccount.AccountKey;
            SettingsStore.Save(_settings);
            RefreshLibraryForCurrentSelection(clearWatchLaterTotalCount: true);

            var message = _isBusy
                ? $"Saved YouTube account {selectedYouTubeAccount.Label}. The current sync will finish first, and the next sync will use it."
                : $"Now using YouTube account {selectedYouTubeAccount.Label}.";

            return new LibraryWebServer.LibraryCommandResponse(true, message);
        });
    }

    private void NormalizeSelectedYouTubeAccountForCurrentBrowser()
    {
        var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(_settings);
        var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(_settings, selectedBrowserAccount?.AuthUserIndex);
        if (youTubeAccounts.Count == 0)
        {
            _settings.SelectedYouTubeAccountKey = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedYouTubeAccountKey)
            && youTubeAccounts.Any(option => string.Equals(option.AccountKey, _settings.SelectedYouTubeAccountKey, StringComparison.Ordinal)))
        {
            return;
        }

        var selectedYouTubeAccount = youTubeAccounts.FirstOrDefault(option => option.IsSelected);
        _settings.SelectedYouTubeAccountKey = string.IsNullOrWhiteSpace(selectedYouTubeAccount.AccountKey)
            ? youTubeAccounts[0].AccountKey
            : selectedYouTubeAccount.AccountKey;
    }

    private int GetCurrentVideoCount()
    {
        var accountScope = _accountScopeResolver.Resolve(_settings);
        return VideoItem.LoadFromDownloads(accountScope.DownloadsPath).Count;
    }

    private void RefreshLibraryForCurrentSelection(bool clearWatchLaterTotalCount = false, bool queueWatchLaterTotalRefresh = true)
    {
        if (clearWatchLaterTotalCount)
        {
            ClearWatchLaterTotalCount();
        }

        _libraryState.MarkLibraryChanged(GetCurrentVideoCount());
        if (queueWatchLaterTotalRefresh)
        {
            QueueWatchLaterTotalRefresh();
        }
    }

    private bool TryPersistPromptSelectedBrowser(AppSettings startingSettings, AppSettings syncSettings)
    {
        if (_settings.BrowserCookies != startingSettings.BrowserCookies
            || !string.Equals(_settings.BrowserProfile, startingSettings.BrowserProfile, StringComparison.Ordinal)
            || !string.Equals(_settings.SelectedBrowserAccountKey, startingSettings.SelectedBrowserAccountKey, StringComparison.Ordinal)
            || !string.Equals(_settings.SelectedYouTubeAccountKey, startingSettings.SelectedYouTubeAccountKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (_settings.BrowserCookies == syncSettings.BrowserCookies
            && string.Equals(_settings.BrowserProfile, syncSettings.BrowserProfile, StringComparison.Ordinal))
        {
            return false;
        }

        _settings.BrowserCookies = syncSettings.BrowserCookies;
        _settings.BrowserProfile = syncSettings.BrowserProfile;
        _settings.SelectedAccountKey = null;
        _settings.SelectedBrowserAccountKey = null;
        _settings.SelectedYouTubeAccountKey = null;
        SettingsStore.Save(_settings);
        return true;
    }

    private static bool MatchesLibrarySelection(AppSettings left, AppSettings right)
    {
        return left.BrowserCookies == right.BrowserCookies
            && string.Equals(left.BrowserProfile, right.BrowserProfile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SelectedBrowserAccountKey, right.SelectedBrowserAccountKey, StringComparison.Ordinal)
            && string.Equals(left.SelectedYouTubeAccountKey, right.SelectedYouTubeAccountKey, StringComparison.Ordinal);
    }

    private void SetWatchLaterTotalCount(int total)
    {
        _libraryState.SetWatchLaterTotalCount(total);
    }

    private void ClearWatchLaterTotalCount()
    {
        _libraryState.SetWatchLaterTotalCount(null);
    }

    private void QueueWatchLaterTotalRefresh()
    {
        if (_shutdownCts.IsCancellationRequested || _isBusy || !_settings.AutoSyncArmed)
        {
            return;
        }

        var settings = _settings.CreateSnapshot();
        var selectionKey = GetLibrarySelectionKey(settings);
        lock (_watchLaterTotalRefreshGate)
        {
            if (!_watchLaterTotalRefreshSelectionsInFlight.Add(selectionKey))
            {
                return;
            }
        }

        _ = RefreshWatchLaterTotalInBackgroundAsync(settings, selectionKey);
    }

    private async Task RefreshWatchLaterTotalInBackgroundAsync(AppSettings settings, string selectionKey)
    {
        try
        {
            var total = await _syncService.GetWatchLaterTotalAsync(settings, _shutdownCts.Token);
            if (MatchesLibrarySelection(settings, _settings))
            {
                SetWatchLaterTotalCount(total);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Background Watch Later total refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_watchLaterTotalRefreshGate)
            {
                _watchLaterTotalRefreshSelectionsInFlight.Remove(selectionKey);
            }
        }
    }

    private static string GetLibrarySelectionKey(AppSettings settings)
    {
        return string.Join(
            "|",
            settings.BrowserCookies,
            settings.BrowserProfile,
            settings.SelectedBrowserAccountKey ?? string.Empty,
            settings.SelectedYouTubeAccountKey ?? string.Empty);
    }

    private static void CloseOpenForms()
    {
        foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
        {
            try
            {
                if (!form.IsDisposed)
                {
                    form.Close();
                }
            }
            catch
            {
                // Best-effort form cleanup during shutdown.
            }
        }
    }
}
