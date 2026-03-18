using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace YouTubeSyncTray;

internal sealed class LibraryWebServer : IAsyncDisposable
{
    private const int PrimaryPort = 80;
    private const int LegacyPort = 48173;
    private static readonly byte[] FaviconBytes = TrayIconFactory.CreatePlayIconBytes();

    private readonly YoutubeSyncPaths _paths;
    private readonly ThumbnailCacheService _thumbnailCacheService;
    private readonly LibraryCatalogStore _libraryCatalogStore;
    private readonly LibraryVideoStateStore _videoStateStore;
    private readonly LibraryBrowserState _state;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<AppSettings, bool> _isYouTubeAccountRefreshInFlight;
    private readonly BrowserAccountDiscoveryService _accountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly AccountScopeResolver _accountScopeResolver;
    private readonly KnownLibraryScopeStore _knownLibraryScopeStore;
    private readonly Func<Task<LibraryCommandResponse>> _requestSyncAsync;
    private readonly Func<IReadOnlyList<string>, bool, Task<LibraryCommandResponse>> _requestRemoveAsync;
    private readonly Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> _requestRestoreAsync;
    private readonly Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> _requestRedownloadAsync;
    private readonly Func<Task<LibraryCommandResponse>> _requestOpenDownloadsFolderAsync;
    private readonly Func<string, Task<LibraryCommandResponse>> _requestSelectBrowserAccountAsync;
    private readonly Func<string, Task<LibraryCommandResponse>> _requestSelectYouTubeAccountAsync;
    private readonly Func<Task<SettingsResponse>> _requestGetSettingsAsync;
    private readonly Func<SettingsRequest, Task<SettingsSummaryResponse>> _requestRefreshSettingsSummaryAsync;
    private readonly Func<SettingsRequest, Task<LibraryCommandResponse>> _requestSaveSettingsAsync;
    private readonly Func<Task<LibraryCommandResponse>> _requestOpenSettingsAsync;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _videoCacheGate = new();
    private readonly string _assetVersion;

    private WebApplication? _app;
    private string? _baseAddress;
    private string _cachedVideoDownloadsPath = string.Empty;
    private Dictionary<string, VideoItem> _cachedVideoItemsById = new(StringComparer.Ordinal);

    public LibraryWebServer(
        YoutubeSyncPaths paths,
        ThumbnailCacheService thumbnailCacheService,
        LibraryCatalogStore libraryCatalogStore,
        LibraryVideoStateStore videoStateStore,
        LibraryBrowserState state,
        Func<AppSettings> getSettings,
        Func<AppSettings, bool> isYouTubeAccountRefreshInFlight,
        BrowserAccountDiscoveryService accountDiscovery,
        YouTubeAccountDiscoveryService youTubeAccountDiscovery,
        AccountScopeResolver accountScopeResolver,
        KnownLibraryScopeStore knownLibraryScopeStore,
        Func<Task<LibraryCommandResponse>> requestSyncAsync,
        Func<IReadOnlyList<string>, bool, Task<LibraryCommandResponse>> requestRemoveAsync,
        Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> requestRestoreAsync,
        Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> requestRedownloadAsync,
        Func<Task<LibraryCommandResponse>> requestOpenDownloadsFolderAsync,
        Func<string, Task<LibraryCommandResponse>> requestSelectBrowserAccountAsync,
        Func<string, Task<LibraryCommandResponse>> requestSelectYouTubeAccountAsync,
        Func<Task<SettingsResponse>> requestGetSettingsAsync,
        Func<SettingsRequest, Task<SettingsSummaryResponse>> requestRefreshSettingsSummaryAsync,
        Func<SettingsRequest, Task<LibraryCommandResponse>> requestSaveSettingsAsync,
        Func<Task<LibraryCommandResponse>> requestOpenSettingsAsync)
    {
        _paths = paths;
        _thumbnailCacheService = thumbnailCacheService;
        _libraryCatalogStore = libraryCatalogStore;
        _videoStateStore = videoStateStore;
        _state = state;
        _getSettings = getSettings;
        _isYouTubeAccountRefreshInFlight = isYouTubeAccountRefreshInFlight;
        _accountDiscovery = accountDiscovery;
        _youTubeAccountDiscovery = youTubeAccountDiscovery;
        _accountScopeResolver = accountScopeResolver;
        _knownLibraryScopeStore = knownLibraryScopeStore;
        _requestSyncAsync = requestSyncAsync;
        _requestRemoveAsync = requestRemoveAsync;
        _requestRestoreAsync = requestRestoreAsync;
        _requestRedownloadAsync = requestRedownloadAsync;
        _requestOpenDownloadsFolderAsync = requestOpenDownloadsFolderAsync;
        _requestSelectBrowserAccountAsync = requestSelectBrowserAccountAsync;
        _requestSelectYouTubeAccountAsync = requestSelectYouTubeAccountAsync;
        _requestGetSettingsAsync = requestGetSettingsAsync;
        _requestRefreshSettingsSummaryAsync = requestRefreshSettingsSummaryAsync;
        _requestSaveSettingsAsync = requestSaveSettingsAsync;
        _requestOpenSettingsAsync = requestOpenSettingsAsync;
        _assetVersion = BuildAssetVersion();
    }

