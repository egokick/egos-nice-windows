using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsPath = System.Drawing.Drawing2D.GraphicsPath;
using DrawingPen = System.Drawing.Pen;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using DrawingSmoothingMode = System.Drawing.Drawing2D.SmoothingMode;
using FormsApplication = System.Windows.Forms.Application;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

#if FINANCE_APP
const string DefaultLocalUrl = "http://127.0.0.1:5137";
Directory.SetCurrentDirectory(FinanceRuntime.ResolveRuntimeWorkingDirectory(args));
var financeDataRoot = FinanceRuntime.ResolveDataRoot();
var webRoot = ResolveWebRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot
});
builder.WebHost.UseUrls(DefaultLocalUrl);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton(_ => FinanceSettings.Load(financeDataRoot, Directory.GetCurrentDirectory(), AppContext.BaseDirectory));
builder.Services.AddSingleton<FinanceStore>();
builder.Services.AddSingleton<FinanceRefreshCoordinator>();
builder.Services.AddSingleton<CodexFinanceRefreshLauncher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<FinanceRefreshCoordinator>());

var app = builder.Build();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/api/finance/state", async (FinanceStore store, FinanceRefreshCoordinator refresher, CancellationToken cancellationToken) =>
{
    var state = await store.GetStateAsync(refresher.Status, cancellationToken);
    return Results.Json(state);
});

app.MapPost("/api/finance/refresh", (CodexFinanceRefreshLauncher launcher) =>
{
    var result = launcher.Start();
    return Results.Json(result);
});

app.MapPost("/api/finance/accounts", async (
    FinanceAccountRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.AddAccountAsync(request, cancellationToken);
    return account is null ? Results.BadRequest(new { error = "Account name and type are required." }) : Results.Json(account);
});

app.MapPut("/api/finance/accounts/{id}", async (
    string id,
    FinanceAccountRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.UpdateAccountAsync(id, request, cancellationToken);
    return account is null ? Results.NotFound(new { error = "Account not found or is read-only." }) : Results.Json(account);
});

app.MapPost("/api/finance/accounts/{id}/values", async (
    string id,
    FinanceAccountValuesRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.UpdateAccountValuesAsync(id, request, cancellationToken);
    return account is null ? Results.NotFound(new { error = "Account not found or is read-only." }) : Results.Json(account);
});

app.MapPost("/api/finance/income", async (
    FinanceIncomeRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var income = await store.RecordIncomeAsync(request, cancellationToken);
    return income is null
        ? Results.BadRequest(new { error = "A known account, posted date, and positive income amount are required." })
        : Results.Json(income);
});

app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "finances.html"));
});

if (OperatingSystem.IsWindows() && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase))
{
    await RunWithFinanceTrayAsync(app, DefaultLocalUrl);
}
else
{
    app.Run();
}
#else
const string DefaultLocalUrl = "http://127.0.0.1:5136";
Directory.SetCurrentDirectory(StartupRegistration.ResolveRuntimeWorkingDirectory(args));
var webRoot = ResolveWebRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot
});
builder.WebHost.UseUrls(DefaultLocalUrl);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton(_ => AppSettings.Load(Directory.GetCurrentDirectory(), AppContext.BaseDirectory));
builder.Services.AddSingleton<DeviceHistoryStore>();
builder.Services.AddSingleton<UiPreferencesStore>();
builder.Services.AddSingleton<RemoteWifiDeviceSource>();
builder.Services.AddSingleton<WindowsNetworkDeviceSource>();
builder.Services.AddSingleton<IDeviceSource, CompositeDeviceSource>();
builder.Services.AddSingleton<PollCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PollCoordinator>());

var app = builder.Build();

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(webRoot)
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/api/state", async (
    DeviceHistoryStore store,
    PollCoordinator poller,
    AppSettings settings,
    CancellationToken cancellationToken) =>
{
    var state = await store.GetDashboardAsync(settings, poller.Status, cancellationToken);
    return Results.Json(state);
});

app.MapGet("/api/history", async (
    DeviceHistoryStore store,
    string? macs,
    int? hours,
    CancellationToken cancellationToken) =>
{
    var requestedHours = Math.Clamp(hours.GetValueOrDefault(24 * 7), 1, 24 * 90);
    var selectedMacs = ParseMacSet(macs);
    var history = await store.GetHistoryAsync(TimeSpan.FromHours(requestedHours), selectedMacs, cancellationToken);
    return Results.Json(history);
});

app.MapGet("/api/ui-preferences", (UiPreferencesStore store) =>
    Results.Json(store.Get()));

app.MapPut("/api/ui-preferences", async (
    UiPreferencesRequest request,
    UiPreferencesStore store,
    CancellationToken cancellationToken) =>
{
    var preferences = await store.SaveAsync(request, cancellationToken);
    return Results.Json(preferences);
});

app.MapPost("/api/devices/{mac}/name", async (
    string mac,
    DeviceNameRequest request,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var device = await store.SetDeviceNameAsync(mac, request.Name, cancellationToken);
    return device is null ? Results.NotFound() : Results.Json(device);
});

app.MapPost("/api/devices/{mac}/groups", async (
    string mac,
    DeviceGroupsRequest request,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var device = await store.SetDeviceGroupsAsync(mac, request.Groups, cancellationToken);
    return device is null ? Results.NotFound() : Results.Json(device);
});

app.MapPost("/api/devices/{mac}/ignore", async (
    string mac,
    DeviceIgnoreRequest request,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var device = await store.SetDeviceIgnoredAsync(mac, request.Ignored, cancellationToken);
    return device is null ? Results.NotFound() : Results.Json(device);
});

app.MapPost("/api/groups", async (
    GroupRequest request,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var groups = await store.CreateGroupAsync(request.Name, cancellationToken);
    return Results.Json(groups);
});

app.MapDelete("/api/groups/{name}", async (
    string name,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var groups = await store.DeleteGroupAsync(name, cancellationToken);
    return Results.Json(groups);
});

app.MapPost("/api/groups/{name}/assign", async (
    string name,
    AssignGroupRequest request,
    DeviceHistoryStore store,
    CancellationToken cancellationToken) =>
{
    var groups = await store.AssignGroupAsync(name, request.Macs, cancellationToken);
    return Results.Json(groups);
});

app.MapPost("/api/poll", async (PollCoordinator poller, CancellationToken cancellationToken) =>
{
    var status = await poller.PollNowAsync(cancellationToken);
    return Results.Json(status);
});

app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
});

if (OperatingSystem.IsWindows() && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase))
{
    await RunWithTrayAsync(app, DefaultLocalUrl);
}
else
{
    app.Run();
}
#endif

#if !FINANCE_APP
static async Task RunWithTrayAsync(WebApplication app, string localUrl)
{
    var trayReady = new TaskCompletionSource<WifiDevicesTray>(TaskCreationOptions.RunContinuationsAsynchronously);
    var trayThread = new Thread(() =>
    {
        FormsApplication.EnableVisualStyles();
        FormsApplication.SetCompatibleTextRenderingDefault(false);
        using var tray = new WifiDevicesTray(app, localUrl);
        trayReady.SetResult(tray);
        FormsApplication.Run(tray.Context);
    })
    {
        IsBackground = true,
        Name = "Wi-Fi Devices Tray"
    };
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();

    var tray = await trayReady.Task;
    try
    {
        await app.RunAsync();
    }
    finally
    {
        tray.Visible = false;
        tray.Context.ExitThread();
        if (!trayThread.Join(TimeSpan.FromSeconds(2)))
        {
            FormsApplication.Exit();
        }
    }
}
#endif

#if FINANCE_APP
static async Task RunWithFinanceTrayAsync(WebApplication app, string localUrl)
{
    var trayReady = new TaskCompletionSource<FinanceTray>(TaskCreationOptions.RunContinuationsAsynchronously);
    var trayThread = new Thread(() =>
    {
        FormsApplication.EnableVisualStyles();
        FormsApplication.SetCompatibleTextRenderingDefault(false);
        using var tray = new FinanceTray(app, localUrl);
        trayReady.SetResult(tray);
        FormsApplication.Run(tray.Context);
    })
    {
        IsBackground = true,
        Name = "Finance Tray"
    };
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();

    var tray = await trayReady.Task;
    try
    {
        await app.RunAsync();
    }
    finally
    {
        tray.Visible = false;
        tray.Context.ExitThread();
        if (!trayThread.Join(TimeSpan.FromSeconds(2)))
        {
            FormsApplication.Exit();
        }
    }
}
#endif

static string ResolveWebRoot()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "web-ui"),
        Path.Combine(AppContext.BaseDirectory, "web-ui")
    };

    foreach (var candidate in candidates)
    {
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return candidates[0];
}

#if !FINANCE_APP
static HashSet<string>? ParseMacSet(string? macs)
{
    if (string.IsNullOrWhiteSpace(macs))
    {
        return null;
    }

    var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var part in macs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var normalized = MacAddress.Normalize(part);
        if (normalized is not null)
        {
            parsed.Add(normalized);
        }
    }

    return parsed.Count == 0 ? null : parsed;
}
#endif

public sealed class WifiDevicesTray : IDisposable
{
    private readonly WebApplication _app;
    private readonly string _localUrl;
    private readonly FormsNotifyIcon _notifyIcon;
    private readonly FormsToolStripMenuItem _startupItem;
    private readonly DrawingIcon _icon;
    private bool _disposed;

    public WifiDevicesTray(WebApplication app, string localUrl)
    {
        _app = app;
        _localUrl = localUrl;
        Context = new System.Windows.Forms.ApplicationContext();

        var menu = new FormsContextMenuStrip();
        var openItem = new FormsToolStripMenuItem("Open Wi-Fi Devices", null, (_, _) => OpenUi());
        _startupItem = new FormsToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = StartupRegistration.IsRegistered(),
            CheckOnClick = false
        };
        var exitItem = new FormsToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());
        menu.Items.Add(openItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = TrayIconFactory.Create();
        _notifyIcon = new FormsNotifyIcon
        {
            Icon = _icon,
            Text = "Wi-Fi Devices",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenUi();
    }

    public System.Windows.Forms.ApplicationContext Context { get; }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Dispose();
        _icon.Dispose();
        Context.Dispose();
    }

    private void OpenUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_localUrl) { UseShellExecute = true });
        }
        catch
        {
            // The tray menu should stay usable even if the default browser cannot be launched.
        }
    }

    private void ToggleStartup()
    {
        if (StartupRegistration.IsRegistered())
        {
            StartupRegistration.Remove();
        }
        else
        {
            StartupRegistration.EnsureRegistered(_localUrl);
        }

        _startupItem.Checked = StartupRegistration.IsRegistered();
    }

    private async Task ExitAsync()
    {
        Visible = false;
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        Context.ExitThread();
    }
}

public sealed class FinanceTray : IDisposable
{
    private readonly WebApplication _app;
    private readonly string _localUrl;
    private readonly FormsNotifyIcon _notifyIcon;
    private readonly DrawingIcon _icon;
    private bool _disposed;

    public FinanceTray(WebApplication app, string localUrl)
    {
        _app = app;
        _localUrl = localUrl;
        Context = new System.Windows.Forms.ApplicationContext();

        var menu = new FormsContextMenuStrip();
        menu.Items.Add(new FormsToolStripMenuItem("Open Finance", null, (_, _) => OpenUi()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new FormsToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()));

        _icon = FinanceTrayIconFactory.Create();
        _notifyIcon = new FormsNotifyIcon
        {
            Icon = _icon,
            Text = "Finance",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                OpenUi();
            }
        };
    }

    public System.Windows.Forms.ApplicationContext Context { get; }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Dispose();
        _icon.Dispose();
        Context.Dispose();
    }

    private void OpenUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_localUrl) { UseShellExecute = true });
        }
        catch
        {
            // The tray menu should remain usable even if the default browser cannot be launched.
        }
    }

    private async Task ExitAsync()
    {
        Visible = false;
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        Context.ExitThread();
    }
}

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WifiDevices";

    public static void EnsureRegistered(string localUrl)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(ValueName, BuildCommand(localUrl), RegistryValueKind.String);
    }

    public static void Remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string command && command.Contains("wifidevices", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCommand(string localUrl)
    {
        var executable = ResolveStartupExecutable();
        var workingDirectory = ResolveStartupWorkingDirectory();
        var arguments = $"--working-directory {Quote(workingDirectory)} --urls {Quote(localUrl)}";
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "wifidevices.dll");
            arguments = $"{Quote(dll)} {arguments}";
        }

        return $"{Quote(executable)} {arguments}";
    }

    private static string ResolveStartupExecutable()
    {
        var bundledExe = Path.Combine(AppContext.BaseDirectory, "wifidevices.exe");
        if (File.Exists(bundledExe))
        {
            return bundledExe;
        }

        return Environment.ProcessPath ?? bundledExe;
    }

    public static string ResolveRuntimeWorkingDirectory(string[] args)
    {
        var requested = ReadArgumentValue(args, "--working-directory");
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
        {
            return requested;
        }

        return ResolveStartupWorkingDirectory();
    }

    private static string ResolveStartupWorkingDirectory()
    {
        var current = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(current, "web-ui")) || File.Exists(Path.Combine(current, ".env")))
        {
            return current;
        }

        return AppContext.BaseDirectory;
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index + 1 < args.Length ? args[index + 1] : null;
        }

        return null;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

public static class FinanceRuntime
{
    public static string ResolveRuntimeWorkingDirectory(string[] args)
    {
        var requested = ReadArgumentValue(args, "--working-directory");
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
        {
            return requested;
        }

        var current = Directory.GetCurrentDirectory();
        return File.Exists(Path.Combine(current, "finance.csproj"))
            ? current
            : AppContext.BaseDirectory;
    }

