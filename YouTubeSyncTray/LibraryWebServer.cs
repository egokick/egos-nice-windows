using System.Diagnostics;
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
    private const int PreferredPort = 48173;
    private static readonly byte[] FaviconBytes = TrayIconFactory.CreatePlayIconBytes();

    private readonly YoutubeSyncPaths _paths;
    private readonly ThumbnailCacheService _thumbnailCacheService;
    private readonly LibraryBrowserState _state;
    private readonly Func<AppSettings> _getSettings;
    private readonly BrowserAccountDiscoveryService _accountDiscovery;
    private readonly YouTubeAccountDiscoveryService _youTubeAccountDiscovery;
    private readonly AccountScopeResolver _accountScopeResolver;
    private readonly Func<Task<LibraryCommandResponse>> _requestSyncAsync;
    private readonly Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> _requestRemoveAsync;
    private readonly Func<string, Task<LibraryCommandResponse>> _requestSelectBrowserAccountAsync;
    private readonly Func<string, Task<LibraryCommandResponse>> _requestSelectYouTubeAccountAsync;
    private readonly Func<Task<LibraryCommandResponse>> _requestOpenSettingsAsync;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private WebApplication? _app;
    private string? _baseAddress;

    public LibraryWebServer(
        YoutubeSyncPaths paths,
        ThumbnailCacheService thumbnailCacheService,
        LibraryBrowserState state,
        Func<AppSettings> getSettings,
        BrowserAccountDiscoveryService accountDiscovery,
        YouTubeAccountDiscoveryService youTubeAccountDiscovery,
        AccountScopeResolver accountScopeResolver,
        Func<Task<LibraryCommandResponse>> requestSyncAsync,
        Func<IReadOnlyList<string>, Task<LibraryCommandResponse>> requestRemoveAsync,
        Func<string, Task<LibraryCommandResponse>> requestSelectBrowserAccountAsync,
        Func<string, Task<LibraryCommandResponse>> requestSelectYouTubeAccountAsync,
        Func<Task<LibraryCommandResponse>> requestOpenSettingsAsync)
    {
        _paths = paths;
        _thumbnailCacheService = thumbnailCacheService;
        _state = state;
        _getSettings = getSettings;
        _accountDiscovery = accountDiscovery;
        _youTubeAccountDiscovery = youTubeAccountDiscovery;
        _accountScopeResolver = accountScopeResolver;
        _requestSyncAsync = requestSyncAsync;
        _requestRemoveAsync = requestRemoveAsync;
        _requestSelectBrowserAccountAsync = requestSelectBrowserAccountAsync;
        _requestSelectYouTubeAccountAsync = requestSelectYouTubeAccountAsync;
        _requestOpenSettingsAsync = requestOpenSettingsAsync;
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
            var port = PreferredPort;
            var baseAddress = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.WebHost.UseUrls(baseAddress);
            builder.Services.Configure<JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (ShouldDisableCaching(context.Request.Path))
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
            MapRoutes(app, webUiPath);
            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (IOException ex) when (IsAddressInUse(ex))
            {
                await app.DisposeAsync();
                throw new InvalidOperationException(
                    $"The browser library could not start on {baseAddress}/ because that localhost port is already in use. Close the process using port {port} or restart the tray app.",
                    ex);
            }

            _app = app;
            _baseAddress = baseAddress + "/";
            TrayLog.Write(_paths, $"Library web server started on {_baseAddress}");
            return _baseAddress;
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

    private void MapRoutes(WebApplication app, string webUiPath)
    {
        app.MapGet("/", () => Results.File(Path.Combine(webUiPath, "index.html"), "text/html; charset=utf-8"));
        app.MapGet("/styles.css", () => Results.File(Path.Combine(webUiPath, "styles.css"), "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => Results.File(Path.Combine(webUiPath, "app.js"), "application/javascript; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.File(FaviconBytes, "image/x-icon"));

        app.MapGet("/api/status", () => Results.Json(BuildStatusDto(), _jsonOptions));
        app.MapGet("/api/videos", () => Results.Json(BuildVideoDtos(), _jsonOptions));
        app.MapGet("/api/videos/{videoId}/thumbnail", GetThumbnailAsync);
        app.MapGet("/api/videos/{videoId}/stream", GetVideoStreamAsync);
        app.MapPost("/api/sync", async () => ToHttpResult(await _requestSyncAsync()));
        app.MapPost("/api/remove", async (RemoveVideosRequest request) => ToHttpResult(
            await _requestRemoveAsync(request.VideoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray())));
        app.MapPost("/api/browser-account/select", async (SelectAccountRequest request) => ToHttpResult(
            await _requestSelectBrowserAccountAsync(request.AccountKey ?? string.Empty)));
        app.MapPost("/api/youtube-account/select", async (SelectAccountRequest request) => ToHttpResult(
            await _requestSelectYouTubeAccountAsync(request.AccountKey ?? string.Empty)));
        app.MapPost("/api/settings/open", async () => ToHttpResult(await _requestOpenSettingsAsync()));
    }

    private BrowserLibraryStatusDto BuildStatusDto()
    {
        var settings = _getSettings();
        var snapshot = _state.GetSnapshot(settings);
        var browserAccounts = _accountDiscovery.DiscoverAccounts(settings);
        var selectedBrowserAccount = _accountDiscovery.ResolveSelectedAccount(settings);
        var youTubeAccounts = _youTubeAccountDiscovery.DiscoverAccounts(
            settings,
            selectedBrowserAccount?.AuthUserIndex,
            allowNetwork: false);
        var selectedYouTubeAccount = _youTubeAccountDiscovery.ResolveSelectedAccount(
            settings,
            selectedBrowserAccount?.AuthUserIndex,
            allowNetwork: false);

        return new BrowserLibraryStatusDto(
            snapshot.IsBusy,
            snapshot.Status,
            snapshot.VideoCount,
            snapshot.LibraryVersion,
            snapshot.DownloadCount,
            snapshot.WatchLaterTotalCount,
            snapshot.BrowserName,
            snapshot.BrowserProfile,
            snapshot.RecentMessages,
            snapshot.UpdatedAtUtc,
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
                    account.AvatarUrl,
                    account.AuthUserIndex))
                .ToList(),
            selectedYouTubeAccount?.AccountKey ?? settings.SelectedYouTubeAccountKey ?? string.Empty,
            selectedYouTubeAccount?.Label ?? string.Empty,
            youTubeAccounts
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

    private IReadOnlyList<LibraryVideoDto> BuildVideoDtos()
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var items = VideoItem.LoadFromDownloads(accountScope.DownloadsPath);
        _state.SetVideoCount(items.Count);

        return items
            .Select(item => new LibraryVideoDto(
                item.VideoId,
                item.Title,
                item.UploaderName,
                item.DisplayIndex,
                $"/api/videos/{Uri.EscapeDataString(item.VideoId)}/thumbnail?v={GetThumbnailRevision(item)}",
                $"/api/videos/{Uri.EscapeDataString(item.VideoId)}/stream"))
            .ToList();
    }

    private async Task<IResult> GetThumbnailAsync(string videoId, CancellationToken cancellationToken)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = VideoItem.LoadFromDownloads(accountScope.DownloadsPath)
            .FirstOrDefault(candidate => string.Equals(candidate.VideoId, videoId, StringComparison.Ordinal));
        if (item is null)
        {
            return Results.NotFound();
        }

        var thumbPath = await _thumbnailCacheService.EnsureThumbnailAsync(item, accountScope.ThumbnailCachePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(thumbPath) || !File.Exists(thumbPath))
        {
            return Results.NotFound();
        }

        return Results.File(thumbPath, "image/jpeg");
    }

    private Task<IResult> GetVideoStreamAsync(string videoId)
    {
        var accountScope = _accountScopeResolver.Resolve(_getSettings());
        var item = VideoItem.LoadFromDownloads(accountScope.DownloadsPath)
            .FirstOrDefault(candidate => string.Equals(candidate.VideoId, videoId, StringComparison.Ordinal));
        if (item is null)
        {
            return Task.FromResult<IResult>(Results.NotFound());
        }

        var contentType = GetVideoContentType(item.VideoPath);
        return Task.FromResult<IResult>(Results.File(item.VideoPath, contentType, enableRangeProcessing: true));
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

    private static bool IsAddressInUse(IOException exception)
    {
        return exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDisableCaching(PathString path) =>
        path == "/"
        || path == "/styles.css"
        || path == "/app.js"
        || path.StartsWithSegments("/api");

    private static void ApplyNoStoreHeaders(IHeaderDictionary headers)
    {
        headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";
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
    }

    private sealed class SelectAccountRequest
    {
        public string? AccountKey { get; set; }
    }

    private sealed record LibraryVideoDto(
        string VideoId,
        string Title,
        string UploaderName,
        string DisplayIndex,
        string ThumbnailUrl,
        string StreamUrl);

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
        int DownloadCount,
        int? WatchLaterTotalCount,
        string BrowserName,
        string BrowserProfile,
        IReadOnlyList<string> RecentMessages,
        DateTimeOffset UpdatedAtUtc,
        string SelectedBrowserAccountKey,
        string SelectedBrowserAccountLabel,
        IReadOnlyList<BrowserAccountDto> AvailableBrowserAccounts,
        string SelectedYouTubeAccountKey,
        string SelectedYouTubeAccountLabel,
        IReadOnlyList<YouTubeAccountDto> AvailableYouTubeAccounts);
}

