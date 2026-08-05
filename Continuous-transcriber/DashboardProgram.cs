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

        var recorderRoot = ResolveRecorderRoot(args);
        Directory.SetCurrentDirectory(recorderRoot);
        var webRoot = ResolveWebRoot(recorderRoot);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = webRoot,
            ContentRootPath = recorderRoot
        });
        builder.WebHost.UseUrls(ListenUrl);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        builder.Services.AddSingleton(new TranscriptArchive(recorderRoot));

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

        app.MapGet("/api/archive/summary", (TranscriptArchive archive) => Results.Json(archive.GetSummary()));
        app.MapGet("/api/archive/entries", (
            DateTimeOffset? start,
            DateTimeOffset? end,
            string? q,
            TranscriptArchive archive) => Results.Json(archive.GetEntries(start, end, q)));
        app.MapGet("/api/archive/audio/{id}", (string id, TranscriptArchive archive) =>
        {
            var path = archive.ResolveAudioPath(id);
            return path is null
                ? Results.NotFound(new { error = "Recording not found." })
                : Results.File(path, "audio/wav", enableRangeProcessing: true);
        });
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

    private static string ResolveRecorderRoot(string[] args)
    {
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, "--working-directory", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && Directory.Exists(args[index + 1]))
        {
            return Path.GetFullPath(args[index + 1]);
        }

        var current = Directory.GetCurrentDirectory();
        return File.Exists(Path.Combine(current, "transcribe_microphone.py"))
            ? current
            : AppContext.BaseDirectory;
    }

    private static string ResolveWebRoot(string recorderRoot)
    {
        var candidates = new[]
        {
            Path.Combine(recorderRoot, "dashboard-ui"),
            Path.Combine(AppContext.BaseDirectory, "dashboard-ui")
        };
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
        menu.Items.Add(new ToolStripMenuItem("Open recorder dashboard", null, (_, _) => Open()));
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