    public static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FINANCE_APP_DATA_ROOT");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var current = Directory.GetCurrentDirectory();
        candidates.Add(current);
        candidates.Add(Path.Combine(current, "..", "wifidevices"));
        candidates.Add(AppContext.BaseDirectory);

        foreach (var candidate in candidates)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(Path.Combine(fullPath, ".env.finance"))
                || Directory.Exists(Path.Combine(fullPath, "data", "finance")))
            {
                return fullPath;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length ? args[index + 1] : null;
            }
        }

        return null;
    }
}

public static class FinanceTrayIconFactory
{
    public static DrawingIcon Create()
    {
        using var bitmap = new DrawingBitmap(32, 32);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = DrawingSmoothingMode.AntiAlias;
            graphics.Clear(DrawingColor.Transparent);

            using var navy = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 19, 61, 90));
            using var paper = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 239, 248, 255));
            using var green = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 52, 211, 153));
            using var ink = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 19, 61, 90));
            using var border = new DrawingPen(DrawingColor.FromArgb(255, 111, 183, 223), 1.4f);
            using var tile = CreateRoundedRectanglePath(new DrawingRectangle(2, 2, 28, 28), 7);
            using var document = CreateRoundedRectanglePath(new DrawingRectangle(8, 6, 16, 20), 3);

            graphics.FillPath(navy, tile);
            graphics.FillPath(paper, document);
            graphics.DrawPath(border, document);
            graphics.FillRectangle(green, 10, 9, 12, 2);
            graphics.FillRectangle(green, 10, 22, 7, 2);
            using var dollarFont = new DrawingFont("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("$", dollarFont, ink, new System.Drawing.PointF(12, 11));
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);

    private static DrawingGraphicsPath CreateRoundedRectanglePath(DrawingRectangle rectangle, int radius)
    {
        var path = new DrawingGraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public static class TrayIconFactory
{
    public static DrawingIcon Create()
    {
        using var bitmap = new DrawingBitmap(32, 32);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = DrawingSmoothingMode.AntiAlias;
            graphics.Clear(DrawingColor.Transparent);

            using var background = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 25, 34, 31));
            using var border = new DrawingPen(DrawingColor.FromArgb(255, 82, 98, 91), 1.2f);
            using var signal = new DrawingPen(DrawingColor.FromArgb(255, 52, 211, 153), 3.2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            using var dimSignal = new DrawingPen(DrawingColor.FromArgb(255, 118, 141, 132), 3.2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            using var dot = new System.Drawing.SolidBrush(DrawingColor.FromArgb(255, 52, 211, 153));
            using var tile = CreateRoundedRectanglePath(new DrawingRectangle(2, 2, 28, 28), 7);

            graphics.FillPath(background, tile);
            graphics.DrawPath(border, tile);
            graphics.DrawArc(dimSignal, 8, 12, 16, 14, 210, 120);
            graphics.DrawArc(signal, 5, 8, 22, 20, 210, 120);
            graphics.FillEllipse(dot, 14, 21, 5, 5);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);

    private static DrawingGraphicsPath CreateRoundedRectanglePath(DrawingRectangle rectangle, int radius)
    {
        var path = new DrawingGraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public sealed class FinanceSettings
{
    private FinanceSettings(string envPath, string dataRoot, IReadOnlyList<FinanceAccountConfig> accounts, TimeOnly refreshTime, string currency)
    {
        EnvPath = envPath;
        DataRoot = dataRoot;
        Accounts = accounts;
        RefreshTime = refreshTime;
        Currency = currency;
    }

    public string EnvPath { get; }
    public string DataRoot { get; }
    public IReadOnlyList<FinanceAccountConfig> Accounts { get; }
    public TimeOnly RefreshTime { get; }
    public string Currency { get; }

    public static FinanceSettings Load(params string[] searchRoots)
    {
        var explicitPath = Environment.GetEnvironmentVariable("WIFIDEVICES_FINANCE_ENV");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        foreach (var root in searchRoots)
        {
            candidates.Add(Path.Combine(root, ".env.finance"));
        }

        var envPath = candidates.FirstOrDefault(File.Exists) ?? candidates.First();
        var values = File.Exists(envPath)
            ? EnvFile.Read(envPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var currency = values.GetValueOrDefault("FINANCE_CURRENCY", "USD").Trim();
        if (string.IsNullOrWhiteSpace(currency))
        {
            currency = "USD";
        }

        var refreshTime = TimeOnly.TryParse(values.GetValueOrDefault("FINANCE_REFRESH_HOUR"), CultureInfo.InvariantCulture, out var parsedTime)
            ? parsedTime
            : new TimeOnly(7, 0);

        var dataRoot = Path.GetDirectoryName(envPath) ?? Directory.GetCurrentDirectory();
        return new FinanceSettings(envPath, dataRoot, LoadAccounts(values), refreshTime, currency);
    }

    private static IReadOnlyList<FinanceAccountConfig> LoadAccounts(IReadOnlyDictionary<string, string> values)
    {
        var ids = values.Keys
            .Select(key => Regex.Match(key, @"^FINANCE_ACCOUNT_(\d+)_", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => int.TryParse(value, out var number) ? number : int.MaxValue)
            .ToList();

        var accounts = new List<FinanceAccountConfig>();
        foreach (var id in ids)
        {
            string? Get(string name) => values.GetValueOrDefault($"FINANCE_ACCOUNT_{id}_{name}")?.Trim();
            var name = FirstNonBlank(Get("NAME"), $"Account {id}");
            var type = FirstNonBlank(Get("TYPE"), "bank").ToLowerInvariant();
            var institution = FirstNonBlank(Get("INSTITUTION"), "Unknown");
            accounts.Add(new FinanceAccountConfig(
                id,
                name,
                type,
                institution,
                EmptyToNull(Get("LOGIN_URL")),
                EmptyToNull(Get("USERNAME")),
                EmptyToNull(Get("PASSWORD")),
                ParseDecimal(Get("CASH_BALANCE")),
                ParseDecimal(Get("BALANCE_OWED")),
                ParseDecimal(Get("CREDIT_LIMIT")),
                ParseDecimal(Get("CREDIT_AVAILABLE")),
                ParseDecimal(Get("APR_PERCENT")),
                EmptyToNull(Get("COLLECTOR")) ?? (string.IsNullOrWhiteSpace(Get("LOGIN_URL")) ? "manual" : "computer_control"),
                EmptyToNull(Get("COLLECTOR_NOTES"))));
        }

        return accounts;
    }

    private static string FirstNonBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

public sealed class FinanceStore
{
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly FinanceSettings _settings;
    private readonly string _financeDirectory;
    private readonly string _accountsPath;
    private readonly string _snapshotsPath;
    private readonly string _logPath;
    private readonly string _incomePath;

    public FinanceStore(FinanceSettings settings)
    {
        _settings = settings;
        _financeDirectory = Path.Combine(_settings.DataRoot, "data", "finance");
        Directory.CreateDirectory(_financeDirectory);
        _accountsPath = Path.Combine(_financeDirectory, "accounts.json");
        _snapshotsPath = Path.Combine(_financeDirectory, "snapshots.jsonl");
        _logPath = Path.Combine(_financeDirectory, "refresh-log.jsonl");
        _incomePath = Path.Combine(_financeDirectory, "income.json");
    }

    public async Task<FinanceDashboardResponse> GetStateAsync(FinanceRefreshStatus status, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var snapshots = ReadSnapshots().ToList();
            var current = BuildSnapshot("current", persistable: false);
            var configuredAccounts = GetConfiguredAccounts();
            var income = BuildIncomeDashboard(LoadIncomeLedger().Records ?? Array.Empty<FinanceIncomeRecord>(), configuredAccounts);
            return new FinanceDashboardResponse(
                DateTimeOffset.UtcNow,
                _settings.Currency,
                _settings.EnvPath,
                configuredAccounts.Count,
                _settings.RefreshTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                current,
                snapshots,
                ReadLogs().TakeLast(20).Reverse().ToList(),
                income,
                status);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceSnapshot> RefreshAsync(string reason, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = BuildSnapshot(reason, persistable: true);
            if (snapshot.Accounts.Count > 0)
            {
                await AppendJsonLineAsync(_snapshotsPath, snapshot, cancellationToken);
            }

            var computerControlPending = snapshot.Accounts.Any(account => account.Status == "pending" && account.Collector == "computer_control");
            var setupPending = snapshot.Accounts.Any(account => account.Status == "pending" && account.Collector != "computer_control");
            var log = new FinanceRefreshLog(
                DateTimeOffset.UtcNow,
                snapshot.Accounts.Count == 0 ? "warning" : setupPending ? "partial" : computerControlPending ? "queued" : "ok",
                snapshot.Accounts.Count == 0
                    ? $"No finance accounts are configured in {_settings.EnvPath}."
                    : setupPending
                        ? "Some accounts need collector/login setup before they can refresh automatically."
                        : computerControlPending
                            ? "Website accounts are saved and waiting for a Codex Computer Use assisted refresh."
                        : "Finance snapshot refreshed from configured values.",
                reason);
            await AppendJsonLineAsync(_logPath, log, cancellationToken);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> NeedsDailyRefreshAsync(CancellationToken cancellationToken)
    {
        if (GetConfiguredAccounts().Count == 0)
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var latest = ReadSnapshots().LastOrDefault();
            if (latest is null)
            {
                return true;
            }

            var now = DateTimeOffset.Now;
            var refreshAt = new DateTimeOffset(now.Date.Add(_settings.RefreshTime.ToTimeSpan()), now.Offset);
            if (now < refreshAt)
            {
                refreshAt = refreshAt.AddDays(-1);
            }

            return latest.SampledAtUtc < refreshAt;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountSnapshot?> AddAccountAsync(FinanceAccountRequest request, CancellationToken cancellationToken)
    {
        var name = CleanFinanceText(request.Name);
        var kind = CleanFinanceText(request.Kind)?.ToLowerInvariant();
        if (name is null || kind is null)
        {
            return null;
        }

        var record = new UserFinanceAccountRecord(
            $"ui-{Guid.NewGuid():N}",
            name,
            kind,
            CleanFinanceText(request.Institution) ?? "Unknown",
            CleanFinanceText(request.LoginUrl),
            CleanFinanceText(request.Username),
            CleanFinanceText(request.Password),
            ParseDecimal(request.CashBalance),
            ParseDecimal(request.BalanceOwed),
            ParseDecimal(request.CreditLimit),
            ParseDecimal(request.CreditAvailable),
            ParseDecimal(request.AprPercent),
            string.IsNullOrWhiteSpace(request.LoginUrl) ? "manual" : "computer_control",
            CleanFinanceText(request.CollectorNotes));

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            accounts.Add(record);
            await SaveUserAccountsAsync(accounts, cancellationToken);
            return ToFinanceAccount(record.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountSnapshot?> UpdateAccountAsync(string id, FinanceAccountRequest request, CancellationToken cancellationToken)
    {
        var name = CleanFinanceText(request.Name);
        var kind = CleanFinanceText(request.Kind)?.ToLowerInvariant();
        if (name is null || kind is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            var current = accounts[index];
            var loginUrl = CleanFinanceText(request.LoginUrl);
            var password = CleanFinanceText(request.Password);
            var updated = current with
            {
                Name = name,
                Kind = kind,
                Institution = CleanFinanceText(request.Institution) ?? "Unknown",
                LoginUrl = loginUrl,
                Username = CleanFinanceText(request.Username),
                Password = password ?? current.Password,
                CashBalance = ParseDecimal(request.CashBalance),
                BalanceOwed = ParseDecimal(request.BalanceOwed),
                CreditLimit = ParseDecimal(request.CreditLimit),
                CreditAvailable = ParseDecimal(request.CreditAvailable),
                AprPercent = ParseDecimal(request.AprPercent),
                Collector = string.IsNullOrWhiteSpace(loginUrl) ? "manual" : "computer_control",
                CollectorNotes = CleanFinanceText(request.CollectorNotes)
            };
            accounts[index] = updated;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            return ToFinanceAccount(updated.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountSnapshot?> UpdateAccountValuesAsync(string id, FinanceAccountValuesRequest request, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            var current = accounts[index];
            var updated = current with
            {
                CashBalance = ParseDecimal(request.CashBalance) ?? current.CashBalance,
                BalanceOwed = ParseDecimal(request.BalanceOwed) ?? current.BalanceOwed,
                CreditLimit = ParseDecimal(request.CreditLimit) ?? current.CreditLimit,
                CreditAvailable = ParseDecimal(request.CreditAvailable) ?? current.CreditAvailable,
                AprPercent = ParseDecimal(request.AprPercent) ?? current.AprPercent
            };
            accounts[index] = updated;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            var snapshot = BuildSnapshot("assisted", persistable: true);
            await AppendJsonLineAsync(_snapshotsPath, snapshot, cancellationToken);
            await AppendJsonLineAsync(
                _logPath,
                new FinanceRefreshLog(DateTimeOffset.UtcNow, "ok", "Finance values updated from an assisted account check.", "assisted"),
                cancellationToken);
            return ToFinanceAccount(updated.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceIncomeEntry?> RecordIncomeAsync(FinanceIncomeRequest request, CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(request.AccountId);
        var amount = request.Amount;
        if (accountId is null || request.PostedOn is null || amount is null || amount <= 0)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = GetConfiguredAccounts();
            var account = accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return null;
            }

            var kind = NormalizeIncomeKind(request.Kind);
            var currency = NormalizeIncomeCurrency(request.Currency, _settings.Currency);
            var description = CleanFinanceText(request.Description);
            var sourceTransactionId = CleanFinanceText(request.SourceTransactionId);
            var requestedRecordId = CleanFinanceText(request.RecordId);
            var fingerprint = CreateIncomeFingerprint(account.Id, request.PostedOn.Value, amount.Value, currency, kind, description);
            var now = DateTimeOffset.UtcNow;
            var records = (LoadIncomeLedger().Records ?? Array.Empty<FinanceIncomeRecord>()).ToList();
            var index = requestedRecordId is not null
                ? records.FindIndex(existing => string.Equals(existing.Id, requestedRecordId, StringComparison.OrdinalIgnoreCase))
                : records.FindIndex(existing =>
                    string.Equals(existing.AccountId, account.Id, StringComparison.OrdinalIgnoreCase)
                    && ((sourceTransactionId is not null
                            && string.Equals(existing.SourceTransactionId, sourceTransactionId, StringComparison.OrdinalIgnoreCase))
                        || (sourceTransactionId is null
                            && string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))));

            FinanceIncomeRecord record;
            if (index >= 0)
            {
                var existing = records[index];
                record = existing with
                {
                    PostedOn = request.PostedOn.Value,
                    Amount = amount.Value,
                    Currency = currency,
                    Kind = kind,
                    Description = description,
                    SourceTransactionId = sourceTransactionId ?? existing.SourceTransactionId,
                    Fingerprint = fingerprint,
                    LastSeenAtUtc = now
                };
                records[index] = record;
            }
            else
            {
                record = new FinanceIncomeRecord(
                    $"income-{Guid.NewGuid():N}",
                    account.Id,
                    request.PostedOn.Value,
                    amount.Value,
                    currency,
                    kind,
                    description,
                    sourceTransactionId,
                    fingerprint,
                    now,
                    now);
                records.Add(record);
            }

            await SaveIncomeLedgerAsync(records, cancellationToken);
            return ToIncomeEntry(record, account.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    private FinanceSnapshot BuildSnapshot(string reason, bool persistable)
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = GetConfiguredAccounts().Select(ToFinanceAccount).ToList();
        var totalCash = accounts.Where(account => account.Kind is "bank" or "cash" or "checking" or "savings").Sum(account => account.CashBalance ?? 0);
        var totalDebt = accounts.Sum(account => account.BalanceOwed ?? 0);
        var totalCreditAvailable = accounts.Sum(account => account.CreditAvailable ?? 0);
        return new FinanceSnapshot(
            now,
            totalCash,
            totalDebt,
            totalCreditAvailable,
            totalCash - totalDebt,
            accounts,
            reason,
            persistable);
    }

    private static FinanceAccountSnapshot ToFinanceAccount(FinanceAccountConfig config)
    {
        var owed = config.BalanceOwed;
        var available = config.CreditAvailable ?? (config.CreditLimit is not null && owed is not null ? config.CreditLimit - owed : null);
        var bankLike = config.Kind is "bank" or "cash" or "checking" or "savings";
        var hasValues = config.Kind == "credit_card"
            ? owed is not null || available is not null
            : bankLike
                ? config.CashBalance is not null
                : config.CashBalance is not null || owed is not null || available is not null;
        var status = config.Kind == "credit_card" && owed is null && available is null
            ? "pending"
            : bankLike && config.CashBalance is null ? "pending"
            : hasValues ? "ok" : "pending";
        var message = status == "pending"
            ? config.Collector == "computer_control"
                ? "Website refresh requires a Codex Computer Use assisted session."
                : "Add manual values or a browser collector for this account."
            : null;

        return new FinanceAccountSnapshot(
            config.Id,
            config.Name,
            config.Kind,
            config.Institution,
            config.LoginUrl,
            config.Username,
            config.Collector,
            config.CashBalance,
            owed,
            config.CreditLimit,
            available,
            config.AprPercent,
            config.CreditLimit is > 0 && owed is not null ? Math.Round(owed.Value / config.CreditLimit.Value * 100, 1) : null,
            status,
            message,
            config.CollectorNotes);
    }

    private FinanceIncomeDashboard BuildIncomeDashboard(
        IReadOnlyList<FinanceIncomeRecord> records,
        IReadOnlyList<FinanceAccountConfig> accounts)
    {
        var accountNames = accounts.ToDictionary(account => account.Id, account => account.Name, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var salaryCutoff = today.AddMonths(-12);
        var salary = records
            .Where(record => record.Kind == "salary" && record.PostedOn >= salaryCutoff)
            .GroupBy(record => new { record.AccountId, record.Currency })
            .Select(group =>
            {
                var latest = group.OrderByDescending(record => record.PostedOn).ThenByDescending(record => record.LastSeenAtUtc).First();
                return new FinanceSalarySummary(
                    group.Key.AccountId,
                    accountNames.GetValueOrDefault(group.Key.AccountId, "Unknown account"),
                    group.Key.Currency,
                    latest.Amount,
                    latest.PostedOn,
                    group.Sum(record => record.Amount),
                    group.Count());
            })
            .OrderByDescending(summary => summary.LatestPaymentOn)
            .ToList();
        var tracking = accounts
            .Select(account =>
            {
                var hasStoredIncome = records.Any(record => string.Equals(record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase));
                return new FinanceIncomeTracking(
                    account.Id,
                    account.Name,
                    hasStoredIncome,
                    hasStoredIncome ? today.AddDays(-30) : today.AddMonths(-24));
            })
            .ToList();
        var recent = records
            .OrderByDescending(record => record.PostedOn)
            .ThenByDescending(record => record.LastSeenAtUtc)
            .Take(12)
            .Select(record => ToIncomeEntry(record, accountNames.GetValueOrDefault(record.AccountId, "Unknown account")))
            .ToList();
        var salaryPayments = records
            .Where(record => record.Kind == "salary")
            .OrderBy(record => record.PostedOn)
            .ThenBy(record => record.LastSeenAtUtc)
            .Select(record => ToIncomeEntry(record, accountNames.GetValueOrDefault(record.AccountId, "Unknown account")))
            .ToList();

        return new FinanceIncomeDashboard(records.Count, salary, tracking, salaryPayments, recent);
    }

    private static FinanceIncomeEntry ToIncomeEntry(FinanceIncomeRecord record, string accountName) =>
        new(record.Id, record.AccountId, accountName, record.PostedOn, record.Amount, record.Currency, record.Kind, record.Description);

    private IReadOnlyList<FinanceAccountConfig> GetConfiguredAccounts() =>
        _settings.Accounts.Concat(LoadUserAccounts().Select(account => account.ToConfig())).ToList();

    private IReadOnlyList<UserFinanceAccountRecord> LoadUserAccounts()
    {
        if (!File.Exists(_accountsPath))
        {
            return Array.Empty<UserFinanceAccountRecord>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<UserFinanceAccountRecord>>(File.ReadAllText(_accountsPath), LineJson) ?? new();
        }
        catch
        {
            return Array.Empty<UserFinanceAccountRecord>();
        }
    }

    private async Task SaveUserAccountsAsync(IReadOnlyList<UserFinanceAccountRecord> accounts, CancellationToken cancellationToken)
    {
        var tempPath = _accountsPath + ".tmp";
        var json = JsonSerializer.Serialize(accounts, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _accountsPath, true);
    }

    private FinanceIncomeLedger LoadIncomeLedger()
    {
        if (!File.Exists(_incomePath))
        {
            return FinanceIncomeLedger.Empty;
        }

        try
        {
            var ledger = JsonSerializer.Deserialize<FinanceIncomeLedger>(File.ReadAllText(_incomePath), LineJson);
            return ledger is null ? FinanceIncomeLedger.Empty : ledger with { Records = ledger.Records ?? Array.Empty<FinanceIncomeRecord>() };
        }
        catch
        {
            return FinanceIncomeLedger.Empty;
        }
    }

    private async Task SaveIncomeLedgerAsync(IReadOnlyList<FinanceIncomeRecord> records, CancellationToken cancellationToken)
    {
        var tempPath = _incomePath + ".tmp";
        var ledger = new FinanceIncomeLedger(1, records);
        var json = JsonSerializer.Serialize(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _incomePath, true);
    }

    private static string? CleanFinanceText(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string NormalizeIncomeKind(string? value)
    {
        var kind = CleanFinanceText(value)?.ToLowerInvariant();
        return kind is "salary" or "bonus" or "other" ? kind : "other";
    }

    private static string NormalizeIncomeCurrency(string? value, string fallback)
    {
        var currency = CleanFinanceText(value)?.ToUpperInvariant() ?? fallback.ToUpperInvariant();
        return currency.Length == 3 && currency.All(char.IsLetter) ? currency : fallback.ToUpperInvariant();
    }

    private static string CreateIncomeFingerprint(
        string accountId,
        DateOnly postedOn,
        decimal amount,
        string currency,
        string kind,
        string? description)
    {
        var normalizedDescription = Regex.Replace(description?.Trim() ?? string.Empty, @"\s+", " ").ToUpperInvariant();
        var input = string.Join("\n", accountId.ToUpperInvariant(), postedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), amount.ToString("0.00", CultureInfo.InvariantCulture), currency, kind, normalizedDescription);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private IEnumerable<FinanceSnapshot> ReadSnapshots()
    {
        if (!File.Exists(_snapshotsPath))
        {
            return Enumerable.Empty<FinanceSnapshot>();
        }

        return ReadJsonLines<FinanceSnapshot>(_snapshotsPath).ToList();
    }

    private IEnumerable<FinanceRefreshLog> ReadLogs()
    {
        if (!File.Exists(_logPath))
        {
            return Enumerable.Empty<FinanceRefreshLog>();
        }

        return ReadJsonLines<FinanceRefreshLog>(_logPath).ToList();
    }

    private static async Task AppendJsonLineAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(value, LineJson) + Environment.NewLine, cancellationToken);
    }

    private static IEnumerable<T> ReadJsonLines<T>(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(line, LineJson);
            }
            catch
            {
                continue;
            }

            if (value is not null)
            {
                yield return value;
            }
        }
    }
}

public sealed class FinanceRefreshCoordinator : BackgroundService
{
    private readonly FinanceStore _store;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public FinanceRefreshCoordinator(FinanceStore store)
    {
        _store = store;
    }

    public FinanceRefreshStatus Status { get; private set; } = FinanceRefreshStatus.NotStarted;

    public async Task<FinanceRefreshStatus> RefreshNowAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            Status = Status with { IsRunning = true, LastStartedUtc = DateTimeOffset.UtcNow, Error = null };
            var snapshot = await _store.RefreshAsync("manual", cancellationToken);
            var hasSetupPending = snapshot.Accounts.Any(account => account.Status == "pending" && account.Collector != "computer_control");
            var hasComputerControlPending = snapshot.Accounts.Any(account => account.Status == "pending" && account.Collector == "computer_control");
            Status = new FinanceRefreshStatus(
                Status.LastStartedUtc,
                DateTimeOffset.UtcNow,
                false,
                snapshot.Accounts.Count > 0 && !hasSetupPending,
                snapshot.Accounts.Count,
                null,
                snapshot.Accounts.Count == 0
                    ? "No finance accounts are configured yet."
                    : hasSetupPending
                        ? "Refresh partially complete; some accounts need collector setup."
                        : hasComputerControlPending
                            ? "Website accounts are saved and waiting for a Codex Computer Use assisted refresh."
                        : "Refresh complete.");
            return Status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new FinanceRefreshStatus(Status.LastStartedUtc, DateTimeOffset.UtcNow, false, false, 0, ex.Message, null);
            return Status;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _store.NeedsDailyRefreshAsync(stoppingToken))
                {
                    await RefreshNowAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status = new FinanceRefreshStatus(Status.LastStartedUtc, DateTimeOffset.UtcNow, false, false, 0, ex.Message, null);
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
    }
}

public sealed class CodexFinanceRefreshLauncher
{
    private const string FinanceRefreshPrompt =
        "Follow every instruction in \"C:\\source\\egos-nice-windows\\wifidevices\\AGENTS.md\" and complete today's entire finance refresh; partial completion is failure. " +
        "Refresh and successfully POST values for every configured finance account before collecting UFCU income. " +
        "For UFCU, allow at least 60 seconds after each major navigation and another 60 seconds if the authenticated page is still loading. " +
        "Use the transaction Filter with Start Date equal to income.tracking.lookbackStartOn, End Date equal to today, and Incoming selected. " +
        "Apply the filter and repeatedly use Load More until it disappears and the displayed result count reconciles with all loaded incoming transactions. " +
        "Open each possible income transaction and use its detail panel for the exact posted date, amount, description, and source transaction ID; never infer values from the biweekly schedule. " +
        "Liberty Mutual ACH transactions identified as PAYROLL with Entry Class Code PPD are salary. Exclude transfers, refunds, reversals, adjustments, reimbursements, cash deposits, and other non-income credits. " +
        "Compare against existing finance income records, update changed transactions by stable source ID when supported, and never create duplicates to reach an expected count. " +
        "Biweekly payroll should produce roughly 26-27 deposits per full year, so a substantially smaller result requires checking loading, filters, pagination, and date coverage again. " +
        "Save every verified qualifying deposit through the finance income API, then re-read finance state and do not report success until every account POST and the UFCU income count, dates, amounts, latest payment, and totals reconcile exactly.";

    private readonly object _sync = new();
    private Process? _process;

    public CodexRefreshLaunchResult Start()
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                return new CodexRefreshLaunchResult(
                    false,
                    true,
                    _process.Id,
                    "A Codex finance refresh is already running.",
                    null);
            }

            try
            {
                var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
                var process = new Process
                {
                    StartInfo = BuildStartInfo(repositoryRoot),
                    EnableRaisingEvents = true
                };
                if (!process.Start())
                {
                    process.Dispose();
                    return new CodexRefreshLaunchResult(false, false, null, null, "Codex could not be started.");
                }

                _process = process;
                _ = TrackProcessAsync(process);
                return new CodexRefreshLaunchResult(
                    true,
                    false,
                    process.Id,
                    "Opened a visible Codex finance refresh session.",
                    null);
            }
            catch (Exception ex)
            {
                return new CodexRefreshLaunchResult(false, false, null, null, ex.Message);
            }
        }
    }

    private static ProcessStartInfo BuildStartInfo(string repositoryRoot)
    {
        var codexArguments = new[]
        {
            ResolveCodexExecutable(),
            "--model",
            "gpt-5.6-luna",
            "--config",
            "model_reasoning_effort=\"low\"",
            "--config",
            "service_tier=\"fast\"",
            "--ask-for-approval",
            "never",
            "--enable",
            "fast_mode",
            "--enable",
            "browser_use",
            "--enable",
            "browser_use_external",
            "--enable",
            "browser_use_full_cdp_access",
            "--sandbox",
            // The Windows workspace-write helper can fail before a finance refresh
            // gets to its first read. This user-initiated workflow must read the
            // local credential-backed finance store and drive the configured browser.
            "danger-full-access",
            FinanceRefreshPrompt
        };

        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandInterpreter))
        {
            commandInterpreter = "cmd.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /c call {BuildWindowsCommandLine(codexArguments)}",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        return startInfo;
    }

    private async Task TrackProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // The launch result has already been returned to the UI; a later
            // finance state read reflects any values the agent successfully saved.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            process.Dispose();
        }
    }

    private static string BuildWindowsCommandLine(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteWindowsCommandArgument));

    private static string QuoteWindowsCommandArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var result = new StringBuilder("\"");
        var consecutiveBackslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                consecutiveBackslashes++;
                continue;
            }

            if (character == '\"')
            {
                result.Append('\\', consecutiveBackslashes * 2 + 1);
                result.Append(character);
                consecutiveBackslashes = 0;
                continue;
            }

            result.Append('\\', consecutiveBackslashes);
            consecutiveBackslashes = 0;
            result.Append(character);
        }

        result.Append('\\', consecutiveBackslashes * 2);
        result.Append('\"');
        return result.ToString();
    }

    private static string ResolveCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var npmCodex = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        if (File.Exists(npmCodex))
        {
            return npmCodex;
        }

        return "codex.cmd";
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return startDirectory;
    }
}

public static class EnvFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }
}

