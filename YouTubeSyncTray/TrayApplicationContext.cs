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
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _syncNowMenuItem;
    private AppSettings _settings;
    private bool _isBusy;

    public TrayApplicationContext()
    {
        _paths = YoutubeSyncPaths.Discover();
        _settings = SettingsStore.Load();
        _syncService = new SyncService(_paths, new WinFormsBrowserLoginPrompt(() => Form.ActiveForm));
        _removalService = new YoutubeRemovalService(_paths);
        _thumbnailCacheService = new ThumbnailCacheService(_paths);
        _uiDispatcher = new UiThreadDispatcher();
        _libraryState = new LibraryBrowserState(VideoItem.LoadFromDownloads(_paths.DownloadsPath).Count);
        _libraryWebServer = new LibraryWebServer(
            _paths,
            _thumbnailCacheService,
            _libraryState,
            GetSettingsSnapshot,
            QueueSyncFromBrowserAsync,
            QueueRemoveFromBrowserAsync,
            QueueOpenSettingsFromBrowserAsync);

        _startupMenuItem = new ToolStripMenuItem("Run at Windows startup") { CheckOnClick = true };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        _settingsMenuItem = new ToolStripMenuItem("Settings");
        _settingsMenuItem.Click += async (_, _) => await OpenSettingsAsync();

        _syncNowMenuItem = new ToolStripMenuItem("Sync Now");
        _syncNowMenuItem.Click += async (_, _) => await RunSyncAsync(showSuccessBalloon: true);

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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _libraryWebServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _uiDispatcher.Dispose();
        base.ExitThreadCore();
    }

    private async Task InitializeAsync()
    {
        try
        {
            TrayLog.Write(_paths, "Tray initialization started.");
            await _libraryWebServer.EnsureStartedAsync(CancellationToken.None);
            await RunSyncAsync(showSuccessBalloon: false);
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"Tray initialization failed: {ex}");
            _libraryState.SetBusy(false, $"Initialization failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
        }
    }

    private async Task ShowLibraryAsync()
    {
        try
        {
            await _libraryWebServer.EnsureStartedAsync(CancellationToken.None);
            _libraryWebServer.OpenInBrowser();
        }
        catch (Exception ex)
        {
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
        form.RefreshTotalRequested += async (_, _) => await PopulateSettingsSummaryAsync(form);
        await PopulateSettingsSummaryAsync(form);

        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings.DownloadCount = form.DownloadCount;
        _settings.BrowserCookies = form.BrowserCookies;
        _settings.BrowserProfile = form.BrowserProfile;
        SettingsStore.Save(_settings);
        ShowInfoBalloon($"Automatic sync is set to the most recent {_settings.DownloadCount} videos using {_settings.BrowserCookies}:{_settings.BrowserProfile}.");
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
                BrowserProfile = form.BrowserProfile
            };
            settings.Normalize();
            var total = await _syncService.GetWatchLaterTotalAsync(settings, CancellationToken.None);
            form.BrowserCookies = settings.BrowserCookies;
            form.SetBusy(false, string.Empty);
            form.SetPlaylistSummary(form.DownloadCount, total);
        }
        catch (Exception ex)
        {
            form.SetBusy(false, $"Could not refresh Watch Later total: {ex.Message}");
        }
    }

    private async Task RunSyncAsync(bool showSuccessBalloon)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            TrayLog.Write(_paths, $"RunSyncAsync started. showSuccessBalloon={showSuccessBalloon}");
            SetBusy(true, $"Syncing the most recent {_settings.DownloadCount} videos...");
            var originalBrowser = _settings.BrowserCookies;
            var originalProfile = _settings.BrowserProfile;
            if (showSuccessBalloon)
            {
                ShowInfoBalloon($"Sync started for the most recent {_settings.DownloadCount} Watch Later videos.");
            }
            var progress = new Progress<string>(message => _libraryState.ReportProgress(TruncateProgress(message)));
            var summary = await _syncService.SyncRecentAsync(_settings, progress, CancellationToken.None);
            if (originalBrowser != _settings.BrowserCookies
                || !string.Equals(originalProfile, _settings.BrowserProfile, StringComparison.Ordinal))
            {
                SettingsStore.Save(_settings);
            }
            _libraryState.MarkLibraryChanged(VideoItem.LoadFromDownloads(_paths.DownloadsPath).Count);
            var status = BuildSyncStatus(summary);
            TrayLog.Write(_paths, $"RunSyncAsync completed. Status: {status}");
            SetBusy(false, status);
            if (showSuccessBalloon)
            {
                ShowInfoBalloon(status);
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write(_paths, $"RunSyncAsync failed: {ex}");
            SetBusy(false, $"Sync failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
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
            SetBusy(true, "Removing selected videos from Watch Later...");
            var progress = new Progress<string>(message => _libraryState.ReportProgress(TruncateProgress(message)));
            await _removalService.RemoveFromWatchLaterAsync(_settings, selectedIds, progress, CancellationToken.None);
            SetBusy(false, $"{VideoItem.LoadFromDownloads(_paths.DownloadsPath).Count} downloaded videos");
            ShowInfoBalloon($"Requested removal for {selectedIds.Count} video(s).");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Removal failed: {Truncate(ex.Message)}");
            ShowErrorBalloon(ex.Message);
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        RefreshMenu();
        _libraryState.SetBusy(isBusy, status);
    }

    private void RefreshMenu()
    {
        _startupMenuItem.Checked = StartupService.IsRunAtStartupEnabled();
        _settingsMenuItem.Enabled = !_isBusy;
        _syncNowMenuItem.Enabled = !_isBusy;
        _notifyIcon.Text = _isBusy
            ? "YouTube Sync Tray: busy"
            : $"YouTube Sync Tray: {_settings.DownloadCount} recent videos";
    }

    private void ShowInfoBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(1500, "YouTube Sync Tray", message, ToolTipIcon.Info);
    }

    private void ShowErrorBalloon(string message)
    {
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

    private static string BuildSyncStatus(SyncService.SyncSummary summary)
    {
        if (summary.TargetCount == 0)
        {
            return "No Watch Later videos were found for the current sync range.";
        }

        if (summary.DownloadedCount == 0 && summary.MissingAfterSyncCount == 0)
        {
            return summary.AlreadyPresentCount == summary.TargetCount
                ? $"No new videos to download. The first {summary.TargetCount} Watch Later videos are already on disk."
                : $"Sync checked {summary.TargetCount} videos. Everything already available is on disk.";
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

        return string.Join("; ", parts) + ".";
    }

    private AppSettings GetSettingsSnapshot()
    {
        return new AppSettings
        {
            DownloadCount = _settings.DownloadCount,
            BrowserCookies = _settings.BrowserCookies,
            BrowserProfile = _settings.BrowserProfile
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

            _ = RunSyncAsync(showSuccessBalloon: true);
            return new LibraryWebServer.LibraryCommandResponse(
                true,
                $"Sync started for the most recent {_settings.DownloadCount} Watch Later videos.");
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueRemoveFromBrowserAsync(IReadOnlyList<string> videoIds)
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (_isBusy)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "The tray app is already busy.");
            }

            if (videoIds.Count == 0)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Select one or more videos first.");
            }

            _ = RemoveSelectedFromWatchLaterAsync(videoIds);
            return new LibraryWebServer.LibraryCommandResponse(
                true,
                $"Queued removal for {videoIds.Count} selected video(s).");
        });
    }

    private Task<LibraryWebServer.LibraryCommandResponse> QueueOpenSettingsFromBrowserAsync()
    {
        return _uiDispatcher.InvokeAsync(async () =>
        {
            if (_isBusy)
            {
                return new LibraryWebServer.LibraryCommandResponse(false, "Wait for the current operation to finish.");
            }

            _ = OpenSettingsAsync();
            return new LibraryWebServer.LibraryCommandResponse(true, "Opened the native settings dialog.");
        });
    }
}
