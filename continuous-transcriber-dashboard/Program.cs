using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Extensions.FileProviders;
using FormsApplication = System.Windows.Forms.Application;

namespace ContinuousTranscriber.Dashboard;

internal static class Program
{
    private const string ListenUrl = "http://127.0.0.1:5138";
    private const string BrowserUrl = "http://127.0.0.1:5138/";
    private const string MutexName = "NiceWindows.ContinuousTranscriber.Dashboard.Singleton";
    private const string OpenEventName = "NiceWindows.ContinuousTranscriber.Dashboard.Open";

    [STAThread]
    private static async Task Main(string[] args)
    {
        using var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            openRequest.Set();
            return;
        }

        var dashboardRoot = ResolveDashboardRoot();
        var recorderRoot = ResolveRecorderRoot(args, dashboardRoot);
        Directory.SetCurrentDirectory(dashboardRoot);
        var webRoot = ResolveWebRoot(dashboardRoot);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = webRoot,
            ContentRootPath = dashboardRoot
        });
        builder.WebHost.UseUrls(ListenUrl);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddSingleton(new FleetDashboardService(recorderRoot));

        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRoot),
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
            }
        });

        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (UnauthorizedAccessException exception)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            }
        });

        app.MapGet("/api/fleet", async (FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.GetFleetAsync(token)));
        app.MapGet("/api/archive/summary", async (string? devices, FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.GetSummaryAsync(ParseDeviceIds(devices), token)));
        app.MapGet("/api/archive/entries", async (
            DateTimeOffset? start, DateTimeOffset? end, string? q, string? devices,
            FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.GetEntriesAsync(ParseDeviceIds(devices), start, end, q, token)));
        app.MapGet("/api/archive/audio/{deviceId:guid}/{id}", async (
            Guid deviceId, string id, FleetDashboardService fleet, CancellationToken token) =>
        {
            var path = await fleet.ResolveAudioAsync(deviceId, id, token);
            return path is null ? Results.NotFound(new { error = "Recording not found." })
                : Results.File(path, "audio/wav", enableRangeProcessing: true);
        });
        app.MapPost("/api/devices/{deviceId:guid}/download", async (
            Guid deviceId, DownloadRequest request, FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.DownloadAsync(deviceId, request.Start, request.End, false, token)));
        app.MapPost("/api/devices/sync", async (
            SyncRequest request, FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.SyncAsync(request, token)));
        app.MapGet("/api/schedules", async (FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.GetSchedulesAsync(token)));
        app.MapPut("/api/devices/{deviceId:guid}/schedule", async (
            Guid deviceId, ScheduleRequest request, FleetDashboardService fleet, CancellationToken token) =>
            Results.Json(await fleet.SaveScheduleAsync(deviceId, request, token)));
        app.MapGet("/favicon.ico", () =>
            Results.File(DashboardTrayIcon.CreateIcoBytes(), "image/x-icon"));
        app.MapFallback(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
        });

        await app.StartAsync();
        var useTray = OperatingSystem.IsWindows()
                      && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase);
        var openInitially = !args.Contains("--no-open", StringComparer.OrdinalIgnoreCase);

        if (useTray)
        {
            await RunWithTrayAsync(app, openRequest, openInitially);
        }
        else
        {
            var registration = ThreadPool.RegisterWaitForSingleObject(
                openRequest,
                (_, _) => OpenBrowser(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
            try
            {
                if (openInitially)
                {
                    OpenBrowser();
                }

                await app.WaitForShutdownAsync();
            }
            finally
            {
                registration.Unregister(null);
            }
        }
    }

    private static async Task RunWithTrayAsync(
        WebApplication app,
        EventWaitHandle openRequest,
        bool openInitially)
    {
        var ready = new TaskCompletionSource<DashboardTray>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trayThread = new Thread(() =>
        {
            FormsApplication.EnableVisualStyles();
            FormsApplication.SetCompatibleTextRenderingDefault(false);
            using var tray = new DashboardTray(app, openRequest);
            ready.SetResult(tray);
            if (openInitially)
            {
                tray.Open();
            }

            FormsApplication.Run(tray.Context);
        })
        {
            IsBackground = true,
            Name = "Continuous Transcriber Dashboard Tray"
        };
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();

        var dashboardTray = await ready.Task;
        try
        {
            await app.WaitForShutdownAsync();
        }
        finally
        {
            dashboardTray.HideAndExit();
            trayThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static IReadOnlyCollection<Guid> ParseDeviceIds(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Guid.TryParse(item, out var id) ? id : throw new InvalidDataException("A selected device ID is invalid."))
            .ToHashSet();

    private static string ResolveDashboardRoot()
    {
        var current = Directory.GetCurrentDirectory();
        return File.Exists(Path.Combine(current, "ContinuousTranscriber.Dashboard.csproj")) ? current : AppContext.BaseDirectory;
    }

    private static string ResolveRecorderRoot(string[] args, string dashboardRoot)
    {
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, "--transcriber-directory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--working-directory", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && Directory.Exists(args[index + 1])) return Path.GetFullPath(args[index + 1]);
        var sibling = Path.GetFullPath(Path.Combine(dashboardRoot, "..", "Continuous-transcriber"));
        if (File.Exists(Path.Combine(sibling, "transcribe_microphone.py"))) return sibling;
        throw new DirectoryNotFoundException("The sibling Continuous-transcriber folder was not found. Use --transcriber-directory <path>.");
    }

    private static string ResolveWebRoot(string dashboardRoot)
    {
        var candidates = new[] { Path.Combine(dashboardRoot, "dashboard-ui"), Path.Combine(AppContext.BaseDirectory, "dashboard-ui") };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    internal static void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(BrowserUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}

internal sealed class DashboardTray : IDisposable
{
    private readonly WebApplication _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Control _dispatcher;
    private readonly RegisteredWaitHandle _openRegistration;
    private bool _disposed;

    public DashboardTray(WebApplication app, EventWaitHandle openRequest)
    {
        _app = app;
        Context = new ApplicationContext();
        _dispatcher = new Control();
        _dispatcher.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open transcription fleet", null, (_, _) => Open()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit dashboard", null, async (_, _) => await ExitAsync()));

        _icon = DashboardTrayIcon.Create();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Continuous Transcriber Dashboard",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                Open();
            }
        };
        _openRegistration = ThreadPool.RegisterWaitForSingleObject(
            openRequest,
            (_, _) =>
            {
                if (!_dispatcher.IsDisposed && _dispatcher.IsHandleCreated)
                {
                    _dispatcher.BeginInvoke(Open);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public ApplicationContext Context { get; }

    public void Open() => Program.OpenBrowser();

    public void HideAndExit()
    {
        if (_dispatcher.IsDisposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            _notifyIcon.Visible = false;
            Context.ExitThread();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _openRegistration.Unregister(null);
        _notifyIcon.Dispose();
        _icon.Dispose();
        _dispatcher.Dispose();
        Context.Dispose();
    }

    private async Task ExitAsync()
    {
        _notifyIcon.Visible = false;
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        Context.ExitThread();
    }
}

internal static class DashboardTrayIcon
{
    public static byte[] CreateIcoBytes()
    {
        using var icon = Create();
        using var stream = new MemoryStream();
        icon.Save(stream);
        return stream.ToArray();
    }

    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(48, 92, 84));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new Pen(Color.FromArgb(238, 250, 245), 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var points = new[]
        {
            new PointF(6, 17), new PointF(9, 17), new PointF(11, 10),
            new PointF(14, 23), new PointF(17, 7), new PointF(20, 20),
            new PointF(22, 14), new PointF(26, 14)
        };
        graphics.DrawLines(pen, points);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