public sealed class AppSettings
{
    private readonly Dictionary<string, string> _values;

    private AppSettings(Dictionary<string, string> values, string envPath)
    {
        _values = values;
        EnvPath = envPath;
        Url = Get("url", "remote_url", "wifi_url", "endpoint");
        Username = Get("username", "user", "login");
        Password = Get("password", "pass");
        DeviceCode = Get("devicecode", "device_code", "code");
        PollInterval = TimeSpan.FromMinutes(GetDouble(5, "poll_interval_minutes", "interval_minutes", "poll_minutes"));
        PingTimeoutMs = Math.Clamp(GetInt(350, "ping_timeout_ms"), 100, 2000);
        MaxPingHosts = Math.Clamp(GetInt(512, "max_ping_hosts"), 32, 2048);
        IgnoreTlsErrors = GetBool(true, "ignore_tls_errors", "allow_invalid_tls", "ignore_certificate_errors");
    }

    public string EnvPath { get; }
    public string? Url { get; }
    public string? Username { get; }
    public string? Password { get; }
    public string? DeviceCode { get; }
    public TimeSpan PollInterval { get; }
    public int PingTimeoutMs { get; }
    public int MaxPingHosts { get; }
    public bool IgnoreTlsErrors { get; }
    public bool HasRemoteUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public static AppSettings Load(params string[] searchRoots)
    {
        var explicitPath = Environment.GetEnvironmentVariable("WIFIDEVICES_ENV");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        foreach (var root in searchRoots)
        {
            candidates.Add(Path.Combine(root, ".env"));
        }

        var envPath = candidates.FirstOrDefault(File.Exists) ?? candidates.Last();
        var values = File.Exists(envPath)
            ? ParseEnvFile(envPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new AppSettings(values, envPath);
    }

    public object ToSafeDto() => new
    {
        hasRemoteUrl = HasRemoteUrl,
        hasCredentials = HasCredentials,
        hasDeviceCode = !string.IsNullOrWhiteSpace(DeviceCode),
        pollIntervalMinutes = PollInterval.TotalMinutes,
        envPath = EnvPath
    };

    private string? Get(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (_values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private int GetInt(int fallback, params string[] keys)
    {
        var value = Get(keys);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private double GetDouble(double fallback, params string[] keys)
    {
        var value = Get(keys);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private bool GetBool(bool fallback, params string[] keys)
    {
        var value = Get(keys);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => fallback
        };
    }

    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = FindSeparator(line);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static int FindSeparator(string line)
    {
        var equals = line.IndexOf('=');
        var colon = line.IndexOf(':');

        if (equals < 0)
        {
            return colon;
        }

        if (colon < 0)
        {
            return equals;
        }

        return Math.Min(equals, colon);
    }
}

public sealed class PollCoordinator : BackgroundService
{
    private readonly IDeviceSource _source;
    private readonly DeviceHistoryStore _store;
    private readonly AppSettings _settings;
    private readonly ILogger<PollCoordinator> _logger;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private PollStatus _status = PollStatus.NotStarted;

    public PollCoordinator(
        IDeviceSource source,
        DeviceHistoryStore store,
        AppSettings settings,
        ILogger<PollCoordinator> logger)
    {
        _source = source;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    public PollStatus Status => _status;

    public async Task<PollStatus> PollNowAsync(CancellationToken cancellationToken)
    {
        if (!await _pollLock.WaitAsync(0, cancellationToken))
        {
            return _status with { Message = "Poll already running." };
        }

        var started = DateTimeOffset.UtcNow;
        _status = _status with
        {
            IsRunning = true,
            LastStartedUtc = started,
            Message = null,
            Error = null
        };

        try
        {
            var result = await _source.PollAsync(cancellationToken);
            if (result.Success)
            {
                await _store.ApplyPollAsync(result, cancellationToken);
            }

            _status = new PollStatus(
                started,
                DateTimeOffset.UtcNow,
                false,
                result.Success,
                result.SourceName,
                result.SourceKind,
                result.Devices.Count,
                result.Error,
                result.Message);

            return _status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Wi-Fi device poll failed.");
            _status = new PollStatus(
                started,
                DateTimeOffset.UtcNow,
                false,
                false,
                null,
                null,
                0,
                ex.Message,
                null);
            return _status;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollNowAsync(stoppingToken);
            try
            {
                await Task.Delay(_settings.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public interface IDeviceSource
{
    Task<DevicePollResult> PollAsync(CancellationToken cancellationToken);
}

public sealed class CompositeDeviceSource : IDeviceSource
{
    private readonly RemoteWifiDeviceSource _remote;
    private readonly WindowsNetworkDeviceSource _local;

    public CompositeDeviceSource(RemoteWifiDeviceSource remote, WindowsNetworkDeviceSource local)
    {
        _remote = remote;
        _local = local;
    }

    public async Task<DevicePollResult> PollAsync(CancellationToken cancellationToken)
    {
        var remote = await _remote.PollAsync(cancellationToken);
        if (remote.Success && remote.Devices.Count > 0)
        {
            return remote;
        }

        var local = await _local.PollAsync(cancellationToken);
        if (local.Success && local.Devices.Count > 0)
        {
            return local;
        }

        var errors = new List<string>();
        if (!remote.Success && !string.IsNullOrWhiteSpace(remote.Error))
        {
            errors.Add(remote.Error);
        }
        else if (remote.Success)
        {
            errors.Add("Remote poll returned no recognizable devices.");
        }

        if (!local.Success && !string.IsNullOrWhiteSpace(local.Error))
        {
            errors.Add(local.Error);
        }
        else if (local.Success)
        {
            errors.Add("Local fallback returned no devices.");
        }

        return DevicePollResult.Failure(
            errors.Count == 0 ? "No device source returned devices." : string.Join(" ", errors),
            "none",
            "none");
    }
}

public sealed class RemoteWifiDeviceSource : IDeviceSource
{
    private static readonly Uri[] NoUris = Array.Empty<Uri>();
    private static readonly Uri LocalDeviceListUri = new("http://192.168.1.254/cgi-bin/devices.ha");
    private readonly AppSettings _settings;

    public RemoteWifiDeviceSource(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<DevicePollResult> PollAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        if (_settings.IgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WifiDevicesPoller/1.0");

        var attempted = BuildDeviceListUris().ToList();
        if (attempted.Count == 0)
        {
            return DevicePollResult.Failure("No valid remote URL is configured.", "remote", "remote");
        }

        var failures = new List<string>();
        foreach (var sourceUri in attempted)
        {
            try
            {
                var first = await FetchAsync(client, sourceUri, cancellationToken);
                var devices = DeviceExtractor.Extract(first.Body, first.ContentType, "remote").ToList();

                if (devices.Count == 0 && _settings.HasCredentials && LooksLikeLogin(first.Body))
                {
                    await TryLoginAsync(client, sourceUri, first.Body, cancellationToken);
                    var afterLogin = await FetchAsync(client, sourceUri, cancellationToken);
                    devices = DeviceExtractor.Extract(afterLogin.Body, afterLogin.ContentType, "remote").ToList();
                }

                if (devices.Count == 0)
                {
                    failures.Add($"{sourceUri.Host}: reachable but no MAC/IP device rows were recognized");
                    continue;
                }

                return DevicePollResult.Ok($"Remote router ({sourceUri.Host})", "remote", true, devices, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{sourceUri.Host}: {InnermostMessage(ex)}");
            }
        }

        return DevicePollResult.Failure($"Remote poll failed: {string.Join("; ", failures)}", "Remote router", "remote");
    }

    private IEnumerable<Uri> BuildDeviceListUris()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_settings.HasRemoteUrl && Uri.TryCreate(_settings.Url, UriKind.Absolute, out var configuredUri))
        {
            foreach (var candidate in ExpandConfiguredDeviceListUris(configuredUri))
            {
                if (seen.Add(candidate.AbsoluteUri))
                {
                    yield return candidate;
                }
            }
        }

        if (seen.Add(LocalDeviceListUri.AbsoluteUri))
        {
            yield return LocalDeviceListUri;
        }
    }

    private static IEnumerable<Uri> ExpandConfiguredDeviceListUris(Uri configuredUri)
    {
        var root = new Uri(configuredUri.GetLeftPart(UriPartial.Authority));
        var deviceListUri = new Uri(root, "/cgi-bin/devices.ha");
        yield return deviceListUri;

        if (Uri.Compare(configuredUri, deviceListUri, UriComponents.AbsoluteUri, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            yield return configuredUri;
        }
    }

    private async Task<HttpFetchResult> FetchAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddAuthHeaders(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return new HttpFetchResult(body, response.Content.Headers.ContentType?.MediaType);
    }

    private async Task TryLoginAsync(HttpClient client, Uri sourceUri, string loginBody, CancellationToken cancellationToken)
    {
        await TryNonceHashLoginAsync(client, sourceUri, loginBody, cancellationToken);

        var loginUris = BuildLoginUris(sourceUri);
        foreach (var loginUri in loginUris)
        {
            using var formRequest = new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
                Content = new FormUrlEncodedContent(BuildLoginFields())
            };
            AddAuthHeaders(formRequest);
            await SendIgnoringFailureAsync(client, formRequest, cancellationToken);

            using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
                Content = new StringContent(JsonSerializer.Serialize(BuildLoginObject()), Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(jsonRequest);
            await SendIgnoringFailureAsync(client, jsonRequest, cancellationToken);
        }
    }

    private async Task TryNonceHashLoginAsync(HttpClient client, Uri sourceUri, string loginBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.Password))
        {
            return;
        }

        var nonce = ExtractInputValue(loginBody, "nonce");
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return;
        }

        var action = ExtractFormAction(loginBody);
        var loginUri = !string.IsNullOrWhiteSpace(action)
            ? new Uri(sourceUri, action)
            : new Uri(new Uri(sourceUri.GetLeftPart(UriPartial.Authority)), "/cgi-bin/login.ha");
        var formUsername = ExtractInputValue(loginBody, "username");
        var usernames = new[] { _settings.Username, formUsername, "login" }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var username in usernames)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
                Content = new FormUrlEncodedContent(BuildNonceHashLoginFields(username!, nonce, _settings.Password))
            };
            AddAuthHeaders(request);
            await SendIgnoringFailureAsync(client, request, cancellationToken);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildNonceHashLoginFields(string username, string nonce, string password)
    {
        yield return new KeyValuePair<string, string>("nonce", nonce);
        yield return new KeyValuePair<string, string>("username", username);
        yield return new KeyValuePair<string, string>("password", new string('*', Math.Max(1, password.Length)));
        yield return new KeyValuePair<string, string>("hashpassword", Md5Hex(password + nonce));
        yield return new KeyValuePair<string, string>("Continue", "Login");
    }

    private static string Md5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? ExtractFormAction(string html)
    {
        var match = Regex.Match(html, @"(?is)<form\b(?<attrs>[^>]*)>");
        return match.Success ? ExtractHtmlAttribute(match.Groups["attrs"].Value, "action") : null;
    }

    private static string? ExtractInputValue(string html, string name)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<input\b(?<attrs>[^>]*)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            var inputName = ExtractHtmlAttribute(attrs, "name");
            if (string.Equals(inputName, name, StringComparison.OrdinalIgnoreCase))
            {
                return ExtractHtmlAttribute(attrs, "value");
            }
        }

        return null;
    }

    private static string? ExtractHtmlAttribute(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $@"(?is)\b{Regex.Escape(name)}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))");
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private async Task SendIgnoringFailureAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            _ = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // Login route probing is best-effort because consumer routers expose many incompatible forms.
        }
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        if (_settings.HasCredentials)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        if (!string.IsNullOrWhiteSpace(_settings.DeviceCode))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Code", _settings.DeviceCode);
            request.Headers.TryAddWithoutValidation("X-DeviceCode", _settings.DeviceCode);
        }
    }

    private IEnumerable<KeyValuePair<string, string>> BuildLoginFields()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Username))
        {
            yield return new KeyValuePair<string, string>("username", _settings.Username);
            yield return new KeyValuePair<string, string>("user", _settings.Username);
        }