    public async Task<string> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_app is not null && !string.IsNullOrWhiteSpace(_baseAddress))
        {
            return _baseAddress;
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null && !string.IsNullOrWhiteSpace(_baseAddress))
            {
                return _baseAddress;
            }

            var webUiPath = ResolveWebUiPath();
            Exception? lastAddressInUse = null;

            foreach (var plan in GetBindingPlans())
            {
                var openAddress = BuildHttpUrl(LocalBrowserHost.Resolve(), plan.PrimaryPort);
                var phoneAccessProbe = new PhoneAccessProbe(plan.PrimaryPort);

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = [],
                    ContentRootPath = AppContext.BaseDirectory
                });
                builder.WebHost.UseUrls(plan.ListenAddresses);
                builder.Services.Configure<JsonOptions>(options =>
                {
                    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                });

                var app = builder.Build();
                app.Use(async (context, next) =>
                {
                    if (ShouldApplyNoStoreHeaders(context.Request.Path))
                    {
                        ApplyNoStoreHeaders(context.Response.Headers);
                    }

                    await next();
                });
                app.UseExceptionHandler(errorApp =>
                {
                    errorApp.Run(async context =>
                    {
                        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                        var message = BuildApiErrorMessage(exception);
                        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            context.Response.ContentType = "application/json; charset=utf-8";
                            await JsonSerializer.SerializeAsync(
                                context.Response.Body,
                                new LibraryCommandResponse(false, message),
                                _jsonOptions,
                                context.RequestAborted);
                            return;
                        }

                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        await context.Response.WriteAsync(message, context.RequestAborted);
                    });
                });
                MapRoutes(app, webUiPath, phoneAccessProbe);
                try
                {
                    await app.StartAsync(cancellationToken);
                }
                catch (IOException ex) when (IsAddressInUse(ex))
                {
                    lastAddressInUse = ex;
                    TrayLog.Write(_paths, $"Could not bind browser library to {string.Join(", ", plan.ListenAddresses)}; trying the next port plan.");
                    await app.DisposeAsync();
                    continue;
                }

                _app = app;
                _baseAddress = openAddress + "/";
                TrayLog.Write(_paths, $"Library web server started on {_baseAddress} and is listening on {string.Join(", ", plan.ListenAddresses)}.");
                return _baseAddress;
            }

            throw new InvalidOperationException(
                $"The browser library could not start on any supported port plan ({PrimaryPort} and/or {LegacyPort}). Close the process using one of those ports or restart the tray app.",
                lastAddressInUse);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void OpenInBrowser()
    {
        if (string.IsNullOrWhiteSpace(_baseAddress))
        {
            throw new InvalidOperationException("Library web server has not started yet.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _baseAddress,
            UseShellExecute = true
        });
    }

    public async ValueTask DisposeAsync()
    {
        _startGate.Dispose();

        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync();
        }
        catch
        {
        }

        await _app.DisposeAsync();
    }

    private void MapRoutes(WebApplication app, string webUiPath, PhoneAccessProbe phoneAccessProbe)
    {
        app.MapGet("/", (HttpContext context) => GetTemplatedShellAsset(
            context,
            Path.Combine(webUiPath, "index.html"),
            "text/html; charset=utf-8"));
        app.MapGet("/styles.css", (HttpContext context) => GetShellAsset(
            context,
            Path.Combine(webUiPath, "styles.css"),
            "text/css; charset=utf-8"));
        app.MapGet("/app.js", (HttpContext context) => GetShellAsset(
            context,
            Path.Combine(webUiPath, "app.js"),
            "application/javascript; charset=utf-8"));
        app.MapGet("/sw.js", (HttpContext context) => GetTemplatedShellAsset(
            context,
            Path.Combine(webUiPath, "sw.js"),
            "application/javascript; charset=utf-8"));
        app.MapGet("/favicon.ico", (HttpContext context) => GetGeneratedAsset(
            context,
            FaviconBytes,
            "image/x-icon",
            "favicon.ico"));

        app.MapGet("/api/network-info", (HttpContext context) => Results.Json(
            phoneAccessProbe.GetSnapshot(IsLocalRequest(context)),
            _jsonOptions));
        app.MapGet("/api/qr-code", (string? value) =>
        {
            var svg = QrCodeSvgRenderer.CreateSvg(value);
            return string.IsNullOrWhiteSpace(svg)
                ? Results.BadRequest()
                : Results.Text(svg, "image/svg+xml; charset=utf-8");
        });
        app.MapGet("/api/bootstrap", (HttpContext context) =>
        {
            ApplyBootstrapCacheHeaders(context.Response.Headers);
            return Results.Json(
                BuildBootstrapDto(IsLocalRequest(context)),
                _jsonOptions);
        });
        app.MapGet("/api/status", (HttpContext context) => Results.Json(
            BuildStatusDto(IsLocalRequest(context)),
            _jsonOptions));
        app.MapGet("/api/settings", async () => Results.Json(await _requestGetSettingsAsync(), _jsonOptions));
        app.MapGet("/api/videos", () => Results.Json(BuildVideoDtos(), _jsonOptions));
        app.MapGet("/api/videos/{videoId}/thumbnail", GetThumbnailAsync);
        app.MapGet("/api/videos/{videoId}/captions", GetVideoCaptions);
        app.MapGet("/api/videos/{videoId}/captions/{trackKey}/file", GetVideoCaptionFileAsync);
        app.MapGet("/api/videos/{videoId}/stream", GetVideoStreamAsync);
        app.MapGet("/api/browser-accounts/{browser}/{profile}/avatar", GetBrowserAccountAvatar);
        app.MapPost("/api/hotspot/start", (HttpContext context) =>
        {
            if (!IsLocalRequest(context))
            {
                return BuildForbiddenResult();
            }

            var result = phoneAccessProbe.SetHotspotEnabled(enable: true, canControlHotspot: true);
            return Results.Json(
                result,
                _jsonOptions,
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/hotspot/stop", (HttpContext context) =>
        {
            if (!IsLocalRequest(context))
            {
                return BuildForbiddenResult();
            }

            var result = phoneAccessProbe.SetHotspotEnabled(enable: false, canControlHotspot: true);
            return Results.Json(
                result,
                _jsonOptions,
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/sync", async () => ToHttpResult(await _requestSyncAsync()));
        app.MapPost("/api/remove", async (RemoveVideosRequest request) => ToHttpResult(
            await _requestRemoveAsync(request.VideoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
                request.MarkHidden)));
        app.MapPost("/api/restore", async (RestoreVideosRequest request) => ToHttpResult(
            await _requestRestoreAsync(request.VideoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray())));
        app.MapPost("/api/redownload", async (RestoreVideosRequest request) => ToHttpResult(
            await _requestRedownloadAsync(request.VideoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray())));
        app.MapPost("/api/downloads/open", async (HttpContext context) =>
        {
            if (!IsLocalRequest(context))
            {
                return BuildForbiddenResult();
            }

            return ToHttpResult(await _requestOpenDownloadsFolderAsync());
        });
        app.MapPost("/api/browser-account/select", async (SelectAccountRequest request) => ToHttpResult(
            await _requestSelectBrowserAccountAsync(request.AccountKey ?? string.Empty)));
        app.MapPost("/api/youtube-account/select", async (SelectAccountRequest request) => ToHttpResult(
            await _requestSelectYouTubeAccountAsync(request.AccountKey ?? string.Empty)));
        app.MapPost("/api/settings/summary", async (SettingsRequest request) => Results.Json(
            await _requestRefreshSettingsSummaryAsync(request),
            _jsonOptions));
        app.MapPost("/api/settings/save", async (SettingsRequest request) => ToHttpResult(
            await _requestSaveSettingsAsync(request)));
        app.MapPost("/api/settings/open", async () => ToHttpResult(await _requestOpenSettingsAsync()));
    }

    private BrowserLibraryStatusDto BuildStatusDto(bool canOpenDownloadsFolder)
    {
        var settings = _getSettings();
        var snapshot = _state.GetSnapshot(settings);
        var browserAccounts = _accountDiscovery.DiscoverAccounts(settings);
        var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(settings);
        var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(
            settings,
            selectedBrowserAccount?.AuthUserIndex,
            allowNetwork: false);
        var isRefreshingYouTubeAccounts = _isYouTubeAccountRefreshInFlight(settings);
        var selectedYouTubeAccount = _youTubeAccountDiscovery.ResolveSelectedAccount(
            settings,
            selectedBrowserAccount?.AuthUserIndex,
            allowNetwork: false);
        var visibleYouTubeAccounts = youTubeAccounts.ToList();
        if (selectedYouTubeAccount.HasValue
            && visibleYouTubeAccounts.All(account =>
                !string.Equals(account.AccountKey, selectedYouTubeAccount.Value.AccountKey, StringComparison.Ordinal)))
        {
            visibleYouTubeAccounts.Insert(0, selectedYouTubeAccount.Value);
        }

        return new BrowserLibraryStatusDto(
            snapshot.IsBusy,
            snapshot.Status,
            snapshot.VideoCount,
            snapshot.LibraryVersion,
            snapshot.ConfiguredDownloadCount,
            snapshot.WatchLaterTotalCount,
            snapshot.WatchLaterTotalUpdatedAtUtc,
            snapshot.SyncScopeDownloadedCount,
            snapshot.SyncScopeTargetCount,
            snapshot.SyncScopeFailedCount,
            snapshot.SyncAuthState.ToString().ToLowerInvariant(),
            snapshot.SyncAuthMessage,
            snapshot.BrowserName,
            snapshot.BrowserProfile,
            snapshot.RecentMessages,
            snapshot.UpdatedAtUtc,
            canOpenDownloadsFolder,
            selectedBrowserAccount?.AccountKey ?? settings.SelectedBrowserAccountKey ?? string.Empty,
            selectedBrowserAccount?.Label ?? string.Empty,
            browserAccounts
                .Select(account => new BrowserAccountDto(
                    account.AccountKey,
                    account.Label,
                    account.DisplayName,
                    account.Email,
                    account.BrowserName,
                    account.Profile,
                    ResolveBrowserAccountAvatarUrl(account, canOpenDownloadsFolder),
                    account.AuthUserIndex))
                .ToList(),
            isRefreshingYouTubeAccounts,
            selectedYouTubeAccount?.AccountKey ?? settings.SelectedYouTubeAccountKey ?? string.Empty,
            selectedYouTubeAccount?.Label ?? string.Empty,
            visibleYouTubeAccounts
                .Select(account => new YouTubeAccountDto(
                    account.AccountKey,
                    account.Label,
                    account.DisplayName,
                    account.Handle,
                    account.Byline,
                    account.AvatarUrl,
                    account.AuthUserIndex))
                .ToList());
    }

    private BootstrapDto BuildBootstrapDto(bool canOpenDownloadsFolder)
    {
        var status = BuildStatusDto(canOpenDownloadsFolder);
        var selectedScope = _accountScopeResolver.Resolve(_getSettings());
        return new BootstrapDto(
            status,
            BuildVideoDtos(),
            _knownLibraryScopeStore.LoadScopes()
                .Select(scope => new KnownLibraryScopeDto(
                    scope.ScopeKey,
                    scope.FolderName,
                    scope.BrowserAccountKey,
                    scope.BrowserDisplayName,
                    scope.BrowserEmail,
                    scope.BrowserProfile,
                    scope.YouTubeAccountKey,
                    scope.YouTubeDisplayName,
                    scope.YouTubeHandle,
                    scope.DownloadedVideoCount,
                    scope.LastSeenAtUtc,
                    scope.LastSuccessfulSyncAtUtc,
                    scope.IsAvailableOnDisk))
                .ToList(),
            new SelectedScopeDto(
                selectedScope.ScopeKey,
                selectedScope.FolderName,
                selectedScope.BrowserAccountKey,
                selectedScope.YouTubeAccountKey,
                selectedScope.DownloadsPath,
                selectedScope.ThumbnailCachePath,
                selectedScope.ArchivePath),
            DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<LibraryVideoDto> BuildVideoDtos()
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var watchLaterOrder = _state.GetWatchLaterOrderSnapshot();
        var items = LoadVideoItems(accountScope);
        var videoStates = _videoStateStore.Load(accountScope.FolderName);
        _state.SetVideoIds(items.Select(item => item.VideoId).ToList());
        _knownLibraryScopeStore.UpdateScopeInventory(accountScope, items.Count);

        return items
            .Select(item =>
            {
                var videoState = videoStates.TryGetValue(item.VideoId, out var state)
                    ? state
                    : default;
                return new LibraryVideoDto(
                    item.VideoId,
                    item.Title,
                    item.UploaderName,
                    item.GetDisplayIndex(watchLaterOrder),
                    $"/api/videos/{Uri.EscapeDataString(item.VideoId)}/thumbnail?v={GetThumbnailRevision(item)}",
                    $"/api/videos/{Uri.EscapeDataString(item.VideoId)}/stream",
                    $"/api/videos/{Uri.EscapeDataString(item.VideoId)}/captions",
                    videoState.IsWatched,
                    videoState.IsHidden);
            })
            .ToList();
    }

    private async Task<IResult> GetThumbnailAsync(HttpContext context, string videoId, CancellationToken cancellationToken)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = FindVideoItem(accountScope, videoId);
        if (item is null)
        {
            return Results.NotFound();
        }

        var thumbPath = await _thumbnailCacheService.EnsureThumbnailAsync(item, accountScope.ThumbnailCachePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(thumbPath) || !File.Exists(thumbPath))
        {
            return Results.NotFound();
        }

        return BuildCacheableFileResult(context, thumbPath, "image/jpeg");
    }

    private Task<IResult> GetVideoStreamAsync(HttpContext context, string videoId)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = FindVideoItem(accountScope, videoId);
        if (item is null)
        {
            return Task.FromResult<IResult>(Results.NotFound());
        }

        var contentType = GetVideoContentType(item.VideoPath);
        return Task.FromResult(BuildCacheableFileResult(
            context,
            item.VideoPath,
            contentType,
            enableRangeProcessing: true));
    }

    private IResult GetVideoCaptions(string videoId)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = FindVideoItem(accountScope, videoId);
        if (item is null)
        {
            return Results.NotFound();
        }

        return Results.Json(
            item.CaptionTracks
                .Select(track => new CaptionTrackDto(
                    track.TrackKey,
                    track.Label,
                    track.LanguageCode,
                    $"/api/videos/{Uri.EscapeDataString(videoId)}/captions/{Uri.EscapeDataString(track.TrackKey)}/file"))
                .ToList(),
            _jsonOptions);
    }

    private async Task<IResult> GetVideoCaptionFileAsync(
        HttpContext context,
        string videoId,
        string trackKey,
        CancellationToken cancellationToken)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = FindVideoItem(accountScope, videoId);
        if (item is null)
        {
            return Results.NotFound();
        }

        var track = item.CaptionTracks.FirstOrDefault(candidate =>
            string.Equals(candidate.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(track.TrackKey) || !File.Exists(track.SourcePath))
        {
            return Results.NotFound();
        }

        var content = await File.ReadAllTextAsync(track.SourcePath, cancellationToken);
        if (string.Equals(track.Format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            content = CaptionFormatConverter.ConvertSrtToWebVtt(content);
        }

        return BuildCacheableTextResult(
            context,
            content,
            "text/vtt; charset=utf-8",
            track.SourcePath);
    }

    private IResult GetBrowserAccountAvatar(HttpContext context, string browser, string profile)
    {
        if (!IsLocalRequest(context))
        {
            return Results.NotFound();
        }

        if (!Enum.TryParse<BrowserCookieSource>(browser, ignoreCase: true, out var browserSource)
            || !ChromiumBrowserLocator.TryGetProfileAvatarPath(browserSource, profile, out var avatarPath)
            || !File.Exists(avatarPath))
        {
            return Results.NotFound();
        }

        return BuildCacheableFileResult(context, avatarPath, GetImageContentType(avatarPath));
    }

    private IReadOnlyList<VideoItem> LoadVideoItems(AccountScopeResolver.ResolvedAccountScope accountScope)
    {
        var items = _libraryCatalogStore.LoadOrScan(
            accountScope.FolderName,
            accountScope.DownloadsPath,
            _state.GetWatchLaterOrderSnapshot());
        UpdateVideoCache(accountScope.DownloadsPath, items);
        return items;
    }

    private VideoItem? FindVideoItem(AccountScopeResolver.ResolvedAccountScope accountScope, string videoId)
    {
        if (TryGetCachedVideoItem(accountScope.DownloadsPath, videoId, out var cachedItem)
            && File.Exists(cachedItem.VideoPath))
        {
            return cachedItem;
        }

        return LoadVideoItems(accountScope)
            .FirstOrDefault(candidate => string.Equals(candidate.VideoId, videoId, StringComparison.Ordinal));
    }

    private bool TryGetCachedVideoItem(string downloadsPath, string videoId, out VideoItem item)
    {
        lock (_videoCacheGate)
        {
            if (string.Equals(_cachedVideoDownloadsPath, downloadsPath, StringComparison.OrdinalIgnoreCase)
                && _cachedVideoItemsById.TryGetValue(videoId, out var cachedItem)
                && cachedItem is not null)
            {
                item = cachedItem;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void UpdateVideoCache(string downloadsPath, IReadOnlyList<VideoItem> items)
    {
        lock (_videoCacheGate)
        {
            _cachedVideoDownloadsPath = downloadsPath;
            _cachedVideoItemsById = items
                .GroupBy(item => item.VideoId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }
    }

    private static IResult ToHttpResult(LibraryCommandResponse response)
    {
        return Results.Json(
            response,
            statusCode: response.Accepted ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
    }

    private static long GetThumbnailRevision(VideoItem item)
    {
        try
        {
            var info = new FileInfo(item.ThumbnailPath);
            if (info.Exists)
            {
                return info.LastWriteTimeUtc.Ticks;
            }

            return new FileInfo(item.VideoPath).LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return DateTime.UtcNow.Ticks;
        }
    }

    private static string ResolveBrowserAccountAvatarUrl(BrowserAccountOption account, bool allowLocalFallback)
    {
        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            return account.AvatarUrl;
        }

        if (!allowLocalFallback
            || !ChromiumBrowserLocator.TryGetProfileAvatarPath(account.Browser, account.Profile, out var avatarPath)
            || !File.Exists(avatarPath))
        {
            return string.Empty;
        }

        return $"/api/browser-accounts/{Uri.EscapeDataString(account.Browser.ToString())}/{Uri.EscapeDataString(account.Profile)}/avatar?v={GetFileRevision(avatarPath)}";
    }

    private static long GetFileRevision(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return DateTime.UtcNow.Ticks;
        }
    }

    private static string GetVideoContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".m4v" => "video/x-m4v",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }

    private static string GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    private static bool IsAddressInUse(IOException exception)
    {
        return exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private IResult BuildForbiddenResult()
    {
        return Results.Json(
            new LibraryCommandResponse(false, "Hotspot control is only available from the laptop running YouTube Sync."),
            _jsonOptions,
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        var localAddress = context.Connection.LocalIpAddress;
        return localAddress is not null && remoteAddress.Equals(localAddress);
    }

    private static string BuildHttpUrl(string host, int port)
    {
        return port == 80
            ? $"http://{host}"
            : $"http://{host}:{port}";
    }

    private static IReadOnlyList<BindingPlan> GetBindingPlans()
    {
        return
        [
            new BindingPlan(
                PrimaryPort,
                [BuildHttpUrl("0.0.0.0", PrimaryPort), BuildHttpUrl("0.0.0.0", LegacyPort)]),
            new BindingPlan(
                PrimaryPort,
                [BuildHttpUrl("0.0.0.0", PrimaryPort)]),
            new BindingPlan(
                LegacyPort,
                [BuildHttpUrl("0.0.0.0", LegacyPort)])
        ];
    }

    private static bool ShouldApplyNoStoreHeaders(PathString path) =>
        path == "/api/status"
        || path == "/api/settings"
        || path.StartsWithSegments("/api/settings/")
        || path == "/api/sync"
        || path == "/api/remove"
        || path == "/api/restore"
        || path == "/api/redownload"
        || path == "/api/browser-account/select"
        || path == "/api/youtube-account/select"
        || path == "/api/downloads/open"
        || path == "/api/hotspot/start"
        || path == "/api/hotspot/stop";

    private static void ApplyNoStoreHeaders(IHeaderDictionary headers)
    {
        headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";
    }

    private static void ApplyBootstrapCacheHeaders(IHeaderDictionary headers)
    {
        headers["Cache-Control"] = "private, max-age=0, must-revalidate";
    }

    private IResult GetShellAsset(HttpContext context, string path, string contentType)
    {
        return BuildCacheableFileResult(context, path, contentType, immutable: true);
    }

    private IResult GetTemplatedShellAsset(HttpContext context, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        var content = ReplaceAssetTokens(File.ReadAllText(path));
        var isIndex = string.Equals(Path.GetFileName(path), "index.html", StringComparison.OrdinalIgnoreCase);
        return BuildCacheableTextResult(
            context,
            content,
            contentType,
            path,
            immutable: !isIndex,
            cacheControl: isIndex ? "public, max-age=0, must-revalidate" : null);
    }

    private IResult GetGeneratedAsset(HttpContext context, byte[] content, string contentType, string assetName)
    {
        var lastModifiedUtc = Assembly.GetExecutingAssembly().Location is { Length: > 0 } assemblyPath && File.Exists(assemblyPath)
            ? File.GetLastWriteTimeUtc(assemblyPath)
            : DateTime.UtcNow;
        var etag = BuildContentEtag(content);
        if (IsNotModified(context.Request, etag, lastModifiedUtc))
        {
            ApplyCacheHeaders(context.Response.Headers, "public, max-age=31536000, immutable", lastModifiedUtc, etag);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        ApplyCacheHeaders(context.Response.Headers, "public, max-age=31536000, immutable", lastModifiedUtc, etag);
        context.Response.Headers["Content-Disposition"] = $"inline; filename=\"{assetName}\"";
        return Results.File(content, contentType);
    }

    private IResult BuildCacheableFileResult(
        HttpContext context,
        string path,
        string contentType,
        bool immutable = false,
        bool enableRangeProcessing = false)
    {
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        var info = new FileInfo(path);
        var etag = BuildFileEtag(info);
        var cacheControl = immutable
            ? "public, max-age=31536000, immutable"
            : "public, max-age=86400";
        if (!context.Request.Headers.ContainsKey("Range") && IsNotModified(context.Request, etag, info.LastWriteTimeUtc))
        {
            ApplyCacheHeaders(context.Response.Headers, cacheControl, info.LastWriteTimeUtc, etag);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        ApplyCacheHeaders(context.Response.Headers, cacheControl, info.LastWriteTimeUtc, etag);
        return Results.File(path, contentType, enableRangeProcessing: enableRangeProcessing);
    }

    private IResult BuildCacheableTextResult(
        HttpContext context,
        string content,
        string contentType,
        string sourcePath,
        bool immutable = false,
        string? cacheControl = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var lastModifiedUtc = File.Exists(sourcePath) ? File.GetLastWriteTimeUtc(sourcePath) : DateTime.UtcNow;
        var etag = BuildContentEtag(bytes);
        cacheControl ??= immutable
            ? "public, max-age=31536000, immutable"
            : "public, max-age=86400";
        if (IsNotModified(context.Request, etag, lastModifiedUtc))
        {
            ApplyCacheHeaders(context.Response.Headers, cacheControl, lastModifiedUtc, etag);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        ApplyCacheHeaders(context.Response.Headers, cacheControl, lastModifiedUtc, etag);
        return Results.Text(content, contentType);
    }

    private string ReplaceAssetTokens(string content)
    {
        return content.Replace("__APP_VERSION__", _assetVersion, StringComparison.Ordinal);
    }

    private static string BuildAssetVersion()
    {
        var seed = new StringBuilder(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
        try
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
            {
                var assemblyInfo = new FileInfo(assemblyPath);
                seed.Append('|').Append(assemblyInfo.Length).Append('|').Append(assemblyInfo.LastWriteTimeUtc.Ticks);
            }

            var webUiPath = ResolveWebUiPath();
            foreach (var assetName in new[] { "index.html", "app.js", "styles.css", "sw.js" })
            {
                var assetPath = Path.Combine(webUiPath, assetName);
                if (!File.Exists(assetPath))
                {
                    continue;
                }

                var info = new FileInfo(assetPath);
                seed.Append('|').Append(assetName).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
        }
        catch
        {
            // If asset discovery fails, fall back to the assembly version string.
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static void ApplyCacheHeaders(
        IHeaderDictionary headers,
        string cacheControl,
        DateTimeOffset lastModifiedUtc,
        string etag)
    {
        headers["Cache-Control"] = cacheControl;
        headers["ETag"] = etag;
        headers["Last-Modified"] = lastModifiedUtc.ToUniversalTime().ToString("R");
    }

    private static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset lastModifiedUtc)
    {
        var ifNoneMatch = request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            var values = ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Any(value => value == "*" || string.Equals(value, etag, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        if (DateTimeOffset.TryParse(request.Headers.IfModifiedSince.ToString(), out var ifModifiedSince))
        {
            return lastModifiedUtc <= ifModifiedSince;
        }

        return false;
    }

    private static string BuildFileEtag(FileInfo info)
    {
        return $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
    }

    private static string BuildContentEtag(byte[] content)
    {
        return "\"" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() + "\"";
    }

    private string BuildApiErrorMessage(Exception? exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception?.Message)
            ? "The tray app hit an internal error."
            : exception.Message.Trim();
        return $"{detail} Check {_paths.LogsPath} for tray-sync.log details.";
    }

    private static string ResolveWebUiPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "web-ui");
        if (Directory.Exists(bundledPath))
        {
            return bundledPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "YouTubeSyncTray", "web-ui");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the browser library assets.");
    }

    internal readonly record struct LibraryCommandResponse(bool Accepted, string Message);

    private sealed class RemoveVideosRequest
    {
        public List<string> VideoIds { get; set; } = [];

        public bool MarkHidden { get; set; }
    }

    private sealed class RestoreVideosRequest
    {
        public List<string> VideoIds { get; set; } = [];
    }

    private sealed class SelectAccountRequest
    {
        public string? AccountKey { get; set; }
    }

    internal sealed class SettingsRequest
    {
        public int DownloadCount { get; set; }

        public string? BrowserCookies { get; set; }

        public string? BrowserProfile { get; set; }
    }

    private sealed record LibraryVideoDto(
        string VideoId,
        string Title,
        string UploaderName,
        string DisplayIndex,
        string ThumbnailUrl,
        string StreamUrl,
        string CaptionsUrl,
        bool IsWatched,
        bool IsHidden);

    private sealed record CaptionTrackDto(
        string TrackKey,
        string Label,
        string LanguageCode,
        string TrackUrl);

    private sealed record BrowserAccountDto(
        string AccountKey,
        string Label,
        string DisplayName,
        string Email,
        string BrowserName,
        string Profile,
        string AvatarUrl,
        int AuthUserIndex);

    private sealed record YouTubeAccountDto(
        string AccountKey,
        string Label,
        string DisplayName,
        string Handle,
        string Byline,
        string AvatarUrl,
        int AuthUserIndex);

    private sealed record BrowserLibraryStatusDto(
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
        string SyncAuthState,
        string SyncAuthMessage,
        string BrowserName,
        string BrowserProfile,
        IReadOnlyList<string> RecentMessages,
        DateTimeOffset UpdatedAtUtc,
        bool CanOpenDownloadsFolder,
        string SelectedBrowserAccountKey,
        string SelectedBrowserAccountLabel,
        IReadOnlyList<BrowserAccountDto> AvailableBrowserAccounts,
        bool IsRefreshingYouTubeAccounts,
        string SelectedYouTubeAccountKey,
        string SelectedYouTubeAccountLabel,
        IReadOnlyList<YouTubeAccountDto> AvailableYouTubeAccounts);

    private sealed record KnownLibraryScopeDto(
        string ScopeKey,
        string FolderName,
        string BrowserAccountKey,
        string BrowserDisplayName,
        string BrowserEmail,
        string BrowserProfile,
        string YouTubeAccountKey,
        string YouTubeDisplayName,
        string YouTubeHandle,
        int DownloadedVideoCount,
        DateTimeOffset LastSeenAtUtc,
        DateTimeOffset? LastSuccessfulSyncAtUtc,
        bool IsAvailableOnDisk);

    private sealed record SelectedScopeDto(
        string ScopeKey,
        string FolderName,
        string BrowserAccountKey,
        string YouTubeAccountKey,
        string DownloadsPath,
        string ThumbnailCachePath,
        string ArchivePath);

    private sealed record BootstrapDto(
        BrowserLibraryStatusDto Status,
        IReadOnlyList<LibraryVideoDto> Videos,
        IReadOnlyList<KnownLibraryScopeDto> KnownScopes,
        SelectedScopeDto SelectedScope,
        DateTimeOffset SnapshotCapturedAtUtc);

    internal sealed record BrowserOptionDto(
        string Value,
        string Label);

    internal sealed record SettingsResponse(
        int DownloadCount,
        string BrowserCookies,
        string BrowserProfile,
        IReadOnlyList<BrowserOptionDto> AvailableBrowsers,
        bool CanRefreshTotal,
        bool ShouldAutoRefreshSummary,
        string SummaryMessage);

    internal sealed record SettingsSummaryResponse(
        bool CanRefreshTotal,
        string SummaryMessage,
        int? WatchLaterTotalCount,
        int DownloadCount,
        string BrowserCookies,
        string BrowserProfile);

    private sealed record BindingPlan(
        int PrimaryPort,
        string[] ListenAddresses);
}

