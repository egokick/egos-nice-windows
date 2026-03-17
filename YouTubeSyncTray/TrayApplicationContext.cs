namespace YouTubeSyncTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly YoutubeSyncPaths _paths;
    private readonly SyncService _syncService;
    private readonly YoutubeRemovalService _removalService;
    private readonly ThumbnailCacheService _thumbnailCacheService;
    private readonly LibraryVideoStateStore _videoStateStore;
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
    private readonly object _watchLaterOrderRefreshGate = new();
    private readonly object _youTubeAccountRefreshGate = new();
    private readonly object _trackedTaskGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private AppSettings _settings;
    private bool _isBusy;
    private bool _isShuttingDown;
    private HashSet<string> _pendingRemovalIds = new(StringComparer.Ordinal);
    private HashSet<Task> _trackedTasks = [];
    private HashSet<string> _watchLaterTotalRefreshSelectionsInFlight = new(StringComparer.Ordinal);
    private HashSet<string> _watchLaterOrderRefreshSelectionsInFlight = new(StringComparer.Ordinal);
    private HashSet<string> _youTubeAccountRefreshSelectionsInFlight = new(StringComparer.Ordinal);
    private Task? _shutdownTask;

    public TrayApplicationContext()
    {
        _paths = YoutubeSyncPaths.Discover();
        _settings = SettingsStore.Load();
        _syncService = new SyncService(_paths);
        _removalService = new YoutubeRemovalService(_paths);
        _thumbnailCacheService = new ThumbnailCacheService(_paths);
        _videoStateStore = new LibraryVideoStateStore(_paths);
        _uiDispatcher = new UiThreadDispatcher();
        _accountDiscovery = new BrowserAccountDiscoveryService();
        _youTubeAccountDiscovery = new YouTubeAccountDiscoveryService(_paths);
        _accountScopeResolver = new AccountScopeResolver(_paths, _accountDiscovery, _youTubeAccountDiscovery);
        _libraryState = new LibraryBrowserState(GetCurrentVideoIds());
        _libraryWebServer = new LibraryWebServer(
            _paths,
            _thumbnailCacheService,
            _videoStateStore,
            _libraryState,
            GetSettingsSnapshot,
            IsYouTubeAccountRefreshInFlight,
            _accountDiscovery,
            _youTubeAccountDiscovery,
            _accountScopeResolver,
            QueueSyncFromBrowserAsync,
            QueueRemoveFromBrowserAsync,
            QueueRestoreFromBrowserAsync,
            QueueOpenDownloadsFolderFromBrowserAsync,
            QueueSelectBrowserAccountFromBrowserAsync,
            QueueSelectYouTubeAccountFromBrowserAsync,
            GetSettingsFromBrowserAsync,
            QueueRefreshSettingsSummaryFromBrowserAsync,
            QueueSaveSettingsFromBrowserAsync,
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
        TrackShutdownTask(InitializeAsync());
    }

    protected override void ExitThreadCore()
    {
        _shutdownTask ??= ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        try
        {
            _shutdownCts.Cancel();
            CloseOpenForms();
            _notifyIcon.Visible = false;
            await WaitForTrackedTasksAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Shutdown encountered an error: {ex}");
        }
        finally
        {
            try
            {
                _notifyIcon.Dispose();
            }
            catch
            {
            }

            try
            {
                await _libraryWebServer.DisposeAsync();
            }
            catch (Exception ex)
            {
                TrayLog.Write(_paths, $"Library web server shutdown failed: {ex}");
            }

            try
            {
                _uiDispatcher.Dispose();
            }
            catch
            {
            }

            _shutdownCts.Dispose();
            base.ExitThreadCore();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            TrayLog.Write(_paths, "Tray initialization started.");
            await _libraryWebServer.EnsureStartedAsync(_shutdownCts.Token);
            QueueWatchLaterOrderRefresh();
            if (_settings.AutoSyncArmed)
            {
                await RunSyncAsync(showSuccessBalloon: false);
            }
            else
            {
                SetBusy(false, BuildAutomaticSyncPausedStatus());
                QueueYouTubeAccountRefresh();
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
            QueueWatchLaterOrderRefresh();
            QueueYouTubeAccountRefresh();
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
        RefreshLibraryForCurrentSelection(
            clearWatchLaterTotalCount: shouldClearWatchLaterTotalCount,
            clearWatchLaterOrder: shouldClearWatchLaterTotalCount,
            resetSyncAuthState: shouldClearWatchLaterTotalCount);
        QueueWatchLaterOrderRefresh();
        QueueYouTubeAccountRefresh();
        ShowInfoBalloon(
            BuildSettingsSavedMessage());
        RefreshMenu();
    }

    private async Task PopulateSettingsSummaryAsync(SettingsForm form)
    {
        AppSettings? settings = null;
        try
        {
            form.SetBusy(true, "Refreshing Watch Later total...");
            settings = new AppSettings
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
            var total = await TrackShutdownTask(_syncService.GetWatchLaterTotalAsync(settings, _shutdownCts.Token));
            if (form.IsDisposed || _shutdownCts.IsCancellationRequested)
            {
                return;
            }

            form.BrowserCookies = settings.BrowserCookies;
            form.SetBusy(false, string.Empty);
            if (MatchesLibrarySelection(settings, _settings))
            {
                SetWatchLaterTotalCount(total);
                SetSyncAuthReady();
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

            if (settings is not null && MatchesLibrarySelection(settings, _settings))
            {
                UpdateSyncAuthStateFromException(ex);
            }
        }
    }

    private Task<LibraryWebServer.SettingsResponse> GetSettingsFromBrowserAsync()
    {
        return _uiDispatcher.InvokeAsync(() => Task.FromResult(BuildSettingsResponse()));
    }

    private Task<LibraryWebServer.SettingsSummaryResponse> QueueRefreshSettingsSummaryFromBrowserAsync(
        LibraryWebServer.SettingsRequest request)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            var settings = CreateDraftSettings(request);
            if (_isBusy)
            {
                return new LibraryWebServer.SettingsSummaryResponse(
                    CanRefreshTotal: false,
                    SummaryMessage: "The app is busy. You can still change settings now; refresh the Watch Later total after the current operation finishes.",
                    WatchLaterTotalCount: null,
                    DownloadCount: settings.DownloadCount,
                    BrowserCookies: settings.BrowserCookies.ToString(),
                    BrowserProfile: settings.BrowserProfile);
            }

            try
            {
                var total = await TrackShutdownTask(_syncService.GetWatchLaterTotalAsync(settings, _shutdownCts.Token));
                if (MatchesLibrarySelection(settings, _settings))
                {
                    SetWatchLaterTotalCount(total);
                    SetSyncAuthReady();
                }

                return BuildSettingsSummaryResponse(settings, total);
            }
            catch (Exception ex)
            {
                if (MatchesLibrarySelection(settings, _settings))
                {
                    UpdateSyncAuthStateFromException(ex);
                }

                throw;
            }
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueSaveSettingsFromBrowserAsync(
        LibraryWebServer.SettingsRequest request)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            var settings = CreateDraftSettings(request);
            var shouldClearWatchLaterTotalCount =
                _settings.BrowserCookies != settings.BrowserCookies
                || !string.Equals(_settings.BrowserProfile, settings.BrowserProfile, StringComparison.Ordinal);
            _settings.DownloadCount = settings.DownloadCount;
            if (shouldClearWatchLaterTotalCount)
            {
                _settings.SelectedAccountKey = null;
                _settings.SelectedBrowserAccountKey = null;
                _settings.SelectedYouTubeAccountKey = null;
            }

            _settings.BrowserCookies = settings.BrowserCookies;
            _settings.BrowserProfile = settings.BrowserProfile;
            SettingsStore.Save(_settings);
            RefreshLibraryForCurrentSelection(
                clearWatchLaterTotalCount: shouldClearWatchLaterTotalCount,
                clearWatchLaterOrder: shouldClearWatchLaterTotalCount,
                resetSyncAuthState: shouldClearWatchLaterTotalCount);
            QueueWatchLaterOrderRefresh();
            QueueYouTubeAccountRefresh();
            RefreshMenu();
            return Task.FromResult(new LibraryWebServer.LibraryCommandResponse(true, BuildSettingsSavedMessage()));
        });
    }

    private LibraryWebServer.SettingsResponse BuildSettingsResponse()
    {
        return new LibraryWebServer.SettingsResponse(
            DownloadCount: _settings.DownloadCount,
            BrowserCookies: _settings.BrowserCookies.ToString(),
            BrowserProfile: _settings.BrowserProfile,
            AvailableBrowsers: GetSettingsBrowserOptions()
                .Select(browser => new LibraryWebServer.BrowserOptionDto(
                    browser.ToString(),
                    ChromiumBrowserLocator.GetDisplayName(browser)))
                .ToList(),
            CanRefreshTotal: !_isBusy,
            ShouldAutoRefreshSummary: !_isBusy && _settings.AutoSyncArmed,
            SummaryMessage: BuildSettingsPanelMessage());
    }

    private LibraryWebServer.SettingsSummaryResponse BuildSettingsSummaryResponse(AppSettings settings, int total)
    {
        return new LibraryWebServer.SettingsSummaryResponse(
            CanRefreshTotal: !_isBusy,
            SummaryMessage: BuildSettingsSummaryMessage(settings, total),
            WatchLaterTotalCount: total,
            DownloadCount: settings.DownloadCount,
            BrowserCookies: settings.BrowserCookies.ToString(),
            BrowserProfile: settings.BrowserProfile);
    }

    private AppSettings CreateDraftSettings(LibraryWebServer.SettingsRequest request)
    {
        if (!TryParseBrowserCookieSource(request.BrowserCookies, out var browserCookies))
        {
            throw new InvalidOperationException("That browser is no longer available.");
        }

        var browserProfile = string.IsNullOrWhiteSpace(request.BrowserProfile) ? "Default" : request.BrowserProfile.Trim();
        var matchesCurrentBrowser =
            browserCookies == _settings.BrowserCookies
            && string.Equals(browserProfile, _settings.BrowserProfile, StringComparison.OrdinalIgnoreCase);
        var settings = new AppSettings
        {
            DownloadCount = request.DownloadCount,
            BrowserCookies = browserCookies,
            BrowserProfile = browserProfile,
            SelectedBrowserAccountKey = matchesCurrentBrowser ? _settings.SelectedBrowserAccountKey : null,
            SelectedYouTubeAccountKey = matchesCurrentBrowser ? _settings.SelectedYouTubeAccountKey : null
        };
        settings.Normalize();
        return settings;
    }

    private string BuildSettingsPanelMessage()
    {
        if (_isBusy)
        {
            return "The app is busy. You can still change settings now; refresh the Watch Later total after the current operation finishes.";
        }

        if (!_settings.AutoSyncArmed)
        {
            return "Automatic sync is paused. Click Sync Now to check again, or Refresh Total to inspect Watch Later without starting a sync.";
        }

        return "Refresh Total inspects Watch Later using the settings below.";
    }

    private string BuildSettingsSavedMessage()
    {
        return _isBusy
            ? $"Settings saved. The current operation will finish first; the next sync will use up to {_settings.DownloadCount} videos on {_settings.BrowserCookies}:{_settings.BrowserProfile}."
            : _settings.AutoSyncArmed
                ? $"Sync scope is set to up to the most recent {_settings.DownloadCount} videos using {_settings.BrowserCookies}:{_settings.BrowserProfile}."
                : $"Sync scope is set to up to the most recent {_settings.DownloadCount} videos using {_settings.BrowserCookies}:{_settings.BrowserProfile}. Automatic sync stays paused until you click Sync Now.";
    }

    private static string BuildSettingsSummaryMessage(AppSettings settings, int total)
    {
        return $"Sync scope: up to the most recent {settings.DownloadCount} videos out of {total} currently in Watch Later using {settings.BrowserCookies}:{settings.BrowserProfile}.";
    }

    private static IReadOnlyList<BrowserCookieSource> GetSettingsBrowserOptions()
    {
        var browserOptions = ChromiumBrowserLocator.GetInstalledBrowsers();
        if (browserOptions.Count == 0)
        {
            browserOptions = ChromiumBrowserLocator.GetManagedBrowsers();
        }

        return browserOptions;
    }

    private static bool TryParseBrowserCookieSource(string? value, out BrowserCookieSource browserCookies)
    {
        if (Enum.TryParse(value, ignoreCase: true, out browserCookies)
            && GetSettingsBrowserOptions().Contains(browserCookies))
        {
            return true;
        }

        browserCookies = default;
        return false;
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
                    SetSyncAuthReady();
                }
            });
            var watchLaterOrderProgress = new Progress<IReadOnlyList<string>>(orderedVideoIds =>
            {
                if (MatchesLibrarySelection(syncSettings, _settings))
                {
                    SetWatchLaterOrder(orderedVideoIds);
                    SetSyncAuthReady();
                }
            });
            var syncTargetProgress = new Progress<IReadOnlyList<string>>(targetVideoIds =>
            {
                if (MatchesLibrarySelection(syncSettings, _settings))
                {
                    SetSyncTargetIds(targetVideoIds);
                }
            });
            var summary = await TrackShutdownTask(_syncService.SyncRecentAsync(
                syncSettings,
                progress,
                _shutdownCts.Token,
                watchLaterTotalProgress,
                watchLaterOrderProgress,
                syncTargetProgress));
            var selectionChanged = TryPersistPromptSelectedBrowser(startingSettings, syncSettings);
            var automaticSyncPaused = PauseAutomaticSyncIfSatisfied(summary);
            RefreshLibraryForCurrentSelection(
                clearWatchLaterTotalCount: selectionChanged,
                clearWatchLaterOrder: selectionChanged,
                queueWatchLaterTotalRefresh: !automaticSyncPaused);
            if (summary.WatchLaterTotalCount.HasValue && MatchesLibrarySelection(syncSettings, _settings))
            {
                SetWatchLaterTotalCount(summary.WatchLaterTotalCount.Value);
                SetSyncAuthReady();
            }

            var status = BuildSyncStatus(summary, automaticSyncPaused);
            TrayLog.Write(_paths, $"RunSyncAsync completed. Status: {status}");
            SetBusy(false, status);
            QueueWatchLaterOrderRefresh();
            QueueYouTubeAccountRefresh();
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
            UpdateSyncAuthStateFromException(ex);
            QueueYouTubeAccountRefresh();
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
            await _uiDispatcher.InvokeAsync(async () =>
            {
                MarkVideosLocally(selectedIds, markHidden: true);
                await Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            TrayLog.Write(_paths, "RemoveSelectedFromWatchLaterAsync cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Local hide failed: {ex}");
            ShowErrorBalloon(ex.Message);
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

    private Task<LibraryWebServer.LibraryCommandResponse> QueueRemoveFromBrowserAsync(IReadOnlyList<string> videoIds, bool markHidden)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (videoIds.Count == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Select one or more videos first.");
            }

            var changedCount = MarkVideosLocally(videoIds, markHidden);
            var normalizedCount = NormalizeVideoIdCount(videoIds);
            if (changedCount == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(
                    false,
                    markHidden
                        ? "Those videos are already hidden."
                        : "Those videos are already marked as watched.");
            }

            return new LibraryWebServer.LibraryCommandResponse(
                true,
                markHidden
                    ? $"Hid {normalizedCount} video(s) from the main library view."
                    : $"Marked {normalizedCount} video(s) as watched.");
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueRestoreFromBrowserAsync(IReadOnlyList<string> videoIds)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (videoIds.Count == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Select one or more videos first.");
            }

            var changedCount = RestoreVideosLocally(videoIds);
            var normalizedCount = NormalizeVideoIdCount(videoIds);
            if (changedCount == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Those videos are already in the main library.");
            }

            return new LibraryWebServer.LibraryCommandResponse(
                true,
                $"Restored {normalizedCount} video(s) to the main library.");
        });
    }

    private int MarkVideosLocally(IReadOnlyList<string> videoIds, bool markHidden)
    {
        var accountScope = _accountScopeResolver.Resolve(_settings);
        var changedCount = _videoStateStore.MarkVideos(accountScope.FolderName, videoIds, markHidden);
        if (changedCount > 0)
        {
            RefreshLibraryForCurrentSelection(queueWatchLaterTotalRefresh: false);
        }

        return changedCount;
    }

    private int RestoreVideosLocally(IReadOnlyList<string> videoIds)
    {
        var accountScope = _accountScopeResolver.Resolve(_settings);
        var changedCount = _videoStateStore.RestoreVideos(accountScope.FolderName, videoIds);
        if (changedCount > 0)
        {
            RefreshLibraryForCurrentSelection(queueWatchLaterTotalRefresh: false);
        }

        return changedCount;
    }

    private static int NormalizeVideoIdCount(IReadOnlyList<string> videoIds) =>
        videoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count();

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

    private Task<LibraryWebServer.LibraryCommandResponse> QueueOpenDownloadsFolderFromBrowserAsync()
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            var accountScope = _accountScopeResolver.Resolve(_settings);
            Directory.CreateDirectory(accountScope.DownloadsPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = accountScope.DownloadsPath,
                UseShellExecute = true
            });

            return Task.FromResult(
                new LibraryWebServer.LibraryCommandResponse(
                    true,
                    "Opened the downloads folder for the selected account."));
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
            NormalizeSelectedYouTubeAccountForCurrentBrowser(allowNetwork: false);
            SettingsStore.Save(_settings);
            RefreshLibraryForCurrentSelection(
                clearWatchLaterTotalCount: true,
                clearWatchLaterOrder: true,
                resetSyncAuthState: true);
            QueueWatchLaterOrderRefresh();
            QueueYouTubeAccountRefresh();

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
            var normalizedAccountKey = string.IsNullOrWhiteSpace(accountKey) ? string.Empty : accountKey.Trim();
            var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(
                _settings,
                selectedBrowserAccount?.AuthUserIndex,
                allowNetwork: false);
            var selectedYouTubeAccount = youTubeAccounts.FirstOrDefault(option =>
                string.Equals(option.AccountKey, normalizedAccountKey, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(selectedYouTubeAccount.AccountKey))
            {
                var fallbackSelection = YouTubeAccountDiscoveryService.BuildFallbackSelectedAccount(
                    normalizedAccountKey,
                    selectedBrowserAccount?.AuthUserIndex ?? 0);
                if (!fallbackSelection.HasValue)
                {
                    return new LibraryWebServer.LibraryCommandResponse(false, "That YouTube account is no longer available.");
                }

                selectedYouTubeAccount = fallbackSelection.Value;
            }

            _settings.SelectedAccountKey = null;
            _settings.SelectedYouTubeAccountKey = selectedYouTubeAccount.AccountKey;
            SettingsStore.Save(_settings);
            RefreshLibraryForCurrentSelection(
                clearWatchLaterTotalCount: true,
                clearWatchLaterOrder: true,
                resetSyncAuthState: true,
                queueWatchLaterTotalRefresh: false);
            SetSyncAuthMissing("Authentication has not been checked for this YouTube account yet. Click Sync Now to reconnect if needed.");

            var message = _isBusy
                ? $"Saved YouTube account {selectedYouTubeAccount.Label}. Downloaded videos for that account are shown now, and the next sync will use it."
                : $"Now using YouTube account {selectedYouTubeAccount.Label}. Downloaded videos for that account are shown now.";

            return new LibraryWebServer.LibraryCommandResponse(true, message);
        });
    }

    private void NormalizeSelectedYouTubeAccountForCurrentBrowser(bool allowNetwork = true)
    {
        var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(_settings);
        var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(
            _settings,
            selectedBrowserAccount?.AuthUserIndex,
            allowNetwork: allowNetwork);
        NormalizeSelectedYouTubeAccountForCurrentBrowser(youTubeAccounts);
    }

    private bool NormalizeSelectedYouTubeAccountForCurrentBrowser(IReadOnlyList<YouTubeAccountOption> youTubeAccounts)
    {
        if (youTubeAccounts.Count == 0)
        {
            var hadSelection = !string.IsNullOrWhiteSpace(_settings.SelectedYouTubeAccountKey);
            _settings.SelectedYouTubeAccountKey = null;
            return hadSelection;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedYouTubeAccountKey)
            && youTubeAccounts.Any(option => string.Equals(option.AccountKey, _settings.SelectedYouTubeAccountKey, StringComparison.Ordinal)))
        {
            return false;
        }

        var selectedYouTubeAccount = youTubeAccounts.FirstOrDefault(option => option.IsSelected);
        _settings.SelectedYouTubeAccountKey = string.IsNullOrWhiteSpace(selectedYouTubeAccount.AccountKey)
            ? youTubeAccounts[0].AccountKey
            : selectedYouTubeAccount.AccountKey;
        return true;
    }

    private IReadOnlyList<string> GetCurrentVideoIds()
    {
        var accountScope = _accountScopeResolver.Resolve(_settings);
        return VideoItem.LoadFromDownloads(accountScope.DownloadsPath)
            .Select(item => item.VideoId)
            .ToList();
    }

    private int GetCurrentVideoCount()
    {
        return GetCurrentVideoIds().Count;
    }

    private void RefreshLibraryForCurrentSelection(
        bool clearWatchLaterTotalCount = false,
        bool clearWatchLaterOrder = false,
        bool resetSyncAuthState = false,
        bool queueWatchLaterTotalRefresh = true)
    {
        if (clearWatchLaterTotalCount)
        {
            ClearWatchLaterTotalCount();
        }

        if (clearWatchLaterOrder)
        {
            ClearWatchLaterOrder();
        }

        if (clearWatchLaterTotalCount || clearWatchLaterOrder || resetSyncAuthState)
        {
            ClearSyncTargetIds();
        }

        if (resetSyncAuthState)
        {
            SetSyncAuthMissing();
        }

        _libraryState.MarkLibraryChanged(GetCurrentVideoIds());
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

    private void SetWatchLaterOrder(IReadOnlyList<string> orderedVideoIds)
    {
        _libraryState.SetWatchLaterOrder(orderedVideoIds);
    }

    private void ClearWatchLaterOrder()
    {
        _libraryState.ClearWatchLaterOrder();
    }

    private void SetSyncTargetIds(IReadOnlyList<string> syncTargetVideoIds)
    {
        _libraryState.SetSyncTargetIds(syncTargetVideoIds);
    }

    private void ClearSyncTargetIds()
    {
        _libraryState.ClearSyncTargetIds();
    }

    private void SetSyncAuthReady(string? message = null)
    {
        _libraryState.SetSyncAuthState(
            LibraryBrowserState.SyncAuthState.Ready,
            message ?? "Authentication is working for the selected account.");
    }

    private void SetSyncAuthMissing(string? message = null)
    {
        _libraryState.SetSyncAuthState(
            LibraryBrowserState.SyncAuthState.Missing,
            message ?? "Authentication has not been verified yet for the selected account.");
    }

    private void SetSyncAuthFailed(string? message = null)
    {
        _libraryState.SetSyncAuthState(
            LibraryBrowserState.SyncAuthState.Failed,
            message ?? "Authentication failed for the selected account. Sign in again or refresh cookies.");
    }

    private void UpdateSyncAuthStateFromException(Exception ex)
    {
        if (!SyncService.LooksLikeAuthFailureOutput(ex.Message))
        {
            return;
        }

        SetSyncAuthFailed("Authentication failed for the selected account. YouTube rejected the Watch Later request.");
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

    private void QueueWatchLaterOrderRefresh()
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        var settings = _settings.CreateSnapshot();
        var selectionKey = GetLibrarySelectionKey(settings);
        lock (_watchLaterOrderRefreshGate)
        {
            if (!_watchLaterOrderRefreshSelectionsInFlight.Add(selectionKey))
            {
                return;
            }
        }

        _ = RefreshWatchLaterOrderInBackgroundAsync(settings, selectionKey);
    }

    private void QueueYouTubeAccountRefresh()
    {
        if (_shutdownCts.IsCancellationRequested || _isBusy)
        {
            return;
        }

        var settings = _settings.CreateSnapshot();
        var selectionKey = GetYouTubeAccountRefreshKey(settings);
        lock (_youTubeAccountRefreshGate)
        {
            if (!_youTubeAccountRefreshSelectionsInFlight.Add(selectionKey))
            {
                return;
            }
        }

        _ = RefreshYouTubeAccountsInBackgroundAsync(settings, selectionKey);
    }

    private bool IsYouTubeAccountRefreshInFlight(AppSettings settings)
    {
        var selectionKey = GetYouTubeAccountRefreshKey(settings);
        lock (_youTubeAccountRefreshGate)
        {
            return _youTubeAccountRefreshSelectionsInFlight.Contains(selectionKey);
        }
    }

    private async Task RefreshWatchLaterTotalInBackgroundAsync(AppSettings settings, string selectionKey)
    {
        try
        {
            var total = await TrackShutdownTask(_syncService.GetWatchLaterTotalAsync(settings, _shutdownCts.Token));
            if (MatchesLibrarySelection(settings, _settings))
            {
                SetWatchLaterTotalCount(total);
                SetSyncAuthReady();
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Background Watch Later total refresh failed: {ex.Message}");
            if (MatchesLibrarySelection(settings, _settings))
            {
                UpdateSyncAuthStateFromException(ex);
            }
        }
        finally
        {
            lock (_watchLaterTotalRefreshGate)
            {
                _watchLaterTotalRefreshSelectionsInFlight.Remove(selectionKey);
            }
        }
    }

    private async Task RefreshWatchLaterOrderInBackgroundAsync(AppSettings settings, string selectionKey)
    {
        try
        {
            var orderedVideoIds = await TrackShutdownTask(
                _syncService.GetWatchLaterOrderedIdsAsync(settings, _shutdownCts.Token));
            if (MatchesLibrarySelection(settings, _settings))
            {
                SetWatchLaterOrder(orderedVideoIds);
                SetSyncAuthReady();
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Background Watch Later order refresh failed: {ex.Message}");
            if (MatchesLibrarySelection(settings, _settings))
            {
                UpdateSyncAuthStateFromException(ex);
            }
        }
        finally
        {
            lock (_watchLaterOrderRefreshGate)
            {
                _watchLaterOrderRefreshSelectionsInFlight.Remove(selectionKey);
            }
        }
    }

    private async Task RefreshYouTubeAccountsInBackgroundAsync(AppSettings settings, string selectionKey)
    {
        try
        {
            var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(settings);
            var youTubeAccounts = await Task.Run(
                () => _youTubeAccountDiscovery.DiscoverAccounts(
                    settings,
                    selectedBrowserAccount?.AuthUserIndex,
                    allowNetwork: true),
                _shutdownCts.Token);

            if (youTubeAccounts.Count == 0)
            {
                return;
            }

            await _uiDispatcher.InvokeAsync(async () =>
            {
                if (!MatchesBrowserSelection(settings, _settings))
                {
                    return;
                }

                if (NormalizeSelectedYouTubeAccountForCurrentBrowser(youTubeAccounts))
                {
                    SettingsStore.Save(_settings);
                    RefreshLibraryForCurrentSelection(
                        clearWatchLaterTotalCount: true,
                        clearWatchLaterOrder: true,
                        resetSyncAuthState: true,
                        queueWatchLaterTotalRefresh: false);
                    QueueWatchLaterOrderRefresh();
                }

                await Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Background YouTube account refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_youTubeAccountRefreshGate)
            {
                _youTubeAccountRefreshSelectionsInFlight.Remove(selectionKey);
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

    private static string GetYouTubeAccountRefreshKey(AppSettings settings)
    {
        return string.Join(
            "|",
            settings.BrowserCookies,
            settings.BrowserProfile,
            settings.SelectedBrowserAccountKey ?? string.Empty);
    }

    private static bool MatchesBrowserSelection(AppSettings left, AppSettings right)
    {
        return left.BrowserCookies == right.BrowserCookies
            && string.Equals(left.BrowserProfile, right.BrowserProfile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SelectedBrowserAccountKey, right.SelectedBrowserAccountKey, StringComparison.Ordinal);
    }

    private Task TrackShutdownTask(Task task)
    {
        if (task.IsCompleted)
        {
            return task;
        }

        lock (_trackedTaskGate)
        {
            _trackedTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_trackedTaskGate)
                {
                    _trackedTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private Task<T> TrackShutdownTask<T>(Task<T> task)
    {
        TrackShutdownTask((Task)task);
        return task;
    }

    private async Task WaitForTrackedTasksAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task[] trackedTasks;
            lock (_trackedTaskGate)
            {
                trackedTasks = _trackedTasks
                    .Where(task => !task.IsCompleted)
                    .ToArray();
            }

            if (trackedTasks.Length == 0)
            {
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                TrayLog.Write(_paths, $"Shutdown timed out while waiting for {trackedTasks.Length} tracked task(s).");
                return;
            }

            var waitForAllTask = Task.WhenAll(trackedTasks);
            var delayTask = Task.Delay(remaining);
            var completedTask = await Task.WhenAny(waitForAllTask, delayTask);
            if (completedTask == delayTask)
            {
                TrayLog.Write(_paths, $"Shutdown timed out while waiting for {trackedTasks.Length} tracked task(s).");
                return;
            }

            try
            {
                await waitForAllTask;
            }
            catch
            {
                // Ignore task failures during shutdown; the goal is to let cancellation cleanup finish.
            }
        }
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