        if (!string.IsNullOrWhiteSpace(_settings.Password))
        {
            yield return new KeyValuePair<string, string>("password", _settings.Password);
            yield return new KeyValuePair<string, string>("pass", _settings.Password);
        }

        if (!string.IsNullOrWhiteSpace(_settings.DeviceCode))
        {
            yield return new KeyValuePair<string, string>("devicecode", _settings.DeviceCode);
            yield return new KeyValuePair<string, string>("deviceCode", _settings.DeviceCode);
            yield return new KeyValuePair<string, string>("code", _settings.DeviceCode);
        }
    }

    private Dictionary<string, string?> BuildLoginObject() => new(StringComparer.Ordinal)
    {
        ["username"] = _settings.Username,
        ["user"] = _settings.Username,
        ["password"] = _settings.Password,
        ["pass"] = _settings.Password,
        ["devicecode"] = _settings.DeviceCode,
        ["deviceCode"] = _settings.DeviceCode,
        ["code"] = _settings.DeviceCode
    };

    private static IEnumerable<Uri> BuildLoginUris(Uri sourceUri)
    {
        if (!sourceUri.IsAbsoluteUri)
        {
            return NoUris;
        }

        var root = new Uri(sourceUri.GetLeftPart(UriPartial.Authority));
        return new[]
        {
            sourceUri,
            root,
            new Uri(root, "/login"),
            new Uri(root, "/api/login"),
            new Uri(root, "/api/auth/login"),
            new Uri(root, "/authenticate")
        }.Distinct();
    }

    private static bool LooksLikeLogin(string body) =>
        body.Contains("password", StringComparison.OrdinalIgnoreCase)
        || body.Contains("username", StringComparison.OrdinalIgnoreCase)
        || body.Contains("login", StringComparison.OrdinalIgnoreCase);

    private static string InnermostMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}

public sealed class WindowsNetworkDeviceSource : IDeviceSource
{
    private readonly AppSettings _settings;

    public WindowsNetworkDeviceSource(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<DevicePollResult> PollAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DevicePollResult.Failure("Windows ARP fallback is only available on Windows.", "Windows ARP/ping", "local");
        }

        var network = LocalNetworkInfo.FindPrimaryNetwork();
        if (network is null)
        {
            return DevicePollResult.Failure("No active IPv4 network with a gateway was found.", "Windows ARP/ping", "local");
        }

        if (!network.Contains(LocalNetworkInfo.RouterAddress))
        {
            return DevicePollResult.Failure(
                $"Windows ARP fallback is disabled because this machine is not on the router local subnet ({LocalNetworkInfo.RouterAddress}).",
                "Windows ARP/ping",
                "local");
        }

        await PingSubnetAsync(network, cancellationToken);
        var arpOutput = await RunArpAsync(cancellationToken);
        var entries = ArpParser.Parse(arpOutput).ToList();
        var devices = await Task.WhenAll(entries.Select(async entry =>
        {
            var hostName = await ResolveHostNameAsync(entry.IpAddress, cancellationToken);
            return new ObservedDevice(entry.Mac, entry.IpAddress, true, hostName, "local", "arp");
        }));

        return DevicePollResult.Ok("Windows ARP/ping", "local", true, devices, null);
    }

    private async Task PingSubnetAsync(LocalNetworkInfo network, CancellationToken cancellationToken)
    {
        var hosts = network.EnumerateHosts(_settings.MaxPingHosts).ToList();
        var throttler = new SemaphoreSlim(64);
        var tasks = hosts.Select(async host =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                using var ping = new Ping();
                await ping.SendPingAsync(host, _settings.PingTimeoutMs);
            }
            catch
            {
                // Many devices ignore ICMP; the ping sweep is only to warm the ARP cache.
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static async Task<string> RunArpAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        return output + Environment.NewLine + error;
    }

    private static async Task<string?> ResolveHostNameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        try
        {
            var lookupTask = Dns.GetHostEntryAsync(address.ToString());
            var completed = await Task.WhenAny(lookupTask, Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken));
            if (completed != lookupTask)
            {
                return null;
            }

            var entry = await lookupTask;
            return CleanResolvedHostName(entry.HostName);
        }
        catch
        {
            return null;
        }
    }

    private static string? CleanResolvedHostName(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var cleaned = hostName.Trim().TrimEnd('.');
        if (IPAddress.TryParse(cleaned, out _))
        {
            return null;
        }

        var firstLabel = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLabel) ? null : firstLabel;
    }
}

public sealed class DeviceHistoryStore
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;
    private readonly string _devicesPath;
    private readonly string _groupsPath;
    private readonly string _historyPath;
    private readonly string _eventsPath;
    private readonly HashSet<string> _groups = new(StringComparer.OrdinalIgnoreCase);

    public DeviceHistoryStore()
    {
        _dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(_dataDirectory);
        _devicesPath = Path.Combine(_dataDirectory, "devices.json");
        _groupsPath = Path.Combine(_dataDirectory, "groups.json");
        _historyPath = Path.Combine(_dataDirectory, "history.jsonl");
        _eventsPath = Path.Combine(_dataDirectory, "events.jsonl");
        LoadDevices();
        LoadGroups();
    }

    public async Task ApplyPollAsync(DevicePollResult result, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var observed = result.Devices
            .Select(device => device with { Mac = MacAddress.Normalize(device.Mac) ?? device.Mac })
            .Where(device => MacAddress.IsNormalized(device.Mac))
            .GroupBy(device => device.Mac, StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeObserved(group, result.SourceKind))
            .ToDictionary(device => device.Mac, StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var samples = new List<DeviceSample>();
            var events = new List<DeviceEvent>();

            foreach (var device in observed.Values)
            {
                var record = GetOrCreateRecord(device.Mac, now, result.SourceKind);
                var wasOnline = record.Online;
                record.Source = result.SourceName;
                record.SourceKind = result.SourceKind;
                record.RawStatus = device.RawStatus;
                record.Online = device.Online;
                record.LastIpAddress = device.IpAddress ?? record.LastIpAddress;
                record.HostName = FirstNonBlank(device.HostName, record.HostName);
                record.NetworkName = FirstNonBlank(device.NetworkName, record.NetworkName);
                record.NetworkBand = FirstNonBlank(device.NetworkBand, record.NetworkBand);
                record.ConnectionType = FirstNonBlank(device.ConnectionType, record.ConnectionType);
                record.LastSampleUtc = now;
                record.SampleCount++;
                if (device.Online)
                {
                    record.LastSeenUtc = now;
                    record.OnlineSampleCount++;
                }

                if (record.LastChangedUtc is null || wasOnline != device.Online)
                {
                    record.LastChangedUtc = now;
                    events.Add(new DeviceEvent(now, record.Mac, device.Online, record.LastIpAddress, result.SourceName, record.HostName));
                }

                samples.Add(ToSample(record, result.SourceName, now));
            }

            if (result.CanMarkMissingOffline)
            {
                foreach (var record in _devices.Values.Where(device =>
                    !observed.ContainsKey(device.Mac)
                    && string.Equals(device.SourceKind, result.SourceKind, StringComparison.OrdinalIgnoreCase)))
                {
                    var wasOnline = record.Online;
                    record.Online = false;
                    record.Source = result.SourceName;
                    record.LastSampleUtc = now;
                    record.SampleCount++;
                    if (wasOnline)
                    {
                        record.LastChangedUtc = now;
                        events.Add(new DeviceEvent(now, record.Mac, false, record.LastIpAddress, result.SourceName, record.HostName));
                    }

                    samples.Add(ToSample(record, result.SourceName, now));
                }
            }

            await AppendJsonLinesAsync(_historyPath, samples, cancellationToken);
            await AppendJsonLinesAsync(_eventsPath, events, cancellationToken);
            await SaveDevicesAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DashboardResponse> GetDashboardAsync(AppSettings settings, PollStatus status, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var devices = _devices.Values
                .OrderByDescending(device => device.Online)
                .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(device => DeviceDto.FromRecord(device, settings.PollInterval))
                .ToList();
            var visibleDevices = devices.Where(device => !device.Ignored).ToList();
            var groups = _groups
                .Concat(_devices.Values.SelectMany(device => device.Groups))
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var events = ReadEvents(DateTimeOffset.UtcNow.AddDays(-14), null, 200)
                .OrderByDescending(entry => entry.AtUtc)
                .Select(entry => EventDto.FromEvent(entry, _devices))
                .ToList();

            return new DashboardResponse(
                DateTimeOffset.UtcNow,
                devices,
                visibleDevices.Count(device => device.Online),
                visibleDevices.Count,
                groups,
                status,
                settings.ToSafeDto(),
                events);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HistoryResponse> GetHistoryAsync(TimeSpan range, HashSet<string>? macs, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow.Subtract(range);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var samples = ReadSamples(start, macs, 50000)
                .OrderBy(sample => sample.SampledAtUtc)
                .ToList();
            var events = ReadEvents(start, macs, 1000)
                .OrderBy(entry => entry.AtUtc)
                .Select(entry => EventDto.FromEvent(entry, _devices))
                .ToList();
            var bounds = GetSampleBounds(macs);

            return new HistoryResponse(start, DateTimeOffset.UtcNow, samples, events, bounds);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DeviceDto?> SetDeviceNameAsync(string mac, string? name, CancellationToken cancellationToken)
    {
        var normalized = MacAddress.Normalize(mac);
        if (normalized is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_devices.TryGetValue(normalized, out var record))
            {
                return null;
            }

            record.Name = NormalizeName(name);
            await SaveDevicesAsync(cancellationToken);
            return DeviceDto.FromRecord(record, TimeSpan.FromMinutes(5));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DeviceDto?> SetDeviceGroupsAsync(string mac, IEnumerable<string>? groups, CancellationToken cancellationToken)
    {
        var normalized = MacAddress.Normalize(mac);
        if (normalized is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_devices.TryGetValue(normalized, out var record))
            {
                return null;
            }

            record.Groups = NormalizeGroups(groups).ToList();
            foreach (var group in record.Groups)
            {
                _groups.Add(group);
            }

            await SaveDevicesAsync(cancellationToken);
            await SaveGroupsAsync(cancellationToken);
            return DeviceDto.FromRecord(record, TimeSpan.FromMinutes(5));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DeviceDto?> SetDeviceIgnoredAsync(string mac, bool ignored, CancellationToken cancellationToken)
    {
        var normalized = MacAddress.Normalize(mac);
        if (normalized is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_devices.TryGetValue(normalized, out var record))
            {
                return null;
            }

            record.Ignored = ignored;
            await SaveDevicesAsync(cancellationToken);
            return DeviceDto.FromRecord(record, TimeSpan.FromMinutes(5));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> CreateGroupAsync(string? name, CancellationToken cancellationToken)
    {
        var group = NormalizeGroup(name);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (group is not null)
            {
                _groups.Add(group);
                await SaveGroupsAsync(cancellationToken);
            }

            return GetAllGroups();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> DeleteGroupAsync(string name, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _groups.RemoveWhere(group => string.Equals(group, name, StringComparison.OrdinalIgnoreCase));
            foreach (var device in _devices.Values)
            {
                device.Groups = device.Groups
                    .Where(group => !string.Equals(group, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            await SaveDevicesAsync(cancellationToken);
            await SaveGroupsAsync(cancellationToken);
            return GetAllGroups();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> AssignGroupAsync(string? name, IEnumerable<string>? macs, CancellationToken cancellationToken)
    {
        var group = NormalizeGroup(name);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (group is not null)
            {
                _groups.Add(group);
                foreach (var mac in macs ?? Enumerable.Empty<string>())
                {
                    var normalized = MacAddress.Normalize(mac);
                    if (normalized is null || !_devices.TryGetValue(normalized, out var record))
                    {
                        continue;
                    }

                    var deviceGroups = NormalizeGroups(record.Groups).ToList();
                    if (!deviceGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                    {
                        deviceGroups.Add(group);
                    }

                    record.Groups = deviceGroups.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                }

                await SaveDevicesAsync(cancellationToken);
                await SaveGroupsAsync(cancellationToken);
            }

            return GetAllGroups();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadDevices()
    {
        if (!File.Exists(_devicesPath))
        {
            return;
        }

        try
        {
            var devices = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_devicesPath), PrettyJson) ?? new();
            foreach (var device in devices)
            {
                var normalized = MacAddress.Normalize(device.Mac);
                if (normalized is null)
                {
                    continue;
                }

                device.Mac = normalized;
                _devices[normalized] = device;
            }
        }
        catch
        {
            // A corrupt state file should not prevent new samples from being collected.
        }
    }

    private void LoadGroups()
    {
        if (File.Exists(_groupsPath))
        {
            try
            {
                var groups = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_groupsPath), PrettyJson) ?? new();
                foreach (var group in NormalizeGroups(groups))
                {
                    _groups.Add(group);
                }
            }
            catch
            {
                // Group metadata is user-editable; ignore corrupt group metadata and keep collecting samples.
            }
        }

        foreach (var group in _devices.Values.SelectMany(device => NormalizeGroups(device.Groups)))
        {
            _groups.Add(group);
        }
    }

    private DeviceRecord GetOrCreateRecord(string mac, DateTimeOffset now, string sourceKind)
    {
        if (_devices.TryGetValue(mac, out var record))
        {
            return record;
        }

        record = new DeviceRecord
        {
            Mac = mac,
            FirstSeenUtc = now,
            LastChangedUtc = now,
            SourceKind = sourceKind
        };
        _devices[mac] = record;
        return record;
    }

    private async Task SaveDevicesAsync(CancellationToken cancellationToken)
    {
        var ordered = _devices.Values
            .OrderBy(device => device.Mac, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(ordered, PrettyJson);
        var tempPath = _devicesPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _devicesPath, true);
    }

    private async Task SaveGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = GetAllGroups();
        var json = JsonSerializer.Serialize(groups, PrettyJson);
        var tempPath = _groupsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _groupsPath, true);
    }

    private static async Task AppendJsonLinesAsync<T>(string path, IReadOnlyList<T> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.AppendLine(JsonSerializer.Serialize(value, LineJson));
        }

        await File.AppendAllTextAsync(path, builder.ToString(), cancellationToken);
    }

    private IEnumerable<DeviceSample> ReadSamples(DateTimeOffset start, HashSet<string>? macs, int maxRows)
    {
        if (!File.Exists(_historyPath))
        {
            return Enumerable.Empty<DeviceSample>();
        }

        return ReadJsonLines<DeviceSample>(_historyPath)
            .Where(sample => sample.SampledAtUtc >= start)
            .Where(sample => macs is null || macs.Contains(sample.Mac))
            .TakeLast(maxRows)
            .ToList();
    }

    private HistoryBounds GetSampleBounds(HashSet<string>? macs)
    {
        if (!File.Exists(_historyPath))
        {
            return new HistoryBounds(null, null);
        }

        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (var sample in ReadJsonLines<DeviceSample>(_historyPath))
        {
            if (macs is not null && !macs.Contains(sample.Mac))
            {
                continue;
            }

            if (first is null || sample.SampledAtUtc < first)
            {
                first = sample.SampledAtUtc;
            }

            if (last is null || sample.SampledAtUtc > last)
            {
                last = sample.SampledAtUtc;
            }
        }

        return new HistoryBounds(first, last);
    }

    private IEnumerable<DeviceEvent> ReadEvents(DateTimeOffset start, HashSet<string>? macs, int maxRows)
    {
        if (!File.Exists(_eventsPath))
        {
            return Enumerable.Empty<DeviceEvent>();
        }

        return ReadJsonLines<DeviceEvent>(_eventsPath)
            .Where(entry => entry.AtUtc >= start)
            .Where(entry => macs is null || macs.Contains(entry.Mac))
            .TakeLast(maxRows)
            .ToList();
    }

    private static IEnumerable<T> ReadJsonLines<T>(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(line, LineJson);
            }
            catch
            {
                continue;
            }

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static ObservedDevice MergeObserved(IEnumerable<ObservedDevice> devices, string sourceKind)
    {
        var list = devices.ToList();
        var online = list.Any(device => device.Online);
        var first = list.First();
        return new ObservedDevice(
            first.Mac,
            list.Select(device => device.IpAddress).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            online,
            list.Select(device => device.HostName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            sourceKind,
            list.Select(device => device.RawStatus).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            list.Select(device => device.NetworkName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            list.Select(device => device.NetworkBand).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            list.Select(device => device.ConnectionType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static DeviceSample ToSample(DeviceRecord record, string source, DateTimeOffset now) =>
        new(now, record.Mac, record.LastIpAddress, record.Online, source, record.HostName, record.NetworkName, record.NetworkBand, record.ConnectionType);

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }

    private IReadOnlyList<string> GetAllGroups() =>
        _groups
            .Concat(_devices.Values.SelectMany(device => NormalizeGroups(device.Groups)))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> NormalizeGroups(IEnumerable<string>? groups) =>
        (groups ?? Enumerable.Empty<string>())
            .Select(NormalizeGroup)
            .Where(group => group is not null)
            .Select(group => group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        var trimmed = Regex.Replace(group.Trim(), @"\s+", " ");
        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }

    private static string? FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;
}

public sealed class UiPreferencesStore
{
    private const int DefaultRangeHours = 168;
    private const int MaximumRangeHours = 2160;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private UiPreferences _preferences;

    public UiPreferencesStore()
    {
        var dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "ui-preferences.json");
        _preferences = Load();
    }

    public UiPreferences Get() => _preferences;

    public async Task<UiPreferences> SaveAsync(UiPreferencesRequest request, CancellationToken cancellationToken)
    {
        var preferences = Normalize(request);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _path, true);
            _preferences = preferences;
            return preferences;
        }
        finally
        {
            _lock.Release();
        }
    }

    private UiPreferences Load()
    {
        if (!File.Exists(_path))
        {
            return UiPreferences.Unconfigured;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(_path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return loaded is null ? UiPreferences.Unconfigured : Normalize(loaded);
        }
        catch (JsonException)
        {
            return UiPreferences.Unconfigured;
        }
    }

    private static UiPreferences Normalize(UiPreferencesRequest request) =>
        Normalize(new UiPreferences(
            true,
            request.GroupFilters,
            request.ExpandedGroups,
            request.HiddenTimelineChildren,
            request.ShowIgnored,
            request.Selected,
            request.RangeHours,
            request.TimelineOffsetHours));

    private static UiPreferences Normalize(UiPreferences preferences) => new(
        preferences.IsConfigured,
        NormalizeStrings(preferences.GroupFilters),
        NormalizeStrings(preferences.ExpandedGroups),
        NormalizeStrings(preferences.HiddenTimelineChildren),
        preferences.ShowIgnored,
        NormalizeMacs(preferences.Selected),
        Math.Clamp(preferences.RangeHours, 6, MaximumRangeHours),
        Math.Clamp(preferences.TimelineOffsetHours, 0, MaximumRangeHours));

    private static IReadOnlyList<string> NormalizeStrings(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

    private static IReadOnlyList<string> NormalizeMacs(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(MacAddress.Normalize)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
}

public static class DeviceExtractor
{
    private static readonly Regex MacRegex = new(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.Compiled);
    private static readonly Regex IpRegex = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled);
    private static readonly Regex TableRowRegex = new(@"(?is)<tr\b[^>]*>.*?</tr>", RegexOptions.Compiled);
    private static readonly Regex TableCellRegex = new(@"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.Compiled);
    private static readonly Regex KeyValueTableRowRegex = new(
        @"(?is)<tr\b[^>]*>\s*<th\b[^>]*>(?<key>.*?)</th>\s*<td\b[^>]*>(?<value>.*?)</td>\s*</tr>",
        RegexOptions.Compiled);
    private static readonly Regex KeyedNameRegex = new(
        @"(?is)\b(?:device\s*name|host\s*name|hostname|client\s*name|name|alias|description)\b\s*[:=]\s*(?:""(?<name>[^""]{1,80})""|'(?<name>[^']{1,80})'|(?<name>[^<\r\n,;|]{1,80}))",
        RegexOptions.Compiled);
    private static readonly Regex KeyedNetworkRegex = new(
        @"(?is)\b(?:ssid|network\s*name|network|wifi\s*network|wlan|interface)\b\s*[:=]\s*(?:""(?<network>[^""]{1,80})""|'(?<network>[^']{1,80})'|(?<network>[^<\r\n,;|]{1,80}))",
        RegexOptions.Compiled);
    private static readonly Regex BandRegex = new(
        @"(?i)\b(?:(?<band>2(?:\.4)?|5|6)\s*(?:g|ghz)|(?<freq>24\d{2}|5\d{3}|6\d{3})\s*(?:mhz)?)\b",
        RegexOptions.Compiled);
    private static readonly string[] MacKeys = { "mac", "macAddress", "mac_address", "hwaddr", "hardwareAddress", "clientMac", "bssid" };
    private static readonly string[] IpKeys = { "ip", "ipAddress", "ip_address", "ipv4", "address", "clientIp", "hostIp" };
    private static readonly string[] NameKeys = { "name", "hostName", "hostname", "deviceName", "device_name", "clientName", "client_name", "alias", "description" };
    private static readonly string[] NetworkKeys = { "ssid", "network", "networkName", "network_name", "wifiNetwork", "wifi_network", "wlan", "interface" };
    private static readonly string[] BandKeys = { "band", "radio", "radioBand", "radio_band", "frequency", "freq", "channelBand", "wifiBand", "wifi_band" };
    private static readonly string[] OnlineKeys = { "online", "connected", "active", "isOnline", "isConnected", "status", "state", "connectionState" };

    public static IEnumerable<ObservedDevice> Extract(string body, string? contentType, string source)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Enumerable.Empty<ObservedDevice>();
        }

        if (LooksLikeJson(contentType, body) && TryExtractJson(body, source, out var jsonDevices))
        {
            return jsonDevices;
        }

        return ExtractHtmlLike(body, source);
    }

    private static bool LooksLikeJson(string? contentType, string body)
    {
        var trimmed = body.TrimStart();
        return contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            || trimmed.StartsWith('{')
            || trimmed.StartsWith('[');
    }

    private static bool TryExtractJson(string body, string source, out List<ObservedDevice> devices)
    {
        devices = new List<ObservedDevice>();
        try
        {
            var node = JsonNode.Parse(body);
            VisitJson(node, devices, source);
            devices = devices
                .GroupBy(device => device.Mac, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void VisitJson(JsonNode? node, List<ObservedDevice> devices, string source)
    {
        switch (node)
        {
            case JsonObject obj:
                var mac = FindString(obj, MacKeys, MacRegex);
                if (mac is not null)
                {
                    var normalizedMac = MacAddress.Normalize(mac);
                    if (normalizedMac is not null)
                    {
                        var ipValue = FindString(obj, IpKeys, null);
                        var ip = MatchValue(ipValue, IpRegex);
                        var online = FindOnline(obj) ?? true;
                        var name = CleanDeviceName(FindString(obj, NameKeys, null))
                            ?? ExtractNameFromIpLikeValue(ipValue);
                        var networkName = CleanNetworkName(FindString(obj, NetworkKeys, null));
                        var networkBand = ExtractNetworkBand(FindString(obj, BandKeys, null))
                            ?? ExtractNetworkBand(ipValue)
                            ?? ExtractNetworkBand(networkName);
                        var rawStatus = FindString(obj, OnlineKeys, null);
                        devices.Add(new ObservedDevice(normalizedMac, ip, online, name, source, rawStatus, networkName, networkBand));
                    }
                }

                foreach (var child in obj)
                {
                    VisitJson(child.Value, devices, source);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    VisitJson(child, devices, source);
                }

                break;
        }
    }

    private static string? FindString(JsonObject obj, IReadOnlyCollection<string> keys, Regex? pattern)
    {
        foreach (var item in obj)
        {
            if (!keys.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = item.Value?.GetValueKind() == JsonValueKind.String
                ? item.Value.GetValue<string>()
                : item.Value?.ToJsonString();
            var matched = MatchValue(value, pattern);
            if (matched is not null)
            {
                return matched;
            }
        }

        if (pattern is null)
        {
            return null;
        }

        foreach (var item in obj)
        {
            if (item.Value?.GetValueKind() != JsonValueKind.String)
            {
                continue;
            }

            var matched = MatchValue(item.Value.GetValue<string>(), pattern);
            if (matched is not null)
            {
                return matched;
            }
        }

        return null;
    }

    private static string? MatchValue(string? value, Regex? pattern)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (pattern is null)
        {
            return value.Trim();
        }

        var match = pattern.Match(value);
        return match.Success ? match.Value : null;
    }

    private static bool? FindOnline(JsonObject obj)
    {
        foreach (var item in obj)
        {
            if (!OnlineKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase) || item.Value is null)
            {
                continue;
            }

            if (item.Value.GetValueKind() is JsonValueKind.True)
            {
                return true;
            }

            if (item.Value.GetValueKind() is JsonValueKind.False)
            {
                return false;
            }

            var text = item.Value.GetValueKind() == JsonValueKind.String
                ? item.Value.GetValue<string>()
                : item.Value.ToJsonString();

            var parsed = ParseOnlineText(text);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<ObservedDevice> ExtractHtmlLike(string body, string source)
    {
        var devices = new List<ObservedDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in ExtractKeyValueTableBlocks(body, source))
        {
            if (seen.Add(device.Mac))
            {
                devices.Add(device);
            }
        }

        foreach (var device in ExtractLabelBlocks(body, source))
        {
            if (seen.Add(device.Mac))
            {
                devices.Add(device);
            }
        }

        foreach (var device in ExtractTableRows(body, source))
        {
            if (seen.Add(device.Mac))
            {
                devices.Add(device);
            }
        }

        foreach (Match match in MacRegex.Matches(body))
        {
            var mac = MacAddress.Normalize(match.Value);
            if (mac is null || !seen.Add(mac))
            {
                continue;
            }

            var start = Math.Max(0, match.Index - 600);
            var length = Math.Min(body.Length - start, 1200);
            var window = body.Substring(start, length);
            var text = WebUtility.HtmlDecode(Regex.Replace(window, "<[^>]+>", " "));
            var ip = IpRegex.Matches(text)
                .Select(ipMatch => ipMatch.Value)
                .FirstOrDefault(IsUsableIp);
            var online = ParseOnlineText(text) ?? true;
            var name = ExtractNameFromText(text);
            var networkName = ExtractNetworkNameFromText(text);
            var networkBand = ExtractNetworkBand(text);

            devices.Add(new ObservedDevice(mac, ip, online, name, source, null, networkName, networkBand));
        }

        return devices;
    }

    private static IEnumerable<ObservedDevice> ExtractKeyValueTableBlocks(string body, string source)
    {
        var current = new List<KeyValuePair<string, string>>();

        foreach (Match match in KeyValueTableRowRegex.Matches(body))
        {
            var key = HtmlToText(match.Groups["key"].Value);
            var value = HtmlToText(match.Groups["value"].Value);
            if (key.StartsWith("MAC Address", StringComparison.OrdinalIgnoreCase) && current.Count > 0)
            {
                var parsed = ParseKeyValueBlock(current, source);
                if (parsed is not null)
                {
                    yield return parsed;
                }

                current.Clear();
            }

            if (current.Count > 0 || key.StartsWith("MAC Address", StringComparison.OrdinalIgnoreCase))
            {
                current.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        if (current.Count > 0)
        {
            var parsed = ParseKeyValueBlock(current, source);
            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static ObservedDevice? ParseKeyValueBlock(IReadOnlyList<KeyValuePair<string, string>> values, string source)
    {
        string? Get(string label) => values
            .Where(item => item.Key.Equals(label, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var mac = MacAddress.Normalize(Get("MAC Address"));
        if (mac is null)
        {
            return null;
        }

        var ipName = Get("IPv4 Address / Name") ?? Get("IPv4 Address") ?? Get("IP Address / Name");
        var ip = MatchValue(ipName, IpRegex);
        var name = ExtractDeviceNameFromIpName(ipName)
            ?? CleanDeviceName(Get("Device Name"))
            ?? CleanDeviceName(Get("Host Name"));
        var status = Get("Status");
        var online = ParseRouterStatus(status) ?? ParseOnlineText(status) ?? true;
        var connectionType = CleanConnectionType(Get("Connection Type"));
        var networkBand = values.Select(item => ExtractNetworkBand($"{item.Key} {item.Value}")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var networkName = CleanNetworkName(GetLast(values, "Name"))
            ?? CleanNetworkName(Get("SSID"))
            ?? CleanNetworkName(Get("Network Name"));

        return new ObservedDevice(mac, ip, online, name, source, status, networkName, networkBand, connectionType);
    }

    private static string? GetLast(IReadOnlyList<KeyValuePair<string, string>> values, string label) =>
        values
            .Where(item => item.Key.Equals(label, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IEnumerable<ObservedDevice> ExtractLabelBlocks(string body, string source)
    {
        var lines = ToLogicalLines(body);
        var block = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("MAC Address", StringComparison.OrdinalIgnoreCase) && block.Count > 0)
            {
                var parsed = ParseLabelBlock(block, source);
                if (parsed is not null)
                {
                    yield return parsed;
                }

                block.Clear();
            }

            if (block.Count > 0 || line.StartsWith("MAC Address", StringComparison.OrdinalIgnoreCase))
            {
                block.Add(line);
            }
        }

        if (block.Count > 0)
        {
            var parsed = ParseLabelBlock(block, source);
            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static ObservedDevice? ParseLabelBlock(IReadOnlyList<string> lines, string source)
    {
        var mac = MacAddress.Normalize(GetLabelValue(lines, "MAC Address"));
        if (mac is null)
        {
            return null;
        }

        var ipName = GetLabelValue(lines, "IPv4 Address / Name")
            ?? GetLabelValue(lines, "IPv4 Address")
            ?? GetLabelValue(lines, "IP Address / Name");
        var ip = MatchValue(ipName, IpRegex);
        var name = ExtractDeviceNameFromIpName(ipName)
            ?? CleanDeviceName(GetLabelValue(lines, "Device Name"))
            ?? CleanDeviceName(GetLabelValue(lines, "Host Name"));
        var status = GetLabelValue(lines, "Status");
        var online = ParseRouterStatus(status) ?? ParseOnlineText(status) ?? true;
        var connectionType = CleanConnectionType(GetLabelValue(lines, "Connection Type"));
        var networkBand = lines.Select(ExtractNetworkBand).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var networkName = GetLabeledValueAfter(lines, "Name:", startAfterLabel: "Type:")
            ?? CleanNetworkName(GetLabelValue(lines, "SSID"))
            ?? CleanNetworkName(GetLabelValue(lines, "Network Name"));

        return new ObservedDevice(mac, ip, online, name, source, status, networkName, networkBand, connectionType);
    }

    private static IReadOnlyList<string> ToLogicalLines(string body)
    {
        var withBreaks = Regex.Replace(body, @"(?i)<\s*br\s*/?\s*>|</\s*(?:p|div|li|tr|section|article|h\d)\s*>", "\n");
        var text = WebUtility.HtmlDecode(Regex.Replace(withBreaks, "<[^>]+>", "\n"));
        return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => NormalizeWhitespace(line))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string? GetLabelValue(IReadOnlyList<string> lines, string label)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i][label.Length..].Trim(' ', '\t', ':', '-', '|');
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (i + 1 < lines.Count)
            {
                return lines[i + 1];
            }
        }

        return null;
    }

    private static string? GetLabeledValueAfter(IReadOnlyList<string> lines, string label, string startAfterLabel)
    {
        var start = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(startAfterLabel, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }

        for (var i = start; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i][label.Length..].Trim(' ', '\t', ':', '-', '|');
            return CleanNetworkName(value);
        }

        return null;
    }

    private static IEnumerable<ObservedDevice> ExtractTableRows(string body, string source)
    {
        foreach (Match rowMatch in TableRowRegex.Matches(body))
        {
            var rowHtml = rowMatch.Value;
            var mac = MacAddress.Normalize(MacRegex.Match(rowHtml).Value);
            if (mac is null)
            {
                continue;
            }

            var cells = TableCellRegex.Matches(rowHtml)
                .Select(match => HtmlToText(match.Groups[1].Value))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            var rowText = cells.Count > 0 ? string.Join(" ", cells) : HtmlToText(rowHtml);
            var ip = cells.SelectMany(cell => IpRegex.Matches(cell).Select(ipMatch => ipMatch.Value))
                .Concat(IpRegex.Matches(rowText).Select(ipMatch => ipMatch.Value))
                .FirstOrDefault(IsUsableIp);
            var online = ParseOnlineText(rowText) ?? true;
            var name = ExtractNameFromCells(cells, rowText);
            var networkName = ExtractNetworkNameFromCells(cells, rowText);
            var networkBand = ExtractNetworkBand(rowText);

            yield return new ObservedDevice(mac, ip, online, name, source, null, networkName, networkBand);
        }
    }

    private static string? ExtractNameFromCells(IReadOnlyList<string> cells, string rowText)
    {
        foreach (var cell in cells)
        {
            var keyed = ExtractNameFromText(cell);
            if (keyed is not null)
            {
                return keyed;
            }
        }

        foreach (var cell in cells)
        {
            var candidate = CleanDeviceName(cell);
            if (candidate is not null
                && !MacRegex.IsMatch(candidate)
                && !IpRegex.IsMatch(candidate)
                && ParseOnlineText(candidate) is null
                && !LooksLikeHeaderOrAction(candidate))
            {
                return candidate;
            }
        }

        return ExtractNameFromText(rowText);
    }

    private static string? ExtractNameFromText(string text)
    {
        var keyed = KeyedNameRegex.Match(text);
        if (keyed.Success)
        {
            return CleanDeviceName(keyed.Groups["name"].Value);
        }

        return null;
    }

    private static string? ExtractNameFromIpLikeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IpRegex.IsMatch(value))
        {
            return null;
        }

        return CleanDeviceName(value);
    }

    private static string? ExtractDeviceNameFromIpName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutIp = IpRegex.Replace(value, " ");
        var slashIndex = withoutIp.IndexOf('/');
        if (slashIndex >= 0)
        {
            withoutIp = withoutIp[(slashIndex + 1)..];
        }

        return CleanDeviceName(withoutIp);
    }

    private static bool? ParseRouterStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => null
        };
    }

    private static string? ExtractNetworkNameFromCells(IReadOnlyList<string> cells, string rowText)
    {
        foreach (var cell in cells)
        {
            var keyed = ExtractNetworkNameFromText(cell);
            if (keyed is not null)
            {
                return keyed;
            }
        }

        return ExtractNetworkNameFromText(rowText);
    }

    private static string? ExtractNetworkNameFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var keyed = KeyedNetworkRegex.Match(text);
        return keyed.Success ? CleanNetworkName(keyed.Groups["network"].Value) : null;
    }

    private static string? CleanNetworkName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = NormalizeWhitespace(WebUtility.HtmlDecode(value))
            .Trim(' ', ':', '-', '|', ',', ';', '"', '\'');
        cleaned = MacRegex.Replace(cleaned, " ");
        cleaned = IpRegex.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:ssid|network\s*name|network|wifi\s*network|wlan|interface|band|radio|frequency)\b\s*[:=]?", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:online|offline|connected|disconnected|active|inactive|enabled|disabled|true|false)\b", " ");
        cleaned = NormalizeWhitespace(cleaned).Trim(' ', ':', '-', '|', ',', ';', '"', '\'');

        if (cleaned.Length is < 2 or > 80 || LooksLikeHeaderOrAction(cleaned) || BandRegex.IsMatch(cleaned))
        {
            return null;
        }

        return cleaned;
    }

    private static string? CleanConnectionType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = NormalizeWhitespace(WebUtility.HtmlDecode(value))
            .Trim(' ', ':', '-', '|', ',', ';', '"', '\'');
        cleaned = Regex.Replace(cleaned, @"(?i)\bconnection\s*type\b\s*[:=]?", " ");
        cleaned = NormalizeWhitespace(cleaned);
        var ethernet = Regex.Match(cleaned, @"(?i)\bEthernet\s+LAN-\d+\b");
        if (ethernet.Success)
        {
            return NormalizeWhitespace(ethernet.Value);
        }

        var wifi = Regex.Match(cleaned, @"(?i)\bWi-Fi(?:\s+Wi-Fi\s+\d+\s+bars)?\b");
        if (wifi.Success)
        {
            return NormalizeWhitespace(wifi.Value);
        }

        return string.IsNullOrWhiteSpace(cleaned) || LooksLikeHeaderOrAction(cleaned) ? null : cleaned;
    }

    private static string? ExtractNetworkBand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = BandRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["band"].Success)
        {
            var band = match.Groups["band"].Value;
            return band.StartsWith("2", StringComparison.Ordinal) ? "2.4 GHz" : $"{band[0]} GHz";
        }

        if (match.Groups["freq"].Success && int.TryParse(match.Groups["freq"].Value, out var freq))
        {
            if (freq >= 2400 && freq < 2500)
            {
                return "2.4 GHz";
            }

            if (freq >= 4900 && freq < 5900)
            {
                return "5 GHz";
            }

            if (freq >= 5900 && freq < 7200)
            {
                return "6 GHz";
            }
        }

        return null;
    }

    private static string HtmlToText(string html) =>
        NormalizeWhitespace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string? CleanDeviceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = NormalizeWhitespace(WebUtility.HtmlDecode(value))
            .Trim(' ', ':', '-', '|', ',', ';', '"', '\'');
        cleaned = MacRegex.Replace(cleaned, " ");
        cleaned = IpRegex.Replace(cleaned, " ");
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\b(?:ip\s*address|ip|ipv4|mac\s*address|mac|device\s*name|host\s*name|hostname|client\s*name|name|alias|description)\b\s*[:=]?",
            " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:online|offline|connected|disconnected|active|inactive|enabled|disabled|true|false)\b", " ");
        cleaned = NormalizeWhitespace(cleaned).Trim(' ', ':', '-', '|', ',', ';', '"', '\'');

        if (cleaned.Length is < 2 or > 80 || LooksLikeHeaderOrAction(cleaned))
        {
            return null;
        }

        return cleaned;
    }

    private static bool LooksLikeHeaderOrAction(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "mac"
            or "mac address"
            or "ip"
            or "ip address"
            or "status"
            or "state"
            or "interface"
            or "type"
            or "actions"
            or "edit"
            or "delete"
            or "remove"
            or "refresh"
            or "unknown"
            or "n/a"
            or "none";
    }

    private static bool? ParseOnlineText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.ToLowerInvariant();
        if (Regex.IsMatch(normalized, @"\b(offline|disconnected|inactive|down|false|disabled)\b"))
        {
            return false;
        }

        if (Regex.IsMatch(normalized, @"\b(online|connected|active|up|true|enabled)\b"))
        {
            return true;
        }

        return null;
    }

    private static bool IsUsableIp(string ip) =>
        !ip.StartsWith("0.", StringComparison.Ordinal)
        && !ip.StartsWith("127.", StringComparison.Ordinal)
        && ip != "255.255.255.255";
}

public sealed record LocalNetworkInfo(IPAddress Address, IPAddress Mask)
{
    public static IPAddress RouterAddress { get; } = IPAddress.Parse("192.168.1.254");

    public static LocalNetworkInfo? FindPrimaryNetwork()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                continue;
            }

            var address = properties.UnicastAddresses.FirstOrDefault(candidate =>
                candidate.Address.AddressFamily == AddressFamily.InterNetwork
                && candidate.IPv4Mask is not null
                && !IPAddress.IsLoopback(candidate.Address));

            if (address is not null)
            {
                return new LocalNetworkInfo(address.Address, address.IPv4Mask);
            }
        }

        return null;
    }

    public IEnumerable<IPAddress> EnumerateHosts(int maxHosts)
    {
        var address = ToUInt32(Address);
        var mask = ToUInt32(Mask);
        var network = address & mask;
        var broadcast = network | ~mask;
        var hostCount = broadcast > network ? broadcast - network - 1 : 0;

        if (hostCount == 0)
        {
            yield break;
        }

        if (hostCount > maxHosts)
        {
            mask = 0xFFFFFF00;
            network = address & mask;
            broadcast = network | ~mask;
            hostCount = broadcast - network - 1;
        }

        for (var current = network + 1; current < broadcast; current++)
        {
            yield return FromUInt32(current);
        }
    }

    public bool Contains(IPAddress address)
    {
        var ownAddress = ToUInt32(Address);
        var targetAddress = ToUInt32(address);
        var mask = ToUInt32(Mask);
        return (ownAddress & mask) == (targetAddress & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint address) =>
        new(new[]
        {
            (byte)(address >> 24),
            (byte)(address >> 16),
            (byte)(address >> 8),
            (byte)address
        });
}

public static class ArpParser
{
    private static readonly Regex ArpLine = new(
        @"^\s*(?<ip>(?:\d{1,3}\.){3}\d{1,3})\s+(?<mac>[0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+(?<type>\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static IEnumerable<ArpEntry> Parse(string output)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ArpLine.Matches(output))
        {
            var mac = MacAddress.Normalize(match.Groups["mac"].Value);
            if (mac is null || IsIgnoredMac(mac) || !seen.Add(mac))
            {
                continue;
            }

            yield return new ArpEntry(match.Groups["ip"].Value, mac);
        }
    }

    private static bool IsIgnoredMac(string mac) =>
        mac == "00:00:00:00:00:00"
        || mac == "FF:FF:FF:FF:FF:FF"
        || mac.StartsWith("01:00:5E:", StringComparison.OrdinalIgnoreCase)
        || mac.StartsWith("33:33:", StringComparison.OrdinalIgnoreCase);
}

public static class MacAddress
{
    private static readonly Regex MacRegex = new(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.Compiled);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = MacRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return match.Value.Replace('-', ':').ToUpperInvariant();
    }

    public static bool IsNormalized(string value) => MacRegex.IsMatch(value);
}

public sealed class DeviceRecord
{
    public string Mac { get; set; } = "";
    public string? Name { get; set; }
    public List<string> Groups { get; set; } = new();
    public bool Ignored { get; set; }
    public string? HostName { get; set; }
    public string? NetworkName { get; set; }
    public string? NetworkBand { get; set; }
    public string? ConnectionType { get; set; }
    public string? LastIpAddress { get; set; }
    public bool Online { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastChangedUtc { get; set; }
    public DateTimeOffset? LastSampleUtc { get; set; }
    public string? Source { get; set; }
    public string? SourceKind { get; set; }
    public string? RawStatus { get; set; }
    public long SampleCount { get; set; }
    public long OnlineSampleCount { get; set; }
    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : !string.IsNullOrWhiteSpace(HostName)
            ? HostName
            : Mac;
}

public sealed record DeviceDto(
    string Mac,
    string? Name,
    IReadOnlyList<string> Groups,
    bool Ignored,
    string? HostName,
    string? NetworkName,
    string? NetworkBand,
    string? ConnectionType,
    string DisplayName,
    string? LastIpAddress,
    bool Online,
    bool Stale,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? LastChangedUtc,
    DateTimeOffset? LastSampleUtc,
    string? Source,
    string? SourceKind,
    long SampleCount,
    long OnlineSampleCount)
{
    public static DeviceDto FromRecord(DeviceRecord record, TimeSpan pollInterval)
    {
        var staleAfter = TimeSpan.FromTicks(Math.Max(pollInterval.Ticks * 3, TimeSpan.FromMinutes(15).Ticks));
        var stale = record.LastSampleUtc is null || DateTimeOffset.UtcNow - record.LastSampleUtc > staleAfter;
        return new DeviceDto(
            record.Mac,
            record.Name,
            record.Groups,
            record.Ignored,
            record.HostName,
            record.NetworkName,
            record.NetworkBand,
            record.ConnectionType,
            record.DisplayName,
            record.LastIpAddress,
            record.Online,
            stale,
            record.FirstSeenUtc,
            record.LastSeenUtc,
            record.LastChangedUtc,
            record.LastSampleUtc,
            record.Source,
            record.SourceKind,
            record.SampleCount,
            record.OnlineSampleCount);
    }
}

public sealed record DeviceSample(
    DateTimeOffset SampledAtUtc,
    string Mac,
    string? IpAddress,
    bool Online,
    string Source,
    string? HostName,
    string? NetworkName = null,
    string? NetworkBand = null,
    string? ConnectionType = null);

public sealed record DeviceEvent(
    DateTimeOffset AtUtc,
    string Mac,
    bool Online,
    string? IpAddress,
    string Source,
    string? HostName);

public sealed record EventDto(
    DateTimeOffset AtUtc,
    string Mac,
    string DisplayName,
    bool Online,
    string? IpAddress,
    string Source)
{
    public static EventDto FromEvent(DeviceEvent entry, IReadOnlyDictionary<string, DeviceRecord> devices)
    {
        var displayName = devices.TryGetValue(entry.Mac, out var record)
            ? record.DisplayName
            : entry.HostName ?? entry.Mac;
        return new EventDto(entry.AtUtc, entry.Mac, displayName, entry.Online, entry.IpAddress, entry.Source);
    }
}

public sealed record ObservedDevice(
    string Mac,
    string? IpAddress,
    bool Online,
    string? HostName,
    string Source,
    string? RawStatus,
    string? NetworkName = null,
    string? NetworkBand = null,
    string? ConnectionType = null);

public sealed record ArpEntry(string IpAddress, string Mac);

public sealed record DevicePollResult(
    bool Success,
    string SourceName,
    string SourceKind,
    bool CanMarkMissingOffline,
    IReadOnlyList<ObservedDevice> Devices,
    string? Error,
    string? Message)
{
    public static DevicePollResult Ok(
        string sourceName,
        string sourceKind,
        bool canMarkMissingOffline,
        IReadOnlyList<ObservedDevice> devices,
        string? message) =>
        new(true, sourceName, sourceKind, canMarkMissingOffline, devices, null, message);

    public static DevicePollResult Failure(string error, string sourceName, string sourceKind) =>
        new(false, sourceName, sourceKind, false, Array.Empty<ObservedDevice>(), error, null);
}

public sealed record PollStatus(
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    bool IsRunning,
    bool LastSucceeded,
    string? Source,
    string? SourceKind,
    int DeviceCount,
    string? Error,
    string? Message)
{
    public static PollStatus NotStarted { get; } = new(null, null, false, false, null, null, 0, null, null);
}

public sealed record DashboardResponse(
    DateTimeOffset NowUtc,
    IReadOnlyList<DeviceDto> Devices,
    int OnlineCount,
    int KnownCount,
    IReadOnlyList<string> Groups,
    PollStatus Poll,
    object Settings,
    IReadOnlyList<EventDto> RecentEvents);

public sealed record HistoryResponse(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    IReadOnlyList<DeviceSample> Samples,
    IReadOnlyList<EventDto> Events,
    HistoryBounds Bounds);

public sealed record HistoryBounds(
    DateTimeOffset? FirstSampleUtc,
    DateTimeOffset? LastSampleUtc);

public sealed record FinanceAccountConfig(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    string? Username,
    string? Password,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    string Collector,
    string? CollectorNotes);

public sealed record UserFinanceAccountRecord(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    string? Username,
    string? Password,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    string Collector,
    string? CollectorNotes)
{
    public FinanceAccountConfig ToConfig() =>
        new(Id, Name, Kind, Institution, LoginUrl, Username, Password, CashBalance, BalanceOwed, CreditLimit, CreditAvailable, AprPercent, Collector, CollectorNotes);
}

public sealed record FinanceAccountSnapshot(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    string? Username,
    string Collector,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    decimal? UtilizationPercent,
    string Status,
    string? Message,
    string? CollectorNotes);

public sealed record FinanceSnapshot(
    DateTimeOffset SampledAtUtc,
    decimal TotalCash,
    decimal TotalDebt,
    decimal TotalCreditAvailable,
    decimal NetAfterDebt,
    IReadOnlyList<FinanceAccountSnapshot> Accounts,
    string Reason,
    bool Persistable);

public sealed record FinanceRefreshLog(
    DateTimeOffset AtUtc,
    string Status,
    string Message,
    string Reason);

// Income lives in its own versioned ledger so account configuration and balance
// snapshots remain independently migratable. AccountId is the stable join key;
// records may therefore be collected from any configured account in the future.
public sealed record FinanceIncomeLedger(
    int Version,
    IReadOnlyList<FinanceIncomeRecord>? Records)
{
    public static FinanceIncomeLedger Empty { get; } = new(1, Array.Empty<FinanceIncomeRecord>());
}

public sealed record FinanceIncomeRecord(
    string Id,
    string AccountId,
    DateOnly PostedOn,
    decimal Amount,
    string Currency,
    string Kind,
    string? Description,
    string? SourceTransactionId,
    string Fingerprint,
    DateTimeOffset FirstRecordedAtUtc,
    DateTimeOffset LastSeenAtUtc);

public sealed record FinanceIncomeEntry(
    string Id,
    string AccountId,
    string AccountName,
    DateOnly PostedOn,
    decimal Amount,
    string Currency,
    string Kind,
    string? Description);

public sealed record FinanceSalarySummary(
    string AccountId,
    string AccountName,
    string Currency,
    decimal LatestPayment,
    DateOnly LatestPaymentOn,
    decimal TotalLast12Months,
    int PaymentCountLast12Months);

public sealed record FinanceIncomeTracking(
    string AccountId,
    string AccountName,
    bool HasStoredIncome,
    DateOnly LookbackStartOn);

public sealed record FinanceIncomeDashboard(
    int RecordCount,
    IReadOnlyList<FinanceSalarySummary> Salary,
    IReadOnlyList<FinanceIncomeTracking> Tracking,
    IReadOnlyList<FinanceIncomeEntry> SalaryPayments,
    IReadOnlyList<FinanceIncomeEntry> Recent);

public sealed record FinanceRefreshStatus(
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    bool IsRunning,
    bool LastSucceeded,
    int AccountCount,
    string? Error,
    string? Message)
{
    public static FinanceRefreshStatus NotStarted { get; } = new(null, null, false, false, 0, null, null);
}

public sealed record CodexRefreshLaunchResult(
    bool Started,
    bool AlreadyRunning,
    int? ProcessId,
    string? Message,
    string? Error);

public sealed record FinanceDashboardResponse(
    DateTimeOffset NowUtc,
    string Currency,
    string EnvPath,
    int ConfiguredAccountCount,
    string DailyRefreshTime,
    FinanceSnapshot Current,
    IReadOnlyList<FinanceSnapshot> History,
    IReadOnlyList<FinanceRefreshLog> RefreshLog,
    FinanceIncomeDashboard Income,
    FinanceRefreshStatus Refresh);

public sealed record FinanceAccountRequest(
    string? Name,
    string? Kind,
    string? Institution,
    string? LoginUrl,
    string? Username,
    string? Password,
    string? CashBalance,
    string? BalanceOwed,
    string? CreditLimit,
    string? CreditAvailable,
    string? AprPercent,
    string? CollectorNotes);

public sealed record FinanceAccountValuesRequest(
    string? CashBalance,
    string? BalanceOwed,
    string? CreditLimit,
    string? CreditAvailable,
    string? AprPercent);

public sealed record FinanceIncomeRequest(
    string? AccountId,
    DateOnly? PostedOn,
    decimal? Amount,
    string? Currency,
    string? Kind,
    string? Description,
    string? SourceTransactionId,
    string? RecordId);

public sealed record DeviceNameRequest(string? Name);
public sealed record DeviceGroupsRequest(IReadOnlyList<string>? Groups);
public sealed record DeviceIgnoreRequest(bool Ignored);
public sealed record GroupRequest(string? Name);
public sealed record AssignGroupRequest(IReadOnlyList<string>? Macs);
public sealed record UiPreferencesRequest(
    IReadOnlyList<string>? GroupFilters,
    IReadOnlyList<string>? ExpandedGroups,
    IReadOnlyList<string>? HiddenTimelineChildren,
    bool ShowIgnored,
    IReadOnlyList<string>? Selected,
    int RangeHours,
    int TimelineOffsetHours);
public sealed record UiPreferences(
    bool IsConfigured,
    IReadOnlyList<string>? GroupFilters,
    IReadOnlyList<string>? ExpandedGroups,
    IReadOnlyList<string>? HiddenTimelineChildren,
    bool ShowIgnored,
    IReadOnlyList<string>? Selected,
    int RangeHours,
    int TimelineOffsetHours)
{
    public static UiPreferences Unconfigured { get; } = new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), 168, 0);
}

public sealed record HttpFetchResult(string Body, string? ContentType);
