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
using System.Text.Json.Serialization;
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

const string FinanceListenUrl = "http://127.0.0.1:5137";
const string FinanceBrowserUrl = "http://finance.local:5137";
Directory.SetCurrentDirectory(FinanceRuntime.ResolveRuntimeWorkingDirectory(args));
var financeDataRoot = FinanceRuntime.ResolveDataRoot();
var webRoot = ResolveWebRoot();
var financeSettingsSearchRoots = new[]
{
    financeDataRoot,
    Directory.GetCurrentDirectory(),
    AppContext.BaseDirectory
};
var financeEnvPath = FinanceSettings.ResolveEnvPath(financeSettingsSearchRoots);
var financeSettingsDataRoot = Path.GetDirectoryName(financeEnvPath) ?? financeDataRoot;
var financeCredentialStore = new WindowsFinanceCredentialStore(financeSettingsDataRoot);
FinanceCredentialMigration.Migrate(financeSettingsDataRoot, financeEnvPath, financeCredentialStore);
var financeSettings = FinanceSettings.Load(financeSettingsSearchRoots);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot
});
builder.WebHost.UseUrls(FinanceListenUrl);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton(financeSettings);
builder.Services.AddSingleton<IFinanceCredentialStore>(financeCredentialStore);
builder.Services.AddSingleton<FinanceCredentialLeaseStore>();
builder.Services.AddHttpClient("finance-currency", client =>
{
    client.BaseAddress = new Uri("https://open.er-api.com/");
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalFinanceDashboard/1.0");
});
builder.Services.AddSingleton<FinanceCurrencyService>();
builder.Services.AddSingleton<FinanceTaxProfileService>();
builder.Services.AddSingleton<FinanceUiPreferencesService>();
builder.Services.AddSingleton<FinanceStore>();
builder.Services.AddSingleton<FinanceRefreshCoordinator>();
builder.Services.AddSingleton<CodexFinanceRefreshLauncher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<FinanceCurrencyService>());
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

app.MapPost("/api/finance/refresh", async (
    CodexFinanceRefreshLauncher launcher,
    FinanceStore store,
    FinanceRefreshCoordinator refresher,
    CancellationToken cancellationToken) =>
{
    var state = await store.GetStateAsync(refresher.Status, cancellationToken);
    var result = launcher.StartAccounts(state.Current.Accounts);
    return Results.Json(result);
});

app.MapPost("/api/finance/accounts/{id}/refresh", async (
    string id,
    CodexFinanceRefreshLauncher launcher,
    FinanceStore store,
    FinanceRefreshCoordinator refresher,
    CancellationToken cancellationToken) =>
{
    var state = await store.GetStateAsync(refresher.Status, cancellationToken);
    var account = state.Current.Accounts.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
    if (account is null)
    {
        return Results.NotFound(new { error = "Account not found." });
    }

    var result = launcher.StartAccounts(new[] { account });
    return Results.Json(result);
});

app.MapPost("/api/finance/transactions/refresh", async (
    CodexFinanceRefreshLauncher launcher,
    FinanceStore store,
    FinanceRefreshCoordinator refresher,
    CancellationToken cancellationToken) =>
{
    var state = await store.GetStateAsync(refresher.Status, cancellationToken);
    var result = launcher.StartTransactions(state.Transactions.Accounts);
    return Results.Json(result);
});

app.MapPost("/api/finance/credential-lease/{accountId}/{field}", (
    string accountId,
    string field,
    HttpRequest request,
    HttpResponse response,
    FinanceCredentialLeaseStore leases) =>
{
    response.Headers.CacheControl = "no-store";
    response.Headers.Pragma = "no-cache";
    response.Headers["X-Content-Type-Options"] = "nosniff";

    var token = request.Headers["X-Finance-Credential-Lease"].ToString();
    return leases.TryRedeem(token, accountId, field, out var value)
        ? Results.Text(value ?? string.Empty, "text/plain", Encoding.UTF8)
        : Results.Unauthorized();
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

app.MapPut("/api/finance/accounts/{id}/credentials", async (
    string id,
    FinanceAccountCredentialRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.SetAccountCredentialsAsync(id, request, cancellationToken);
    return result is null
        ? Results.BadRequest(new { error = "A known account plus a non-empty username and password are required." })
        : Results.Json(result);
});

app.MapDelete("/api/finance/accounts/{id}/credentials", async (
    string id,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.DeleteAccountCredentialsAsync(id, cancellationToken);
    return result is null
        ? Results.NotFound(new { error = "Account not found." })
        : Results.Json(result);
});

app.MapPost("/api/finance/accounts/{id}/values", async (
    string id,
    FinanceAccountValuesRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.UpdateAccountValuesAsync(id, request, cancellationToken);
    return result.Status switch
    {
        FinanceAccountValuesUpdateStatus.Updated => Results.Json(new
        {
            account = result.Account,
            completionToken = result.CompletionToken
        }),
        FinanceAccountValuesUpdateStatus.NotFound => Results.NotFound(new { error = result.Error }),
        _ => Results.BadRequest(new { error = result.Error })
    };
});

app.MapPost("/api/finance/accounts/{id}/refresh-complete", async (
    string id,
    FinanceAccountRefreshCompletionRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.CompleteAccountRefreshAsync(id, request.CompletionToken, cancellationToken);
    return account is null
        ? Results.BadRequest(new { error = "No matching verified account-value update is pending." })
        : Results.Json(account);
});

app.MapPut("/api/finance/accounts/{id}/notes", async (
    string id,
    FinanceAccountNotesRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var notes = await store.UpdateAccountNotesAsync(id, request, cancellationToken);
    return notes is null
        ? Results.BadRequest(new { error = "An editable account and non-empty collector notes under 30,000 characters are required." })
        : Results.Json(notes);
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

app.MapPut("/api/finance/salary-plan", async (
    FinanceSalaryPlanRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var plan = await store.UpdateSalaryPlanAsync(request, cancellationToken);
    return plan is null
        ? Results.BadRequest(new { error = "A positive salary, supported currency, pay interval, next pay date, and valid bonuses are required." })
        : Results.Json(plan);
});

app.MapPost("/api/finance/transactions", async (
    FinanceTransactionRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var transaction = await store.RecordTransactionAsync(request, cancellationToken);
    return transaction is null
        ? Results.BadRequest(new { error = "A shown account, posted date, non-zero signed amount, and matching money_in or money_out direction are required." })
        : Results.Json(transaction);
});

app.MapPut("/api/finance/transactions/bulk-label", async (
    FinanceTransactionBulkLabelRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.AddTransactionLabelAsync(request, cancellationToken);
    return result is null
        ? Results.BadRequest(new { error = "At least one transaction ID and a non-empty label are required." })
        : Results.Json(result);
});

app.MapPut("/api/finance/transactions/{id}", async (
    string id,
    FinanceTransactionRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var transaction = await store.RecordTransactionAsync(
        request with { RecordId = id, ReplaceMetadata = true },
        cancellationToken);
    return transaction is null
        ? Results.NotFound(new { error = "Transaction not found or its account is not editable." })
        : Results.Json(transaction);
});

app.MapPost("/api/finance/transactions/{accountId}/days/{postedOn}/snapshot", async (
    string accountId,
    DateOnly postedOn,
    FinanceTransactionDaySnapshotRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.ReconcileTransactionDayAsync(accountId, postedOn, request, cancellationToken);
    return result is null
        ? Results.BadRequest(new
        {
            error = "A complete snapshot containing only valid transactions for the requested account and posted date is required."
        })
        : Results.Json(result);
});

app.MapPost("/api/finance/transactions/{accountId}/sync", async (
    string accountId,
    FinanceTransactionSyncRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.RecordTransactionSyncAsync(accountId, request, cancellationToken);
    return account is null
        ? Results.BadRequest(new { error = "The account and complete required coverage window are required. Initial backfills must cover 24 months; later refreshes must cover one month." })
        : Results.Json(account);
});

app.MapPost("/api/finance/recurring-transactions", async (
    FinanceRecurringTransactionRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var recurring = await store.AddRecurringTransactionAsync(request, cancellationToken);
    return recurring is null
        ? Results.BadRequest(new { error = "A known cash account, description, non-zero amount, supported currency, and valid next date are required." })
        : Results.Json(recurring);
});

app.MapPut("/api/finance/recurring-transactions/{id}", async (
    string id,
    FinanceRecurringTransactionRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var recurring = await store.UpdateRecurringTransactionAsync(id, request, cancellationToken);
    return recurring is null
        ? Results.BadRequest(new { error = "A visible recurring transaction, known cash account, description, non-zero amount, supported currency, and valid next date are required." })
        : Results.Json(recurring);
});
app.MapPut("/api/finance/recurring-transactions/{id}/status", async (
    string id,
    FinanceRecurringTransactionStatusRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var recurring = await store.UpdateRecurringTransactionStatusAsync(id, request.Status, cancellationToken);
    return recurring is null
        ? Results.BadRequest(new { error = "A known recurring transaction and approved, rejected, or pending status are required." })
        : Results.Json(recurring);
});
app.MapDelete("/api/finance/recurring-transactions/{id}", async (
    string id,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var removed = await store.RemoveRecurringTransactionAsync(id, cancellationToken);
    return removed
        ? Results.Json(new { removed = true })
        : Results.NotFound(new { error = "A visible recurring transaction is required." });
});

app.MapPut("/api/finance/settings/currency", async (
    FinanceMasterCurrencyRequest request,
    FinanceCurrencyService currencies,
    CancellationToken cancellationToken) =>
{
    var settings = await currencies.SetMasterCurrencyAsync(request.Currency, cancellationToken);
    return settings is null
        ? Results.BadRequest(new { error = "A supported three-letter currency code is required." })
        : Results.Json(settings);
});

app.MapPut("/api/finance/settings/tax-profile", async (
    FinanceTaxProfileRequest request,
    FinanceTaxProfileService taxProfiles,
    CancellationToken cancellationToken) =>
{
    var profile = await taxProfiles.SetProfileAsync(request, cancellationToken);
    return profile is null
        ? Results.BadRequest(new { error = "A supported country, U.S. state when applicable, income source, and marital status are required." })
        : Results.Json(profile);
});
app.MapGet("/api/finance/settings/ui-preferences", (FinanceUiPreferencesService preferences) =>
    Results.Json(preferences.GetPreferences()));

app.MapPut("/api/finance/settings/ui-preferences", async (
    FinanceUiPreferencesRequest request,
    FinanceUiPreferencesService preferences,
    CancellationToken cancellationToken) =>
{
    var saved = await preferences.SetPreferencesAsync(request, cancellationToken);
    return saved is null
        ? Results.BadRequest(new { error = "The history range, projection dates, or hidden sections are invalid." })
        : Results.Json(saved);
});

app.MapPut("/api/finance/accounts/{id}/currency", async (
    string id,
    FinanceAccountCurrencyRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.UpdateAccountCurrencyAsync(id, request.Currency, cancellationToken);
    return account is null
        ? Results.BadRequest(new { error = "An editable account and supported three-letter currency code are required." })
        : Results.Json(account);
});

app.MapPut("/api/finance/accounts/{id}/apr", async (
    string id,
    FinanceAccountAprRequest request,
    FinanceStore store,
    CancellationToken cancellationToken) =>
{
    var account = await store.UpdateAccountAprAsync(id, request, cancellationToken);
    return account is null
        ? Results.BadRequest(new
        {
            error = "A known editable credit or loan account, nonnegative regular APR, and complete promotional APR/start-of-regular-rate pair are required."
        })
        : Results.Json(account);
});

app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "finances.html"));
});

if (OperatingSystem.IsWindows() && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase))
{
    await RunWithFinanceTrayAsync(app, FinanceBrowserUrl);
}
else
{
    app.Run();
}

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

    public static string ResolveEnvPath(params string[] searchRoots)
    {
        var explicitPath = Environment.GetEnvironmentVariable("FINANCE_APP_ENV");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        foreach (var root in searchRoots)
        {
            candidates.Add(Path.Combine(root, ".env.finance"));
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates.First();
    }

    public static FinanceSettings Load(params string[] searchRoots)
    {
        var envPath = ResolveEnvPath(searchRoots);
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
                ParseDecimal(Get("CASH_BALANCE")),
                ParseDecimal(Get("BALANCE_OWED")),
                ParseDecimal(Get("CREDIT_LIMIT")),
                ParseDecimal(Get("CREDIT_AVAILABLE")),
                ParseDecimal(Get("APR_PERCENT")),
                ParseDecimal(Get("PROMOTIONAL_APR_PERCENT")),
                ParseDateOnly(Get("PROMOTIONAL_APR_ENDS_ON")),
                ParseDecimal(Get("MINIMUM_PAYMENT")),
                ParseDateOnly(Get("PAYMENT_DUE_DATE")),
                ParseNullableBoolean(Get("MINIMUM_PAYMENT_MET")),
                EmptyToNull(Get("COLLECTOR")) ?? (string.IsNullOrWhiteSpace(Get("LOGIN_URL")) ? "manual" : "computer_control"),
                EmptyToNull(Get("COLLECTOR_NOTES")),
                FinanceCurrencyService.NormalizeAccountCurrency(Get("CURRENCY"), name)));
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

    private static DateOnly? ParseDateOnly(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static bool? ParseNullableBoolean(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "paid" => true,
            "false" or "no" or "outstanding" => false,
            _ => null
        };
}

public sealed class FinanceCurrencyService : IHostedService
{
    private const string ExchangeRatePath = "v6/latest/USD";
    private const string DefaultCurrency = "USD";
    private static readonly JsonSerializerOptions CurrencyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly IReadOnlyList<string> SupportedCurrencyCodes = BuildSupportedCurrencyCodes();
    private static readonly HashSet<string> SupportedCurrencySet = new(SupportedCurrencyCodes, StringComparer.OrdinalIgnoreCase);
    private const string KnownCurrencyCodes =
        "USD CAD GBP EUR AUD JPY CNY AED AFN ALL AMD ANG AOA ARS AWG AZN BAM BBD BDT BGN BHD BIF BMD BND BOB BRL BSD BTN BWP BYN BZD CDF CHF CLF CLP CNH COP CRC CUP CVE CZK DJF DKK DOP DZD EGP ERN ETB FJD FKP FOK GEL GGP GHS GIP GMD GNF GTQ GYD HKD HNL HRK HTG HUF IDR ILS IMP INR IQD IRR ISK JEP JMD JOD KES KGS KHR KID KMF KRW KWD KYD KZT LAK LBP LKR LRD LSL LYD MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON RSD RUB RWF SAR SBD SCR SDG SEK SGD SHP SLE SLL SOS SRD SSP STN SYP SZL THB TJS TMT TND TOP TRY TTD TVD TWD TZS UAH UGX UYU UZS VES VND VUV WST XAF XCD XCG XDR XOF XPF YER ZAR ZMW ZWG ZWL";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _settingsPath;
    private string _masterCurrency;
    private Dictionary<string, decimal> _usdRates;
    private DateTimeOffset? _ratesLastUpdatedUtc;
    private DateTimeOffset? _ratesFetchedAtUtc;
    private string? _lastRefreshError;
    private bool _lastRefreshSucceeded;

    public FinanceCurrencyService(FinanceSettings settings, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        var financeDirectory = Path.Combine(settings.DataRoot, "data", "finance");
        Directory.CreateDirectory(financeDirectory);
        _settingsPath = Path.Combine(financeDirectory, "currency-settings.json");
        var stored = LoadStoredSettings();
        _masterCurrency = NormalizeCurrencyCode(stored?.MasterCurrency) ?? NormalizeCurrencyCode(settings.Currency) ?? DefaultCurrency;
        _usdRates = NormalizeRates(stored?.UsdRates);
        _ratesLastUpdatedUtc = stored?.RatesLastUpdatedUtc;
        _ratesFetchedAtUtc = stored?.RatesFetchedAtUtc;
    }

    public Task StartAsync(CancellationToken cancellationToken) => RefreshRatesAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public FinanceCurrencyDashboard GetDashboard()
    {
        _lock.Wait();
        try
        {
            return BuildDashboard();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceCurrencyDashboard?> SetMasterCurrencyAsync(string? currency, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCurrencyCode(currency);
        if (normalized is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _masterCurrency = normalized;
            await SaveSettingsAsync(cancellationToken);
            return BuildDashboard();
        }
        finally
        {
            _lock.Release();
        }
    }

    public decimal Convert(decimal amount, string? sourceCurrency, string? targetCurrency = null)
    {
        var source = NormalizeCurrencyCode(sourceCurrency) ?? DefaultCurrency;
        _lock.Wait();
        try
        {
            var target = NormalizeCurrencyCode(targetCurrency) ?? _masterCurrency;
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return amount;
            }

            if (!_usdRates.TryGetValue(source, out var sourceRate)
                || !_usdRates.TryGetValue(target, out var targetRate)
                || sourceRate <= 0
                || targetRate <= 0)
            {
                return amount;
            }

            return decimal.Round(amount / sourceRate * targetRate, 2, MidpointRounding.AwayFromZero);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string NormalizeAccountCurrency(string? currency, string? accountName)
    {
        var normalized = NormalizeCurrencyCode(currency);
        if (normalized is not null)
        {
            return normalized;
        }

        return accountName?.Contains("CAD", StringComparison.OrdinalIgnoreCase) == true ? "CAD" : DefaultCurrency;
    }

    public static string? NormalizeCurrencyCode(string? currency)
    {
        var normalized = currency?.Trim().ToUpperInvariant();
        return normalized is not null && SupportedCurrencySet.Contains(normalized) ? normalized : null;
    }

    private async Task RefreshRatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("finance-currency").GetAsync(ExchangeRatePath, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<ExchangeRateApiResponse>(CurrencyJson, cancellationToken);
            if (payload is null
                || !string.Equals(payload.Result, "success", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(payload.BaseCode, DefaultCurrency, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The exchange-rate provider returned an invalid response.");
            }

            var rates = NormalizeRates(payload.Rates);
            if (rates.Count < 100 || !rates.TryGetValue(DefaultCurrency, out var usdRate) || usdRate != 1m)
            {
                throw new InvalidDataException("The exchange-rate provider returned an incomplete rate table.");
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                _usdRates = rates;
                _ratesLastUpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(payload.TimeLastUpdateUnix);
                _ratesFetchedAtUtc = DateTimeOffset.UtcNow;
                _lastRefreshSucceeded = true;
                _lastRefreshError = null;
                await SaveSettingsAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                _lastRefreshSucceeded = false;
                _lastRefreshError = ex.Message;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private FinanceCurrencyDashboard BuildDashboard() =>
        new(_masterCurrency, SupportedCurrencyCodes, _ratesLastUpdatedUtc, _ratesFetchedAtUtc, _usdRates.Count > 1,
            _lastRefreshSucceeded, _lastRefreshError, "https://www.exchangerate-api.com");

    private FinanceCurrencyStoreRecord? LoadStoredSettings()
    {
        var stored = FinanceDataFile.ReadOptionalJson<FinanceCurrencyStoreRecord>(_settingsPath, CurrencyJson);
        if (stored is not null && (stored.Version < 1 || stored.UsdRates is null))
        {
            throw new FinanceDataException("Finance data file 'currency-settings.json' has an invalid structure.");
        }

        return stored;
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        var stored = new FinanceCurrencyStoreRecord(1, _masterCurrency, _ratesLastUpdatedUtc, _ratesFetchedAtUtc, _usdRates);
        await FinanceDataFile.WriteJsonAtomicAsync(_settingsPath, stored, CurrencyJson, cancellationToken);
    }

    private static Dictionary<string, decimal> NormalizeRates(IReadOnlyDictionary<string, decimal>? rates)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [DefaultCurrency] = 1m };
        if (rates is null)
        {
            return result;
        }

        foreach (var (currency, rate) in rates)
        {
            var normalized = NormalizeCurrencyCode(currency);
            if (normalized is not null && rate > 0)
            {
                result[normalized] = rate;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> BuildSupportedCurrencyCodes()
    {
        var priority = new[] { "USD", "CAD", "GBP", "EUR", "AUD", "JPY", "CNY" };
        var codes = KnownCurrencyCodes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return priority.Concat(codes.Except(priority, StringComparer.OrdinalIgnoreCase).OrderBy(code => code, StringComparer.Ordinal)).ToArray();
    }

    private sealed record ExchangeRateApiResponse(
        string? Result,
        [property: JsonPropertyName("base_code")] string? BaseCode,
        [property: JsonPropertyName("time_last_update_unix")] long TimeLastUpdateUnix,
        IReadOnlyDictionary<string, decimal>? Rates);
}

public sealed class FinanceTaxProfileService
{
    private const string DefaultCountryCode = "US";
    private const string DefaultStateCode = "TX";
    private const string DefaultIncomeSource = "employee_salary";
    private static readonly DateOnly DefaultSalaryStartOn = new(2024, 12, 1);
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> UsStateCodes = new(
        "AL AK AZ AR CA CO CT DE FL GA HI ID IL IN IA KS KY LA ME MD MA MI MN MS MO MT NE NV NH NJ NM NY NC ND OH OK OR PA RI SC SD TN TX UT VT VA WA WV WI WY DC"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> IncomeSources = new(
        new[]
        {
            "employee_salary",
            "self_employment",
            "contract_freelance",
            "business_income",
            "investment_income",
            "rental_income",
            "retirement_pension",
            "other"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _settingsPath;
    private string _countryCode;
    private string? _stateCode;
    private string _incomeSource;
    private bool _married;
    private DateOnly _salaryStartOn;

    public FinanceTaxProfileService(FinanceSettings settings)
    {
        var financeDirectory = Path.Combine(settings.DataRoot, "data", "finance");
        Directory.CreateDirectory(financeDirectory);
        _settingsPath = Path.Combine(financeDirectory, "tax-profile-settings.json");

        var stored = LoadStoredSettings();
        _countryCode = NormalizeCountryCode(stored?.CountryCode) ?? DefaultCountryCode;
        _stateCode = string.Equals(_countryCode, DefaultCountryCode, StringComparison.OrdinalIgnoreCase)
            ? NormalizeStateCode(stored?.StateCode) ?? DefaultStateCode
            : null;
        _incomeSource = NormalizeIncomeSource(stored?.IncomeSource) ?? DefaultIncomeSource;
        _married = stored?.Married ?? true;
        _salaryStartOn = stored?.SalaryStartOn is { Year: >= 2000 } startOn
            ? startOn
            : DefaultSalaryStartOn;

        if (stored is null)
        {
            SaveSettings();
        }
    }

    public FinanceTaxProfileDashboard GetDashboard()
    {
        _lock.Wait();
        try
        {
            return BuildDashboard();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceTaxProfileDashboard?> SetProfileAsync(
        FinanceTaxProfileRequest request,
        CancellationToken cancellationToken)
    {
        var countryCode = NormalizeCountryCode(request.CountryCode);
        var stateCode = string.Equals(countryCode, DefaultCountryCode, StringComparison.OrdinalIgnoreCase)
            ? NormalizeStateCode(request.StateCode)
            : null;
        var incomeSource = NormalizeIncomeSource(request.IncomeSource);
        if (countryCode is null
            || incomeSource is null
            || request.Married is null
            || (string.Equals(countryCode, DefaultCountryCode, StringComparison.OrdinalIgnoreCase) && stateCode is null))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _countryCode = countryCode;
            _stateCode = stateCode;
            _incomeSource = incomeSource;
            _married = request.Married.Value;
            await SaveSettingsAsync(cancellationToken);
            return BuildDashboard();
        }
        finally
        {
            _lock.Release();
        }
    }

    private FinanceTaxProfileDashboard BuildDashboard() =>
        new(_countryCode, _stateCode, _incomeSource, _married, _salaryStartOn);

    private FinanceTaxProfileStoreRecord? LoadStoredSettings()
    {
        var stored = FinanceDataFile.ReadOptionalJson<FinanceTaxProfileStoreRecord>(_settingsPath, ProfileJson);
        if (stored is not null && stored.Version < 1)
        {
            throw new FinanceDataException("Finance data file 'tax-profile-settings.json' has an invalid structure.");
        }

        return stored;
    }

    private void SaveSettings()
    {
        FinanceDataFile.WriteJsonAtomic(_settingsPath, BuildStoreRecord(), ProfileJson);
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        await FinanceDataFile.WriteJsonAtomicAsync(
            _settingsPath,
            BuildStoreRecord(),
            ProfileJson,
            cancellationToken);
    }

    private FinanceTaxProfileStoreRecord BuildStoreRecord() =>
        new(1, _countryCode, _stateCode, _incomeSource, _married, _salaryStartOn);

    private static string? NormalizeCountryCode(string? countryCode)
    {
        var normalized = countryCode?.Trim().ToUpperInvariant();
        return normalized is { Length: 2 } && normalized.All(character => character is >= 'A' and <= 'Z')
            ? normalized
            : null;
    }

    private static string? NormalizeStateCode(string? stateCode)
    {
        var normalized = stateCode?.Trim().ToUpperInvariant();
        return normalized is not null && UsStateCodes.Contains(normalized) ? normalized : null;
    }

    private static string? NormalizeIncomeSource(string? incomeSource)
    {
        var normalized = incomeSource?.Trim().ToLowerInvariant();
        return normalized is not null && IncomeSources.Contains(normalized) ? normalized : null;
    }
}
public sealed class FinanceUiPreferencesService
{
    private static readonly JsonSerializerOptions PreferencesJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> AllowedHiddenSections = new(
        new[] { "net", "cash", "credit", "debt", "salary", "credit-loans", "accounts" },
        StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _settingsPath;
    private FinanceUiPreferencesDashboard _preferences;

    public FinanceUiPreferencesService(FinanceSettings settings)
    {
        var financeDirectory = Path.Combine(settings.DataRoot, "data", "finance");
        Directory.CreateDirectory(financeDirectory);
        _settingsPath = Path.Combine(financeDirectory, "ui-preferences.json");
        var stored = LoadStoredSettings();
        _preferences = Normalize(
            stored?.HistoryStartOn,
            stored?.HistoryEndOn,
            stored?.ProjectionEnabled ?? false,
            stored?.ProjectionStartOn,
            stored?.ProjectionOn,
            stored?.HiddenValueSections)
            ?? EmptyPreferences();
    }

    public FinanceUiPreferencesDashboard GetPreferences()
    {
        _lock.Wait();
        try
        {
            return _preferences;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceUiPreferencesDashboard?> SetPreferencesAsync(
        FinanceUiPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(
            request.HistoryStartOn,
            request.HistoryEndOn,
            request.ProjectionEnabled,
            request.ProjectionStartOn,
            request.ProjectionOn,
            request.HiddenValueSections);
        if (normalized is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _preferences = normalized;
            var stored = new FinanceUiPreferencesStoreRecord(
                1,
                normalized.HistoryStartOn,
                normalized.HistoryEndOn,
                normalized.ProjectionEnabled,
                normalized.ProjectionStartOn,
                normalized.ProjectionOn,
                normalized.HiddenValueSections);
            await FinanceDataFile.WriteJsonAtomicAsync(
                _settingsPath,
                stored,
                PreferencesJson,
                cancellationToken);
            return _preferences;
        }
        finally
        {
            _lock.Release();
        }
    }

    private FinanceUiPreferencesStoreRecord? LoadStoredSettings()
    {
        var stored = FinanceDataFile.ReadOptionalJson<FinanceUiPreferencesStoreRecord>(_settingsPath, PreferencesJson);
        if (stored is not null && stored.Version < 1)
        {
            throw new FinanceDataException("Finance data file 'ui-preferences.json' has an invalid structure.");
        }

        return stored;
    }

    private static FinanceUiPreferencesDashboard? Normalize(
        DateOnly? historyStartOn,
        DateOnly? historyEndOn,
        bool projectionEnabled,
        DateOnly? projectionStartOn,
        DateOnly? projectionOn,
        IReadOnlyList<string>? hiddenValueSections)
    {
        if (historyStartOn > historyEndOn
            || (projectionEnabled && projectionOn is null)
            || (projectionStartOn is not null && projectionOn is not null && projectionStartOn > projectionOn))
        {
            return null;
        }

        var hiddenSections = (hiddenValueSections ?? Array.Empty<string>())
            .Where(AllowedHiddenSections.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new FinanceUiPreferencesDashboard(
            historyStartOn,
            historyEndOn,
            projectionEnabled,
            projectionStartOn,
            projectionOn,
            hiddenSections);
    }

    private static FinanceUiPreferencesDashboard EmptyPreferences() =>
        new(null, null, false, null, null, Array.Empty<string>());
}

public sealed class FinanceStore
{
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, PendingAccountRefresh> _pendingAccountRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly FinanceSettings _settings;
    private readonly FinanceCurrencyService _currencies;
    private readonly FinanceTaxProfileService _taxProfiles;
    private readonly IFinanceCredentialStore _credentialStore;
    private readonly string _financeDirectory;
    private readonly string _accountsPath;
    private readonly string _snapshotsPath;
    private readonly string _logPath;
    private readonly string _incomePath;
    private readonly string _salaryPlanPath;
    private readonly string _transactionsPath;
    private readonly string _recurringTransactionsPath;

    public FinanceStore(
        FinanceSettings settings,
        FinanceCurrencyService currencies,
        FinanceTaxProfileService taxProfiles,
        IFinanceCredentialStore credentialStore)
    {
        _settings = settings;
        _currencies = currencies;
        _taxProfiles = taxProfiles;
        _credentialStore = credentialStore;
        _financeDirectory = Path.Combine(_settings.DataRoot, "data", "finance");
        Directory.CreateDirectory(_financeDirectory);
        _accountsPath = Path.Combine(_financeDirectory, "accounts.json");
        _snapshotsPath = Path.Combine(_financeDirectory, "snapshots.jsonl");
        _logPath = Path.Combine(_financeDirectory, "refresh-log.jsonl");
        _incomePath = Path.Combine(_financeDirectory, "income.json");
        _salaryPlanPath = Path.Combine(_financeDirectory, "salary-plan.json");
        _transactionsPath = Path.Combine(_financeDirectory, "transactions.json");
        _recurringTransactionsPath = Path.Combine(_financeDirectory, "recurring-transactions.json");
        MigrateAccountCurrencies();
        MigrateTransactionAmounts();
    }

    public async Task<FinanceDashboardResponse> GetStateAsync(FinanceRefreshStatus status, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var configuredAccounts = GetConfiguredAccounts();
            var currencySettings = _currencies.GetDashboard();
            var taxProfile = _taxProfiles.GetDashboard();
            var current = ConvertSnapshotToMaster(BuildSnapshot("current", persistable: false), configuredAccounts, currencySettings.MasterCurrency);
            var snapshots = ReadSnapshots()
                .Select(snapshot => ConvertSnapshotToMaster(snapshot, configuredAccounts, currencySettings.MasterCurrency))
                .ToList();
            var income = ConvertIncomeToMaster(
                BuildIncomeDashboard(LoadIncomeLedger().Records ?? Array.Empty<FinanceIncomeRecord>(), configuredAccounts),
                currencySettings.MasterCurrency);
            var salaryPlan = BuildSalaryPlanDashboard(LoadSalaryPlanLedger(), currencySettings.MasterCurrency);
            var transactionLedger = LoadTransactionLedger();
            var transactions = ConvertTransactionsToMaster(BuildTransactionsDashboard(
                transactionLedger.Records ?? Array.Empty<FinanceTransactionRecord>(),
                transactionLedger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>(),
                configuredAccounts), currencySettings.MasterCurrency);
            var recurringTransactions = ConvertRecurringTransactionsToMaster(BuildRecurringTransactionsDashboard(
                transactionLedger.Records ?? Array.Empty<FinanceTransactionRecord>(),
                LoadRecurringTransactionLedger().Records ?? Array.Empty<FinanceRecurringTransactionRecord>(),
                configuredAccounts), currencySettings.MasterCurrency);
            return new FinanceDashboardResponse(
                DateTimeOffset.UtcNow,
                currencySettings.MasterCurrency,
                _settings.EnvPath,
                configuredAccounts.Count,
                _settings.RefreshTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                current,
                snapshots,
                ReadLogs().TakeLast(20).Reverse().ToList(),
                income,
                salaryPlan,
                transactions,
                recurringTransactions,
                currencySettings,
                taxProfile,
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
        if (name is null
            || kind is null
            || !TryBuildCredential(request.Username, request.Password, out var credential))
        {
            return null;
        }

        var collectorNotes = CleanFinanceText(request.CollectorNotes);
        if (FinanceCredentialMigration.ContainsCredentialMaterial(collectorNotes, credential))
        {
            return null;
        }

        var accountId = $"ui-{Guid.NewGuid():N}";
        var record = new UserFinanceAccountRecord(
            accountId,
            name,
            kind,
            CleanFinanceText(request.Institution) ?? "Unknown",
            CleanFinanceText(request.LoginUrl),
            request.CashBalance,
            request.BalanceOwed,
            request.CreditLimit,
            request.CreditAvailable,
            request.AprPercent,
            null,
            null,
            request.MinimumPayment,
            request.PaymentDueDate,
            request.MinimumPaymentMet,
            string.IsNullOrWhiteSpace(request.LoginUrl) ? "manual" : "computer_control",
            collectorNotes,
            FinanceCurrencyService.NormalizeAccountCurrency(request.Currency, name));

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            accounts.Add(record);
            if (credential is not null)
            {
                _credentialStore.Write(accountId, credential);
            }

            try
            {
                await SaveUserAccountsAsync(accounts, cancellationToken);
            }
            catch
            {
                if (credential is not null)
                {
                    _credentialStore.Delete(accountId);
                }

                throw;
            }

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
        if (name is null
            || kind is null
            || !TryBuildCredential(request.Username, request.Password, out var requestedCredential))
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
            var storedCredential = _credentialStore.Read(current.Id);
            var collectorNotes = CleanFinanceText(request.CollectorNotes);
            if (FinanceCredentialMigration.ContainsCredentialMaterial(
                    collectorNotes,
                    requestedCredential,
                    storedCredential))
            {
                return null;
            }

            var loginUrl = CleanFinanceText(request.LoginUrl);
            var updated = current with
            {
                Name = name,
                Kind = kind,
                Institution = CleanFinanceText(request.Institution) ?? "Unknown",
                LoginUrl = loginUrl,
                CashBalance = request.CashBalance,
                BalanceOwed = request.BalanceOwed,
                CreditLimit = request.CreditLimit,
                CreditAvailable = request.CreditAvailable,
                AprPercent = request.AprPercent,
                MinimumPayment = request.MinimumPayment,
                PaymentDueDate = request.PaymentDueDate,
                MinimumPaymentMet = request.MinimumPaymentMet,
                Collector = string.IsNullOrWhiteSpace(loginUrl) ? "manual" : "computer_control",
                CollectorNotes = collectorNotes,
                Currency = FinanceCurrencyService.NormalizeAccountCurrency(request.Currency, name)
            };
            accounts[index] = updated;
            var previousCredential = requestedCredential is null ? null : storedCredential;
            if (requestedCredential is not null)
            {
                _credentialStore.Write(current.Id, requestedCredential);
            }

            try
            {
                await SaveUserAccountsAsync(accounts, cancellationToken);
            }
            catch
            {
                if (requestedCredential is not null)
                {
                    RestoreCredential(current.Id, previousCredential);
                }

                throw;
            }

            return ToFinanceAccount(updated.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountCredentialResult?> SetAccountCredentialsAsync(
        string id,
        FinanceAccountCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(id);
        if (accountId is null
            || !TryBuildCredential(request.Username, request.Password, out var credential)
            || credential is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = GetConfiguredAccounts().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null
                || FinanceCredentialMigration.ContainsCredentialMaterial(account.CollectorNotes, credential))
            {
                return null;
            }

            _credentialStore.Write(accountId, credential);
            return new FinanceAccountCredentialResult(accountId, true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountCredentialResult?> DeleteAccountCredentialsAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(id);
        if (accountId is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!GetConfiguredAccounts().Any(account =>
                    string.Equals(account.Id, accountId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            _credentialStore.Delete(accountId);
            return new FinanceAccountCredentialResult(accountId, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountValuesUpdateResult> UpdateAccountValuesAsync(
        string id,
        FinanceAccountValuesRequest request,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return FinanceAccountValuesUpdateResult.NotFound();
            }

            var current = accounts[index];
            _pendingAccountRefreshes.Remove(current.Id);
            var validationError = ValidateCompleteAccountValues(current, request);
            if (validationError is not null)
            {
                return FinanceAccountValuesUpdateResult.Invalid(validationError);
            }

            var collectorNotes = CleanFinanceText(request.CollectorNotes);
            if (FinanceCredentialMigration.ContainsCredentialMaterial(
                    collectorNotes,
                    _credentialStore.Read(current.Id)))
            {
                return FinanceAccountValuesUpdateResult.Invalid(
                    "Collector notes must not contain usernames, passwords, passcodes, or PINs.");
            }

            var updated = current with
            {
                CashBalance = request.CashBalance,
                BalanceOwed = request.BalanceOwed,
                CreditLimit = request.CreditLimit,
                CreditAvailable = request.CreditAvailable,
                AprPercent = request.AprPercent,
                MinimumPayment = request.MinimumPayment,
                PaymentDueDate = request.PaymentDueDate,
                MinimumPaymentMet = request.MinimumPaymentMet,
                CollectorNotes = collectorNotes
            };
            accounts[index] = updated;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            var completionToken = Guid.NewGuid().ToString("N");
            _pendingAccountRefreshes[current.Id] = new PendingAccountRefresh(completionToken, updated);
            return FinanceAccountValuesUpdateResult.Updated(ToFinanceAccount(updated.ToConfig()), completionToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountSnapshot?> CompleteAccountRefreshAsync(
        string id,
        string? completionToken,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(completionToken)
                || !_pendingAccountRefreshes.TryGetValue(id, out var pending)
                || !string.Equals(pending.CompletionToken, completionToken, StringComparison.Ordinal))
            {
                return null;
            }

            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || accounts[index] != pending.Account)
            {
                _pendingAccountRefreshes.Remove(id);
                return null;
            }

            var completedAtUtc = DateTimeOffset.UtcNow;
            var completed = accounts[index] with { LastUpdatedUtc = completedAtUtc };
            accounts[index] = completed;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            _pendingAccountRefreshes.Remove(id);

            var snapshot = BuildSnapshot("assisted", persistable: true);
            await AppendJsonLineAsync(_snapshotsPath, snapshot, cancellationToken);
            await AppendJsonLineAsync(
                _logPath,
                new FinanceRefreshLog(completedAtUtc, "ok", "Verified finance values updated from an assisted account check.", "assisted"),
                cancellationToken);
            return ToFinanceAccount(completed.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountSnapshot?> UpdateAccountAprAsync(
        string id,
        FinanceAccountAprRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(id);
        var hasPromotionRate = request.PromotionalAprPercent is not null;
        var hasPromotionEnd = request.PromotionalAprEndsOn is not null;
        if (accountId is null
            || request.AprPercent is null or < 0
            || hasPromotionRate != hasPromotionEnd
            || request.PromotionalAprPercent is < 0)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account =>
                string.Equals(account.Id, accountId, StringComparison.OrdinalIgnoreCase)
                && account.Kind is "credit_card" or "loan");
            if (index < 0)
            {
                return null;
            }

            var updated = accounts[index] with
            {
                AprPercent = request.AprPercent,
                PromotionalAprPercent = request.PromotionalAprPercent,
                PromotionalAprEndsOn = request.PromotionalAprEndsOn
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

    public async Task<FinanceAccountSnapshot?> UpdateAccountCurrencyAsync(
        string id,
        string? currency,
        CancellationToken cancellationToken)
    {
        var normalized = FinanceCurrencyService.NormalizeCurrencyCode(currency);
        if (normalized is null)
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

            var updated = accounts[index] with { Currency = normalized };
            accounts[index] = updated;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            return ToFinanceAccount(updated.ToConfig());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceAccountNotesResult?> UpdateAccountNotesAsync(
        string id,
        FinanceAccountNotesRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(id);
        var collectorNotes = CleanFinanceText(request.CollectorNotes);
        if (accountId is null || collectorNotes is null || collectorNotes.Length > 30_000)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = LoadUserAccounts().ToList();
            var index = accounts.FindIndex(account =>
                string.Equals(account.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            if (FinanceCredentialMigration.ContainsCredentialMaterial(
                    collectorNotes,
                    _credentialStore.Read(accounts[index].Id)))
            {
                return null;
            }

            var updated = accounts[index] with { CollectorNotes = collectorNotes };
            accounts[index] = updated;
            await SaveUserAccountsAsync(accounts, cancellationToken);
            return new FinanceAccountNotesResult(updated.Id, updated.CollectorNotes);
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

    public async Task<FinanceSalaryPlanDashboard?> UpdateSalaryPlanAsync(
        FinanceSalaryPlanRequest request,
        CancellationToken cancellationToken)
    {
        var amount = request.Amount;
        var currency = FinanceCurrencyService.NormalizeCurrencyCode(request.Currency);
        var interval = NormalizeSalaryInterval(request.Interval);
        if (amount is null || amount <= 0 || currency is null || interval is null || request.NextOn is null)
        {
            return null;
        }

        var requestedBonuses = request.Bonuses ?? Array.Empty<FinanceBonusRequest>();
        if (requestedBonuses.Count > 100)
        {
            return null;
        }

        var bonusIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bonuses = new List<FinanceBonusRecord>();
        foreach (var requestedBonus in requestedBonuses)
        {
            var bonusCurrency = FinanceCurrencyService.NormalizeCurrencyCode(requestedBonus.Currency);
            if (requestedBonus.Amount is null
                || requestedBonus.Amount <= 0
                || requestedBonus.PaidOn is null
                || bonusCurrency is null)
            {
                return null;
            }

            var id = CleanFinanceText(requestedBonus.Id);
            if (id is null || !bonusIds.Add(id))
            {
                do
                {
                    id = $"bonus-{Guid.NewGuid():N}";
                }
                while (!bonusIds.Add(id));
            }

            bonuses.Add(new FinanceBonusRecord(
                id,
                CleanFinanceText(requestedBonus.Description) ?? "Bonus",
                requestedBonus.Amount.Value,
                bonusCurrency,
                requestedBonus.PaidOn.Value));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var ledger = new FinanceSalaryPlanLedger(
                1,
                new FinanceSalaryScheduleRecord(
                    amount.Value,
                    currency,
                    interval,
                    request.NextOn.Value,
                    DateTimeOffset.UtcNow),
                bonuses);
            await SaveSalaryPlanLedgerAsync(ledger, cancellationToken);
            return BuildSalaryPlanDashboard(ledger, _currencies.GetDashboard().MasterCurrency);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceTransactionEntry?> RecordTransactionAsync(FinanceTransactionRequest request, CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(request.AccountId);
        if (accountId is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = GetConfiguredAccounts().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase)
                && IsTransactionAccount(candidate));
            var normalized = account is null ? null : NormalizeTransactionRequest(request, account.Id);
            if (account is null || normalized is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var ledger = LoadTransactionLedger();
            var records = (ledger.Records ?? Array.Empty<FinanceTransactionRecord>()).ToList();
            var index = FindTransactionMatchIndex(
                records,
                account.Id,
                normalized,
                allowedIndices: null,
                minimumScore: 600,
                requireUniqueBest: true);

            if (normalized.RecordId is not null && index < 0)
            {
                return null;
            }

            FinanceTransactionRecord record;
            if (index >= 0)
            {
                record = UpdateTransactionRecord(records[index], normalized, now);
                records[index] = record;
            }
            else
            {
                record = CreateTransactionRecord(account.Id, normalized, now);
                records.Add(record);
            }

            await SaveTransactionLedgerAsync(
                records,
                ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>(),
                ledger.DayReconciliations ?? Array.Empty<FinanceTransactionDayReconciliationRecord>(),
                cancellationToken);
            return ToTransactionEntry(record, account.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceTransactionBulkLabelResult?> AddTransactionLabelAsync(
        FinanceTransactionBulkLabelRequest request,
        CancellationToken cancellationToken)
    {
        var label = CleanFinanceText(request.Label);
        var requestedIds = (request.TransactionIds ?? Array.Empty<string>())
            .Select(CleanFinanceText)
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5000)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (label is null || requestedIds.Count == 0)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var ledger = LoadTransactionLedger();
            var records = (ledger.Records ?? Array.Empty<FinanceTransactionRecord>()).ToList();
            var updatedCount = 0;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (!requestedIds.Contains(record.Id))
                {
                    continue;
                }

                var labels = NormalizeTransactionLabels(
                    (record.Labels ?? Array.Empty<string>()).Append(label),
                    record.Label);
                if (LabelsEqual(record.Labels, labels))
                {
                    continue;
                }

                records[index] = record with
                {
                    Label = labels.FirstOrDefault(),
                    Labels = labels,
                    LastSeenAtUtc = DateTimeOffset.UtcNow
                };
                updatedCount++;
            }

            if (updatedCount > 0)
            {
                await SaveTransactionLedgerAsync(
                    records,
                    ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>(),
                    ledger.DayReconciliations ?? Array.Empty<FinanceTransactionDayReconciliationRecord>(),
                    cancellationToken);
            }

            return new FinanceTransactionBulkLabelResult(requestedIds.Count, updatedCount, label);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceTransactionDaySnapshotResult?> ReconcileTransactionDayAsync(
        string accountId,
        DateOnly postedOn,
        FinanceTransactionDaySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var cleanedAccountId = CleanFinanceText(accountId);
        if (cleanedAccountId is null || !request.Complete || request.Transactions is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = GetConfiguredAccounts().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cleanedAccountId, StringComparison.OrdinalIgnoreCase)
                && IsTransactionAccount(candidate));
            if (account is null)
            {
                return null;
            }

            var normalizedTransactions = new List<NormalizedFinanceTransaction>();
            foreach (var transaction in request.Transactions)
            {
                var normalized = NormalizeTransactionRequest(transaction, account.Id, postedOn);
                if (normalized is null)
                {
                    return null;
                }

                normalizedTransactions.Add(normalized);
            }

            if (HasDuplicateStableTransactionIdentity(normalizedTransactions))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var ledger = LoadTransactionLedger();
            var records = (ledger.Records ?? Array.Empty<FinanceTransactionRecord>()).ToList();
            if (normalizedTransactions.Any(transaction =>
                    transaction.RecordId is not null
                    && !records.Any(record =>
                        string.Equals(record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(record.Id, transaction.RecordId, StringComparison.OrdinalIgnoreCase))))
            {
                return null;
            }

            var removableIndices = records
                .Select((record, index) => (record, index))
                .Where(item =>
                    string.Equals(item.record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase)
                    && item.record.PostedOn == postedOn)
                .Select(item => item.index)
                .ToHashSet();
            var availableIndices = new HashSet<int>(removableIndices);
            for (var index = 0; index < records.Count; index++)
            {
                if (string.Equals(records[index].AccountId, account.Id, StringComparison.OrdinalIgnoreCase)
                    && normalizedTransactions.Any(transaction =>
                        (transaction.RecordId is not null
                         && string.Equals(records[index].Id, transaction.RecordId, StringComparison.OrdinalIgnoreCase))
                        || (transaction.SourceTransactionId is not null
                            && string.Equals(records[index].SourceTransactionId, transaction.SourceTransactionId, StringComparison.OrdinalIgnoreCase))))
                {
                    availableIndices.Add(index);
                }
            }
            var reconciled = new List<FinanceTransactionRecord>();
            var inserted = 0;
            var updated = 0;
            var unchanged = 0;

            foreach (var normalized in normalizedTransactions.OrderByDescending(GetTransactionIdentityStrength))
            {
                var index = FindTransactionMatchIndex(
                    records,
                    account.Id,
                    normalized,
                    availableIndices,
                    minimumScore: 100,
                    requireUniqueBest: false);
                if (index >= 0)
                {
                    var existing = records[index];
                    var record = UpdateTransactionRecord(existing, normalized, now);
                    records[index] = record;
                    availableIndices.Remove(index);
                    removableIndices.Remove(index);
                    reconciled.Add(record);
                    if (HasSameTransactionContent(existing, record))
                    {
                        unchanged++;
                    }
                    else
                    {
                        updated++;
                    }
                }
                else
                {
                    var record = CreateTransactionRecord(account.Id, normalized, now);
                    records.Add(record);
                    reconciled.Add(record);
                    inserted++;
                }
            }

            var removedIds = removableIndices.Select(index => records[index].Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            records.RemoveAll(record => removedIds.Contains(record.Id));
            var dayReconciliations = (ledger.DayReconciliations
                ?? Array.Empty<FinanceTransactionDayReconciliationRecord>()).ToList();
            var reconciliation = new FinanceTransactionDayReconciliationRecord(
                account.Id,
                postedOn,
                normalizedTransactions.Count,
                now);
            var reconciliationIndex = dayReconciliations.FindIndex(existing =>
                string.Equals(existing.AccountId, account.Id, StringComparison.OrdinalIgnoreCase)
                && existing.PostedOn == postedOn);
            if (reconciliationIndex >= 0)
            {
                dayReconciliations[reconciliationIndex] = reconciliation;
            }
            else
            {
                dayReconciliations.Add(reconciliation);
            }

            await SaveTransactionLedgerAsync(
                records,
                ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>(),
                dayReconciliations,
                cancellationToken);
            return new FinanceTransactionDaySnapshotResult(
                account.Id, postedOn, normalizedTransactions.Count, inserted, updated, unchanged,
                removedIds.Count, reconciled.Select(record => ToTransactionEntry(record, account.Name)).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceTransactionAccount?> RecordTransactionSyncAsync(
        string accountId,
        FinanceTransactionSyncRequest request,
        CancellationToken cancellationToken)
    {
        var cleanedAccountId = CleanFinanceText(accountId);
        var mode = CleanFinanceText(request.Mode)?.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (cleanedAccountId is null
            || mode is not ("initial_backfill" or "incremental")
            || request.CoverageStartOn is null
            || request.CoverageEndOn is null
            || request.CoverageStartOn > request.CoverageEndOn)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = GetConfiguredAccounts().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cleanedAccountId, StringComparison.OrdinalIgnoreCase)
                && IsTransactionAccount(candidate));
            if (account is null)
            {
                return null;
            }

            var today = DateOnly.FromDateTime(DateTime.Now);
            var requiredStartOn = mode == "initial_backfill" ? today.AddMonths(-24) : today.AddMonths(-1);
            if (request.CoverageStartOn > requiredStartOn || request.CoverageEndOn < today)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var ledger = LoadTransactionLedger();
            var records = ledger.Records ?? Array.Empty<FinanceTransactionRecord>();
            var syncStates = (ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>()).ToList();
            var index = syncStates.FindIndex(existing =>
                string.Equals(existing.AccountId, account.Id, StringComparison.OrdinalIgnoreCase));
            var existing = index >= 0 ? syncStates[index] : null;
            if (mode == "incremental" && existing is null)
            {
                return null;
            }

            var dayReconciliations = ledger.DayReconciliations
                ?? Array.Empty<FinanceTransactionDayReconciliationRecord>();
            var evidenceCutoff = mode == "incremental"
                ? existing!.LastSuccessfulRefreshAtUtc
                : DateTimeOffset.MinValue;
            if (!HasCompleteMonthlyReconciliationEvidence(
                    dayReconciliations,
                    account.Id,
                    requiredStartOn,
                    today,
                    evidenceCutoff))
            {
                return null;
            }

            FinanceTransactionSyncRecord syncState;
            if (mode == "initial_backfill")
            {
                syncState = new FinanceTransactionSyncRecord(
                    account.Id,
                    existing?.InitialBackfillCompletedAtUtc ?? now,
                    existing is null || request.CoverageStartOn < existing.BackfillStartOn
                        ? request.CoverageStartOn.Value
                        : existing.BackfillStartOn,
                    existing is null || request.CoverageEndOn > existing.BackfillEndOn
                        ? request.CoverageEndOn.Value
                        : existing.BackfillEndOn,
                    request.CoverageStartOn.Value,
                    request.CoverageEndOn.Value,
                    now);
            }
            else
            {
                syncState = existing! with
                {
                    LastRefreshStartOn = request.CoverageStartOn.Value,
                    LastRefreshEndOn = request.CoverageEndOn.Value,
                    LastSuccessfulRefreshAtUtc = now
                };
            }

            if (index >= 0)
            {
                syncStates[index] = syncState;
            }
            else
            {
                syncStates.Add(syncState);
            }

            await SaveTransactionLedgerAsync(records, syncStates, dayReconciliations, cancellationToken);
            return ToTransactionAccount(account, records, syncState, today);
        }
        finally
        {
            _lock.Release();
        }
    }
    public async Task<FinanceRecurringTransactionEntry?> AddRecurringTransactionAsync(
        FinanceRecurringTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = CleanFinanceText(request.AccountId);
        var description = CleanFinanceText(request.Description);
        var requestedCurrency = CleanFinanceText(request.Currency);
        var currency = requestedCurrency is null
            ? _currencies.GetDashboard().MasterCurrency
            : FinanceCurrencyService.NormalizeCurrencyCode(requestedCurrency);
        if (accountId is null || description is null || request.Amount is null || request.Amount == 0 || currency is null || request.NextOn is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = GetConfiguredAccounts().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase)
                && IsCashAccount(candidate));
            if (account is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var record = new FinanceRecurringTransactionRecord(
                $"recurring-{Guid.NewGuid():N}",
                null,
                account.Id,
                description,
                -Math.Abs(request.Amount.Value),
                currency,
                request.NextOn.Value.Day,
                request.NextOn.Value,
                "approved",
                "manual",
                0,
                null,
                null,
                now,
                now);
            var records = (LoadRecurringTransactionLedger().Records ?? Array.Empty<FinanceRecurringTransactionRecord>()).ToList();
            records.Add(record);
            await SaveRecurringTransactionLedgerAsync(records, cancellationToken);
            return ToRecurringTransactionEntry(record, account.Name, DateOnly.FromDateTime(DateTime.Now));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FinanceRecurringTransactionEntry?> UpdateRecurringTransactionAsync(
        string id,
        FinanceRecurringTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var cleanedId = CleanFinanceText(id);
        var accountId = CleanFinanceText(request.AccountId);
        var description = CleanFinanceText(request.Description);
        var requestedCurrency = CleanFinanceText(request.Currency);
        var currency = requestedCurrency is null
            ? null
            : FinanceCurrencyService.NormalizeCurrencyCode(requestedCurrency);
        if (cleanedId is null
            || accountId is null
            || description is null
            || request.Amount is null
            || request.Amount == 0
            || (requestedCurrency is not null && currency is null)
            || request.NextOn is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = GetConfiguredAccounts();
            var account = accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase)
                && IsCashAccount(candidate));
            if (account is null)
            {
                return null;
            }

            var stored = (LoadRecurringTransactionLedger().Records ?? Array.Empty<FinanceRecurringTransactionRecord>()).ToList();
            var dashboard = BuildRecurringTransactionsDashboard(
                LoadTransactionLedger().Records ?? Array.Empty<FinanceTransactionRecord>(),
                stored,
                accounts);
            var entry = dashboard.Records.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cleanedId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }
            currency ??= entry.EnteredCurrency;

            var index = stored.FindIndex(candidate =>
                string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
                || (entry.DetectionKey is not null
                    && string.Equals(candidate.DetectionKey, entry.DetectionKey, StringComparison.Ordinal)));
            var now = DateTimeOffset.UtcNow;
            var record = new FinanceRecurringTransactionRecord(
                entry.Id,
                entry.DetectionKey,
                account.Id,
                description,
                -Math.Abs(request.Amount.Value),
                currency,
                request.NextOn.Value.Day,
                request.NextOn.Value,
                entry.Status,
                entry.DetectionKey is null ? "manual" : "custom",
                entry.EvidenceCount,
                entry.FirstObservedOn,
                entry.LastObservedOn,
                index >= 0 ? stored[index].CreatedAtUtc : now,
                now);
            if (index >= 0)
            {
                stored[index] = record;
            }
            else
            {
                stored.Add(record);
            }

            await SaveRecurringTransactionLedgerAsync(stored, cancellationToken);
            return ToRecurringTransactionEntry(record, account.Name, DateOnly.FromDateTime(DateTime.Now));
        }
        finally
        {
            _lock.Release();
        }
    }
    public async Task<FinanceRecurringTransactionEntry?> UpdateRecurringTransactionStatusAsync(
        string id,
        string? requestedStatus,
        CancellationToken cancellationToken)
    {
        var cleanedId = CleanFinanceText(id);
        var status = NormalizeRecurringTransactionStatus(requestedStatus);
        if (cleanedId is null || status is null)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = GetConfiguredAccounts();
            var stored = (LoadRecurringTransactionLedger().Records ?? Array.Empty<FinanceRecurringTransactionRecord>()).ToList();
            var dashboard = BuildRecurringTransactionsDashboard(
                LoadTransactionLedger().Records ?? Array.Empty<FinanceTransactionRecord>(),
                stored,
                accounts);
            var entry = dashboard.Records.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cleanedId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var index = stored.FindIndex(candidate =>
                string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
                || (entry.DetectionKey is not null
                    && string.Equals(candidate.DetectionKey, entry.DetectionKey, StringComparison.Ordinal)));
            var record = new FinanceRecurringTransactionRecord(
                entry.Id,
                entry.DetectionKey,
                entry.AccountId,
                entry.Description,
                entry.Amount,
                entry.Currency,
                entry.DayOfMonth,
                entry.NextOn,
                status,
                entry.Source,
                entry.EvidenceCount,
                entry.FirstObservedOn,
                entry.LastObservedOn,
                index >= 0 ? stored[index].CreatedAtUtc : now,
                now);
            if (index >= 0)
            {
                stored[index] = record;
            }
            else
            {
                stored.Add(record);
            }

            await SaveRecurringTransactionLedgerAsync(stored, cancellationToken);
            var accountName = accounts.FirstOrDefault(account =>
                string.Equals(account.Id, record.AccountId, StringComparison.OrdinalIgnoreCase))?.Name ?? entry.AccountName;
            return ToRecurringTransactionEntry(record, accountName, DateOnly.FromDateTime(DateTime.Now));
        }
        finally
        {
            _lock.Release();
        }
    }
    public async Task<bool> RemoveRecurringTransactionAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var cleanedId = CleanFinanceText(id);
        if (cleanedId is null)
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var accounts = GetConfiguredAccounts();
            var stored = (LoadRecurringTransactionLedger().Records ?? Array.Empty<FinanceRecurringTransactionRecord>()).ToList();
            var dashboard = BuildRecurringTransactionsDashboard(
                LoadTransactionLedger().Records ?? Array.Empty<FinanceTransactionRecord>(),
                stored,
                accounts);
            var entry = dashboard.Records.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cleanedId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return false;
            }

            var index = stored.FindIndex(candidate =>
                string.Equals(candidate.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
                || (entry.DetectionKey is not null
                    && string.Equals(candidate.DetectionKey, entry.DetectionKey, StringComparison.Ordinal)));
            if (entry.DetectionKey is null)
            {
                if (index < 0)
                {
                    return false;
                }

                stored.RemoveAt(index);
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                var tombstone = new FinanceRecurringTransactionRecord(
                    entry.Id,
                    entry.DetectionKey,
                    entry.AccountId,
                    entry.Description,
                    entry.Amount,
                    entry.Currency,
                    entry.DayOfMonth,
                    entry.NextOn,
                    "removed",
                    entry.Source,
                    entry.EvidenceCount,
                    entry.FirstObservedOn,
                    entry.LastObservedOn,
                    index >= 0 ? stored[index].CreatedAtUtc : now,
                    now);
                if (index >= 0)
                {
                    stored[index] = tombstone;
                }
                else
                {
                    stored.Add(tombstone);
                }
            }

            await SaveRecurringTransactionLedgerAsync(stored, cancellationToken);
            return true;
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

    private FinanceAccountSnapshot ToFinanceAccount(FinanceAccountConfig config)
    {
        var owed = config.BalanceOwed;
        var available = config.CreditAvailable ?? (config.CreditLimit is not null && owed is not null ? config.CreditLimit - owed : null);
        var asOf = DateOnly.FromDateTime(DateTime.Now);
        var effectiveApr = config.PromotionalAprPercent is not null
                           && config.PromotionalAprEndsOn is not null
                           && asOf < config.PromotionalAprEndsOn
            ? config.PromotionalAprPercent
            : config.AprPercent;
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
            _credentialStore.Exists(config.Id),
            config.Collector,
            config.CashBalance,
            owed,
            config.CreditLimit,
            available,
            config.AprPercent,
            config.PromotionalAprPercent,
            config.PromotionalAprEndsOn,
            effectiveApr,
            config.MinimumPayment,
            config.PaymentDueDate,
            config.MinimumPaymentMet,
            config.CreditLimit is > 0 && owed is not null ? Math.Round(owed.Value / config.CreditLimit.Value * 100, 1) : null,
            status,
            message,
            config.CollectorNotes,
            config.Currency,
            config.LastUpdatedUtc);
    }

    private FinanceSnapshot ConvertSnapshotToMaster(
        FinanceSnapshot snapshot,
        IReadOnlyList<FinanceAccountConfig> configuredAccounts,
        string masterCurrency)
    {
        var configuredById = configuredAccounts.ToDictionary(account => account.Id, StringComparer.OrdinalIgnoreCase);
        var accounts = snapshot.Accounts.Select(account =>
        {
            configuredById.TryGetValue(account.Id, out var configured);
            var sourceCurrency = FinanceCurrencyService.NormalizeAccountCurrency(
                account.Currency ?? configured?.Currency,
                account.Name);
            return account with
            {
                CashBalance = ConvertNullable(account.CashBalance, sourceCurrency, masterCurrency),
                BalanceOwed = ConvertNullable(account.BalanceOwed, sourceCurrency, masterCurrency),
                CreditLimit = ConvertNullable(account.CreditLimit, sourceCurrency, masterCurrency),
                CreditAvailable = ConvertNullable(account.CreditAvailable, sourceCurrency, masterCurrency),
                MinimumPayment = ConvertNullable(account.MinimumPayment, sourceCurrency, masterCurrency),
                Currency = sourceCurrency
            };
        }).ToList();
        var totalCash = accounts.Where(account => account.Kind is "bank" or "cash" or "checking" or "savings")
            .Sum(account => account.CashBalance ?? 0);
        var totalDebt = accounts.Sum(account => account.BalanceOwed ?? 0);
        var totalCreditAvailable = accounts.Sum(account => account.CreditAvailable ?? 0);
        return snapshot with
        {
            Accounts = accounts,
            TotalCash = totalCash,
            TotalDebt = totalDebt,
            TotalCreditAvailable = totalCreditAvailable,
            NetAfterDebt = totalCash - totalDebt
        };
    }

    private FinanceIncomeDashboard ConvertIncomeToMaster(FinanceIncomeDashboard income, string masterCurrency) =>
        income with
        {
            Salary = income.Salary.Select(summary => summary with
            {
                LatestPayment = _currencies.Convert(summary.LatestPayment, summary.Currency, masterCurrency),
                TotalLast12Months = _currencies.Convert(summary.TotalLast12Months, summary.Currency, masterCurrency),
                Currency = masterCurrency
            }).ToList(),
            SalaryPayments = income.SalaryPayments.Select(entry => entry with
            {
                Amount = _currencies.Convert(entry.Amount, entry.Currency, masterCurrency),
                Currency = masterCurrency
            }).ToList(),
            Recent = income.Recent.Select(entry => entry with
            {
                Amount = _currencies.Convert(entry.Amount, entry.Currency, masterCurrency),
                Currency = masterCurrency
            }).ToList()
        };

    private FinanceSalaryPlanDashboard BuildSalaryPlanDashboard(
        FinanceSalaryPlanLedger ledger,
        string masterCurrency)
    {
        var salary = ledger.Salary is null
            ? null
            : new FinanceSalaryScheduleEntry(
                _currencies.Convert(ledger.Salary.Amount, ledger.Salary.Currency, masterCurrency),
                masterCurrency,
                ledger.Salary.Amount,
                ledger.Salary.Currency,
                ledger.Salary.Interval,
                ledger.Salary.NextOn);
        var bonuses = (ledger.Bonuses ?? Array.Empty<FinanceBonusRecord>())
            .OrderBy(bonus => bonus.PaidOn)
            .ThenBy(bonus => bonus.Description, StringComparer.OrdinalIgnoreCase)
            .Select(bonus => new FinanceBonusEntry(
                bonus.Id,
                bonus.Description,
                _currencies.Convert(bonus.Amount, bonus.Currency, masterCurrency),
                masterCurrency,
                bonus.Amount,
                bonus.Currency,
                bonus.PaidOn))
            .ToList();
        return new FinanceSalaryPlanDashboard(salary, bonuses);
    }

    private FinanceTransactionsDashboard ConvertTransactionsToMaster(FinanceTransactionsDashboard transactions, string masterCurrency) =>
        transactions with
        {
            Records = transactions.Records.Select(entry => entry with
            {
                Amount = _currencies.Convert(entry.Amount, entry.Currency, masterCurrency),
                Currency = masterCurrency
            }).ToList()
        };

    private FinanceRecurringTransactionsDashboard ConvertRecurringTransactionsToMaster(
        FinanceRecurringTransactionsDashboard recurring,
        string masterCurrency) =>
        recurring with
        {
            Records = recurring.Records.Select(entry => entry with
            {
                Amount = _currencies.Convert(entry.Amount, entry.Currency, masterCurrency),
                Currency = masterCurrency
            }).ToList()
        };
    private decimal? ConvertNullable(decimal? amount, string sourceCurrency, string masterCurrency) =>
        amount is null ? null : _currencies.Convert(amount.Value, sourceCurrency, masterCurrency);

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

    private static bool IsCashAccount(FinanceAccountConfig account) =>
        account.Kind is "bank" or "cash" or "checking" or "savings";

    private static bool IsTransactionAccount(FinanceAccountConfig account) =>
        account.Kind is not "credit_card" and not "loan";

    private static bool HasCompleteMonthlyReconciliationEvidence(
        IReadOnlyList<FinanceTransactionDayReconciliationRecord> reconciliations,
        string accountId,
        DateOnly coverageStartOn,
        DateOnly coverageEndOn,
        DateTimeOffset completedAfterUtc)
    {
        if (string.IsNullOrWhiteSpace(accountId) || coverageEndOn < coverageStartOn)
        {
            return false;
        }

        var month = new DateOnly(coverageStartOn.Year, coverageStartOn.Month, 1);
        while (month <= coverageEndOn)
        {
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var windowStart = coverageStartOn > month ? coverageStartOn : month;
            var windowEnd = coverageEndOn < monthEnd ? coverageEndOn : monthEnd;
            if (!reconciliations.Any(reconciliation =>
                    string.Equals(reconciliation.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                    && reconciliation.PostedOn >= windowStart
                    && reconciliation.PostedOn <= windowEnd
                    && reconciliation.CompletedAtUtc > completedAfterUtc))
            {
                return false;
            }

            month = month.AddMonths(1);
        }

        return true;
    }

    private static FinanceTransactionsDashboard BuildTransactionsDashboard(
        IReadOnlyList<FinanceTransactionRecord> records,
        IReadOnlyList<FinanceTransactionSyncRecord> syncStates,
        IReadOnlyList<FinanceAccountConfig> accounts)
    {
        var shownAccounts = accounts.Where(IsTransactionAccount).ToList();
        var accountNames = shownAccounts.ToDictionary(account => account.Id, account => account.Name, StringComparer.OrdinalIgnoreCase);
        var syncByAccount = syncStates.ToDictionary(sync => sync.AccountId, StringComparer.OrdinalIgnoreCase);
        var entries = records
            .Where(record => accountNames.ContainsKey(record.AccountId))
            .OrderByDescending(record => record.PostedOn)
            .ThenByDescending(record => record.TransactedOn)
            .ThenByDescending(record => record.LastSeenAtUtc)
            .Select(record => ToTransactionEntry(record, accountNames[record.AccountId]))
            .ToList();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var targets = shownAccounts
            .Select(account => ToTransactionAccount(
                account,
                records,
                syncByAccount.GetValueOrDefault(account.Id),
                today))
            .ToList();
        return new FinanceTransactionsDashboard(entries.Count, targets, entries);
    }

    private static FinanceTransactionAccount ToTransactionAccount(
        FinanceAccountConfig account,
        IReadOnlyList<FinanceTransactionRecord> records,
        FinanceTransactionSyncRecord? syncState,
        DateOnly today)
    {
        var accountRecords = records
            .Where(record => string.Equals(record.AccountId, account.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var initialBackfillComplete = syncState is not null;
        return new FinanceTransactionAccount(
            account.Id,
            account.Name,
            account.Institution,
            account.LoginUrl,
            accountRecords.Count,
            accountRecords.Count == 0 ? null : accountRecords.Min(record => record.PostedOn),
            accountRecords.Count == 0 ? null : accountRecords.Max(record => record.PostedOn),
            initialBackfillComplete,
            initialBackfillComplete ? "incremental" : "initial_backfill",
            initialBackfillComplete ? today.AddMonths(-1) : today.AddMonths(-24),
            today,
            syncState?.BackfillStartOn,
            syncState?.BackfillEndOn,
            syncState?.LastRefreshStartOn,
            syncState?.LastRefreshEndOn,
            syncState?.LastSuccessfulRefreshAtUtc,
            account.CollectorNotes);
    }

    private static FinanceTransactionEntry ToTransactionEntry(FinanceTransactionRecord record, string accountName) =>
        new(
            record.Id, record.AccountId, accountName, record.PostedOn, record.TransactedOn,
            record.Amount, record.Currency, record.Amount, record.Currency,
            record.Direction, record.Description, record.Merchant,
            record.Status, record.Reference, record.SourceTransactionId, record.Label,
            record.Labels ?? Array.Empty<string>(), record.Person, record.Notes);

    private static FinanceRecurringTransactionsDashboard BuildRecurringTransactionsDashboard(
        IReadOnlyList<FinanceTransactionRecord> transactions,
        IReadOnlyList<FinanceRecurringTransactionRecord> storedRecords,
        IReadOnlyList<FinanceAccountConfig> accounts)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var accountNames = accounts
            .Where(IsCashAccount)
            .ToDictionary(account => account.Id, account => account.Name, StringComparer.OrdinalIgnoreCase);
        var detected = InferRecurringTransactions(transactions, accounts, today);
        var storedByDetectionKey = storedRecords
            .Where(record => record.DetectionKey is not null)
            .GroupBy(record => record.DetectionKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(record => record.UpdatedAtUtc).First(), StringComparer.Ordinal);
        var merged = new List<FinanceRecurringTransactionRecord>();
        var consumedStoredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in detected)
        {
            if (candidate.DetectionKey is not null && storedByDetectionKey.TryGetValue(candidate.DetectionKey, out var decision))
            {
                if (decision.Source == "custom")
                {
                    merged.Add(decision);
                }
                else
                {
                    merged.Add(candidate with
                    {
                        Id = decision.Id,
                        Status = decision.Status,
                        CreatedAtUtc = decision.CreatedAtUtc,
                        UpdatedAtUtc = decision.UpdatedAtUtc
                    });
                }
                consumedStoredIds.Add(decision.Id);
            }
            else
            {
                merged.Add(candidate);
            }
        }

        merged.AddRange(storedRecords.Where(record =>
            !consumedStoredIds.Contains(record.Id)
            && accountNames.ContainsKey(record.AccountId)));
        var entries = merged
            .Where(record => record.Status is not ("rejected" or "removed"))
            .Select(record => ToRecurringTransactionEntry(
                record,
                accountNames.GetValueOrDefault(record.AccountId, "Unknown account"),
                today))
            .OrderBy(entry => entry.Status == "pending" ? 0 : entry.Status == "approved" ? 1 : 2)
            .ThenBy(entry => entry.NextOn)
            .ThenBy(entry => entry.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FinanceRecurringTransactionsDashboard(
            entries.Count,
            entries.Count(entry => entry.Status == "pending"),
            entries.Count(entry => entry.Status == "approved"),
            entries.Count(entry => entry.Status == "rejected"),
            entries);
    }

    private static IReadOnlyList<FinanceRecurringTransactionRecord> InferRecurringTransactions(
        IReadOnlyList<FinanceTransactionRecord> transactions,
        IReadOnlyList<FinanceAccountConfig> accounts,
        DateOnly today)
    {
        var cashAccounts = accounts.Where(IsCashAccount)
            .ToDictionary(account => account.Id, StringComparer.OrdinalIgnoreCase);
        var groups = transactions
            .Where(transaction => transaction.Amount < 0
                && string.Equals(transaction.Direction, "money_out", StringComparison.Ordinal)
                && !string.Equals(transaction.Status, "declined", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(transaction.Status, "reversed", StringComparison.OrdinalIgnoreCase)
                && cashAccounts.ContainsKey(transaction.AccountId))
            .Select(transaction => new
            {
                Transaction = transaction,
                Key = NormalizeRecurringMerchantKey(transaction.Merchant ?? transaction.Description)
            })
            .Where(item => item.Key is not null)
            .GroupBy(item => $"{item.Transaction.AccountId}:{item.Key}", StringComparer.Ordinal);
        var candidates = new List<FinanceRecurringTransactionRecord>();
        var now = DateTimeOffset.UtcNow;

        foreach (var group in groups)
        {
            var monthly = FindBestMonthlyTransactionSequence(group.Select(item => item.Transaction));
            if (monthly.Count < 3 || monthly[^1].PostedOn < today.AddMonths(-12))
            {
                continue;
            }

            var representative = monthly[^1];
            var amounts = monthly.Select(transaction => Math.Abs(transaction.Amount)).Order().ToList();
            var days = monthly.Select(transaction => transaction.PostedOn.Day).Order().ToList();
            var amount = amounts[amounts.Count / 2];
            var dayOfMonth = days[days.Count / 2];
            var merchantKey = NormalizeRecurringMerchantKey(representative.Merchant ?? representative.Description)!;
            var detectionKey = $"{representative.AccountId}:{merchantKey}";
            var nextOn = NextMonthlyOccurrence(today, dayOfMonth, null);
            candidates.Add(new FinanceRecurringTransactionRecord(
                CreateRecurringCandidateId(detectionKey),
                detectionKey,
                representative.AccountId,
                CleanFinanceText(representative.Merchant) ?? representative.Description,
                -amount,
                representative.Currency,
                dayOfMonth,
                nextOn,
                "pending",
                "detected",
                monthly.Count,
                monthly[0].PostedOn,
                monthly[^1].PostedOn,
                now,
                now));
        }

        return candidates;
    }

    private static IReadOnlyList<FinanceTransactionRecord> FindBestMonthlyTransactionSequence(
        IEnumerable<FinanceTransactionRecord> source)
    {
        var transactions = source
            .Where(transaction => Math.Abs(transaction.Amount) >= 2m)
            .OrderBy(transaction => transaction.PostedOn)
            .ThenBy(transaction => Math.Abs(transaction.Amount))
            .ToList();
        var chains = new List<List<FinanceTransactionRecord>>(transactions.Count);

        for (var index = 0; index < transactions.Count; index++)
        {
            var current = transactions[index];
            List<FinanceTransactionRecord>? bestPredecessor = null;
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previous = transactions[previousIndex];
                var monthGap = (current.PostedOn.Year - previous.PostedOn.Year) * 12
                    + current.PostedOn.Month - previous.PostedOn.Month;
                if (monthGap != 1
                    || Math.Abs(current.PostedOn.Day - previous.PostedOn.Day) > 8
                    || !RecurringAmountsMatch(current.Amount, previous.Amount))
                {
                    continue;
                }

                var candidate = chains[previousIndex];
                if (bestPredecessor is null || candidate.Count > bestPredecessor.Count)
                {
                    bestPredecessor = candidate;
                }
            }

            var chain = bestPredecessor is null
                ? new List<FinanceTransactionRecord>()
                : new List<FinanceTransactionRecord>(bestPredecessor);
            chain.Add(current);
            chains.Add(chain);
        }

        var best = chains
            .OrderByDescending(chain => chain.Count)
            .ThenByDescending(chain => chain[^1].PostedOn)
            .FirstOrDefault();
        return best is null ? Array.Empty<FinanceTransactionRecord>() : best;
    }

    private static bool RecurringAmountsMatch(decimal left, decimal right)
    {
        var leftAmount = Math.Abs(left);
        var rightAmount = Math.Abs(right);
        var tolerance = Math.Max(2m, Math.Max(leftAmount, rightAmount) * 0.35m);
        return Math.Abs(leftAmount - rightAmount) <= tolerance;
    }
    private static FinanceRecurringTransactionEntry ToRecurringTransactionEntry(
        FinanceRecurringTransactionRecord record,
        string accountName,
        DateOnly today) =>
        new(
            record.Id,
            record.DetectionKey,
            record.AccountId,
            accountName,
            record.Description,
            record.Amount,
            record.Currency,
            record.Amount,
            record.Currency,
            record.DayOfMonth,
            NextMonthlyOccurrence(today, record.DayOfMonth, record.NextOn),
            record.Status,
            record.Source,
            record.EvidenceCount,
            record.FirstObservedOn,
            record.LastObservedOn);

    private static DateOnly NextMonthlyOccurrence(DateOnly today, int dayOfMonth, DateOnly? anchor)
    {
        if (anchor is not null && anchor > today)
        {
            return anchor.Value;
        }

        var candidate = new DateOnly(today.Year, today.Month, Math.Min(dayOfMonth, DateTime.DaysInMonth(today.Year, today.Month)));
        if (candidate <= today)
        {
            var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
            candidate = new DateOnly(nextMonth.Year, nextMonth.Month, Math.Min(dayOfMonth, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month)));
        }
        return candidate;
    }

    private static string? NormalizeRecurringMerchantKey(string? value)
    {
        var cleaned = CleanFinanceText(value)?.ToUpperInvariant();
        if (cleaned is null)
        {
            return null;
        }

        cleaned = Regex.Replace(cleaned, @"\b(ACH|DEBIT|CARD|CHECKCARD|ONLINE|POS|PURCHASE|RECURRING|WITHDRAWAL)\b", " ");
        cleaned = Regex.Replace(cleaned, @"\b\d{2,}\b", " ");
        cleaned = Regex.Replace(cleaned, @"[^A-Z0-9]+", " ").Trim();
        return cleaned.Length < 3 ? null : cleaned;
    }

    private static string CreateRecurringCandidateId(string detectionKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(detectionKey));
        return $"recurring-detected-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string? NormalizeRecurringTransactionStatus(string? value) =>
        CleanFinanceText(value)?.ToLowerInvariant() switch
        {
            "approved" => "approved",
            "rejected" => "rejected",
            "pending" => "pending",
            _ => null
        };
    private void MigrateTransactionAmounts()
    {
        var ledger = FinanceDataFile.ReadOptionalJson<FinanceTransactionLedger>(_transactionsPath, LineJson);
        if (ledger is null)
        {
            return;
        }

        if (ledger.Version < 1 || ledger.Records is null)
        {
            throw new FinanceDataException("Finance data file 'transactions.json' has an invalid structure.");
        }

        var originalRecords = ledger.Records.ToList();
        var migratedRecords = originalRecords.Select(NormalizeStoredTransactionRecord).ToList();
        var dayReconciliations = ledger.DayReconciliations
            ?? Array.Empty<FinanceTransactionDayReconciliationRecord>();
        var originalSyncStates = ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>();
        var migratedSyncStates = ledger.Version < 6
            ? Array.Empty<FinanceTransactionSyncRecord>()
            : originalSyncStates
                .Where(syncState => HasCompleteMonthlyReconciliationEvidence(
                    dayReconciliations,
                    syncState.AccountId,
                    syncState.BackfillStartOn,
                    syncState.BackfillEndOn,
                    DateTimeOffset.MinValue))
                .ToArray();
        var changed = ledger.Version < 6
            || ledger.SyncStates is null
            || ledger.DayReconciliations is null
            || migratedSyncStates.Length != originalSyncStates.Count
            || originalRecords.Zip(migratedRecords).Any(pair => !HasSameTransactionContent(pair.First, pair.Second));
        if (!changed)
        {
            return;
        }

        var migratedLedger = new FinanceTransactionLedger(
            6,
            migratedRecords,
            migratedSyncStates,
            dayReconciliations);
        FinanceDataFile.WriteJsonAtomic(_transactionsPath, migratedLedger, IndentedJson);
    }

    private void MigrateAccountCurrencies()
    {
        var accounts = LoadUserAccounts();
        var migrated = accounts.Select(account => account with
        {
            Currency = FinanceCurrencyService.NormalizeAccountCurrency(account.Currency, account.Name)
        }).ToList();
        if (!accounts.Zip(migrated).Any(pair => !string.Equals(pair.First.Currency, pair.Second.Currency, StringComparison.Ordinal)))
        {
            return;
        }

        FinanceDataFile.WriteJsonAtomic(_accountsPath, migrated, IndentedJson);
    }

    private IReadOnlyList<FinanceAccountConfig> GetConfiguredAccounts() =>
        _settings.Accounts.Concat(LoadUserAccounts().Select(account => account.ToConfig())).ToList();

    private IReadOnlyList<UserFinanceAccountRecord> LoadUserAccounts()
    {
        return FinanceDataFile.ReadOptionalJson<List<UserFinanceAccountRecord>>(_accountsPath, LineJson)
            ?? new List<UserFinanceAccountRecord>();
    }

    private async Task SaveUserAccountsAsync(IReadOnlyList<UserFinanceAccountRecord> accounts, CancellationToken cancellationToken)
    {
        await FinanceDataFile.WriteJsonAtomicAsync(_accountsPath, accounts, IndentedJson, cancellationToken);
    }

    private FinanceIncomeLedger LoadIncomeLedger()
    {
        var ledger = FinanceDataFile.ReadOptionalJson<FinanceIncomeLedger>(_incomePath, LineJson);
        if (ledger is null)
        {
            return FinanceIncomeLedger.Empty;
        }

        return ledger.Version >= 1 && ledger.Records is not null
            ? ledger
            : throw new FinanceDataException("Finance data file 'income.json' has an invalid structure.");
    }

    private async Task SaveIncomeLedgerAsync(IReadOnlyList<FinanceIncomeRecord> records, CancellationToken cancellationToken)
    {
        var ledger = new FinanceIncomeLedger(1, records);
        await FinanceDataFile.WriteJsonAtomicAsync(_incomePath, ledger, IndentedJson, cancellationToken);
    }

    private FinanceSalaryPlanLedger LoadSalaryPlanLedger()
    {
        var ledger = FinanceDataFile.ReadOptionalJson<FinanceSalaryPlanLedger>(_salaryPlanPath, LineJson);
        if (ledger is null)
        {
            return FinanceSalaryPlanLedger.Empty;
        }

        return ledger.Version >= 1 && ledger.Bonuses is not null
            ? ledger
            : throw new FinanceDataException("Finance data file 'salary-plan.json' has an invalid structure.");
    }

    private async Task SaveSalaryPlanLedgerAsync(
        FinanceSalaryPlanLedger ledger,
        CancellationToken cancellationToken)
    {
        await FinanceDataFile.WriteJsonAtomicAsync(_salaryPlanPath, ledger, IndentedJson, cancellationToken);
    }

    private FinanceRecurringTransactionLedger LoadRecurringTransactionLedger()
    {
        var ledger = FinanceDataFile.ReadOptionalJson<FinanceRecurringTransactionLedger>(_recurringTransactionsPath, LineJson);
        if (ledger is null)
        {
            return FinanceRecurringTransactionLedger.Empty;
        }

        return ledger.Version >= 1 && ledger.Records is not null
            ? ledger
            : throw new FinanceDataException("Finance data file 'recurring-transactions.json' has an invalid structure.");
    }

    private async Task SaveRecurringTransactionLedgerAsync(
        IReadOnlyList<FinanceRecurringTransactionRecord> records,
        CancellationToken cancellationToken)
    {
        var ledger = new FinanceRecurringTransactionLedger(1, records);
        await FinanceDataFile.WriteJsonAtomicAsync(_recurringTransactionsPath, ledger, IndentedJson, cancellationToken);
    }
    private FinanceTransactionLedger LoadTransactionLedger()
    {
        var ledger = FinanceDataFile.ReadOptionalJson<FinanceTransactionLedger>(_transactionsPath, LineJson);
        if (ledger is null)
        {
            return FinanceTransactionLedger.Empty;
        }

        if (ledger.Version < 1 || ledger.Records is null)
        {
            throw new FinanceDataException("Finance data file 'transactions.json' has an invalid structure.");
        }

        return ledger with
        {
            Records = ledger.Records.Select(NormalizeStoredTransactionRecord).ToList(),
            SyncStates = ledger.SyncStates ?? Array.Empty<FinanceTransactionSyncRecord>(),
            DayReconciliations = ledger.DayReconciliations ?? Array.Empty<FinanceTransactionDayReconciliationRecord>()
        };
    }

    private async Task SaveTransactionLedgerAsync(
        IReadOnlyList<FinanceTransactionRecord> records,
        IReadOnlyList<FinanceTransactionSyncRecord> syncStates,
        IReadOnlyList<FinanceTransactionDayReconciliationRecord> dayReconciliations,
        CancellationToken cancellationToken)
    {
        var ledger = new FinanceTransactionLedger(6, records, syncStates, dayReconciliations);
        await FinanceDataFile.WriteJsonAtomicAsync(_transactionsPath, ledger, IndentedJson, cancellationToken);
    }

    private static bool TryBuildCredential(
        string? requestedUsername,
        string? requestedPassword,
        out FinanceCredential? credential)
    {
        var username = CleanFinanceText(requestedUsername);
        var hasPassword = requestedPassword is { Length: > 0 };
        if (username is null && !hasPassword)
        {
            credential = null;
            return true;
        }

        if (username is null || !hasPassword)
        {
            credential = null;
            return false;
        }

        credential = new FinanceCredential(username, requestedPassword!);
        return true;
    }

    private void RestoreCredential(string accountId, FinanceCredential? previousCredential)
    {
        if (previousCredential is null)
        {
            _credentialStore.Delete(accountId);
        }
        else
        {
            _credentialStore.Write(accountId, previousCredential);
        }
    }

    private static string? ValidateCompleteAccountValues(
        UserFinanceAccountRecord account,
        FinanceAccountValuesRequest request)
    {
        if (request.CreditLimit is < 0
            || request.CreditAvailable is < 0
            || request.AprPercent is < 0
            || request.MinimumPayment is < 0)
        {
            return "Credit limits, available credit, APR, and minimum payment cannot be negative.";
        }

        var bankLike = account.Kind is "bank" or "cash" or "checking" or "savings";
        if (bankLike)
        {
            return request.CashBalance is null
                ? "A complete bank-account refresh requires cashBalance."
                : null;
        }

        if (account.Kind is "credit_card" or "loan")
        {
            if (request.BalanceOwed is null
                || request.AprPercent is null
                || request.MinimumPayment is null
                || request.PaymentDueDate is null
                || request.MinimumPaymentMet is null
                || CleanFinanceText(request.CollectorNotes) is null)
            {
                return "A complete credit or loan refresh requires balanceOwed, aprPercent, minimumPayment, paymentDueDate, minimumPaymentMet, and collectorNotes.";
            }

            if (account.Kind == "credit_card"
                && request.CreditLimit is null
                && request.CreditAvailable is null)
            {
                return "A complete credit-card refresh requires creditLimit or creditAvailable.";
            }

            return null;
        }

        return request.CashBalance is null
               && request.BalanceOwed is null
               && request.CreditAvailable is null
            ? "A complete account refresh requires at least one current balance value."
            : null;
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

    private static string? NormalizeSalaryInterval(string? value) =>
        CleanFinanceText(value)?.ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "weekly" => "weekly",
            "biweekly" or "fortnightly" or "every_two_weeks" => "biweekly",
            "semimonthly" or "twice_monthly" => "semimonthly",
            "monthly" => "monthly",
            _ => null
        };

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

    private static string? NormalizeTransactionDirection(string? value) =>
        CleanFinanceText(value)?.ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "money_in" or "in" or "credit" or "deposit" => "money_in",
            "money_out" or "out" or "debit" or "withdrawal" => "money_out",
            _ => null
        };

    private static decimal NormalizeTransactionAmount(decimal amount, string direction)
    {
        var magnitude = Math.Abs(amount);
        return string.Equals(direction, "money_out", StringComparison.Ordinal) ? -magnitude : magnitude;
    }

    private static FinanceTransactionRecord NormalizeStoredTransactionRecord(FinanceTransactionRecord record)
    {
        var direction = NormalizeTransactionDirection(record.Direction) ?? record.Direction;
        var amount = NormalizeTransactionAmount(record.Amount, direction);
        var labels = NormalizeTransactionLabels(record.Labels, record.Label);
        return record with
        {
            Amount = amount,
            Direction = direction,
            Label = labels.FirstOrDefault(),
            Labels = labels,
            Notes = CleanFinanceText(record.Notes),
            Fingerprint = CreateTransactionFingerprint(
                record.AccountId,
                record.PostedOn,
                record.TransactedOn,
                amount,
                record.Currency,
                direction,
                record.Description,
                record.Merchant,
                record.Reference)
        };
    }

    private static IReadOnlyList<string> NormalizeTransactionLabels(
        IEnumerable<string>? labels,
        string? legacyLabel = null)
    {
        return (labels ?? Array.Empty<string>())
            .Append(legacyLabel)
            .Select(CleanFinanceText)
            .Where(label => label is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static bool LabelsEqual(IEnumerable<string>? left, IEnumerable<string>? right) =>
        (left ?? Array.Empty<string>()).SequenceEqual(
            right ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

    private static string NormalizeTransactionStatus(string? value)
    {
        var normalized = CleanFinanceText(value)?.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized is "pending" or "declined" or "reversed" ? normalized : "posted";
    }

    private NormalizedFinanceTransaction? NormalizeTransactionRequest(
        FinanceTransactionRequest request,
        string expectedAccountId,
        DateOnly? expectedPostedOn = null)
    {
        var accountId = CleanFinanceText(request.AccountId);
        var direction = NormalizeTransactionDirection(request.Direction);
        if (accountId is null
            || !string.Equals(accountId, expectedAccountId, StringComparison.OrdinalIgnoreCase)
            || request.PostedOn is null
            || (expectedPostedOn is not null && request.PostedOn != expectedPostedOn)
            || request.Amount is null
            || request.Amount == 0
            || direction is null)
        {
            return null;
        }

        var amount = NormalizeTransactionAmount(request.Amount.Value, direction);
        var currency = NormalizeIncomeCurrency(request.Currency, _settings.Currency);
        var description = CleanFinanceText(request.Description) ?? "Transaction";
        var merchant = CleanFinanceText(request.Merchant);
        var reference = CleanFinanceText(request.Reference);
        var labels = request.Labels is not null || CleanFinanceText(request.Label) is not null
            ? NormalizeTransactionLabels(request.Labels, request.Label)
            : null;
        return new NormalizedFinanceTransaction(
            request.PostedOn.Value,
            request.TransactedOn,
            amount,
            currency,
            direction,
            description,
            merchant,
            NormalizeTransactionStatus(request.Status),
            reference,
            CleanFinanceText(request.SourceTransactionId),
            labels,
            CleanFinanceText(request.Person),
            CleanFinanceText(request.Notes),
            request.ReplaceMetadata == true,
            CleanFinanceText(request.RecordId),
            CreateTransactionFingerprint(
                expectedAccountId,
                request.PostedOn.Value,
                request.TransactedOn,
                amount,
                currency,
                direction,
                description,
                merchant,
                reference));
    }

    private static int FindTransactionMatchIndex(
        IReadOnlyList<FinanceTransactionRecord> records,
        string accountId,
        NormalizedFinanceTransaction incoming,
        IReadOnlySet<int>? allowedIndices,
        int minimumScore,
        bool requireUniqueBest)
    {
        var bestIndex = -1;
        var bestScore = minimumScore - 1;
        var bestScoreCount = 0;
        for (var index = 0; index < records.Count; index++)
        {
            if (allowedIndices is not null && !allowedIndices.Contains(index))
            {
                continue;
            }

            var score = GetTransactionMatchScore(records[index], accountId, incoming);
            if (score < minimumScore)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestIndex = index;
                bestScore = score;
                bestScoreCount = 1;
            }
            else if (score == bestScore)
            {
                bestScoreCount++;
            }
        }

        return requireUniqueBest && bestScoreCount != 1 ? -1 : bestIndex;
    }

    private static int GetTransactionMatchScore(
        FinanceTransactionRecord existing,
        string accountId,
        NormalizedFinanceTransaction incoming)
    {
        if (!string.Equals(existing.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (incoming.RecordId is not null)
        {
            return string.Equals(existing.Id, incoming.RecordId, StringComparison.OrdinalIgnoreCase) ? 1000 : -1;
        }

        if (incoming.SourceTransactionId is not null && existing.SourceTransactionId is not null)
        {
            return string.Equals(
                incoming.SourceTransactionId,
                existing.SourceTransactionId,
                StringComparison.OrdinalIgnoreCase)
                ? 900
                : -1;
        }

        if (existing.PostedOn != incoming.PostedOn
            || existing.Amount != incoming.Amount
            || !string.Equals(existing.Currency, incoming.Currency, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Direction, incoming.Direction, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (string.Equals(existing.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal))
        {
            return 800;
        }

        if (incoming.Reference is not null
            && existing.Reference is not null
            && string.Equals(incoming.Reference, existing.Reference, StringComparison.OrdinalIgnoreCase))
        {
            return 700;
        }

        if (ReferenceAppearsInTransaction(incoming.Reference, existing)
            || ReferenceAppearsInTransaction(existing.Reference, incoming))
        {
            return 675;
        }

        if (GetLongTransactionTokens(existing.Description, existing.Reference, existing.SourceTransactionId)
            .Overlaps(GetLongTransactionTokens(
                incoming.Description,
                incoming.Reference,
                incoming.SourceTransactionId)))
        {
            return 650;
        }

        var score = 100;
        var existingDescription = NormalizeTransactionMatchText(existing.Description);
        var incomingDescription = NormalizeTransactionMatchText(incoming.Description);
        if (string.Equals(existingDescription, incomingDescription, StringComparison.Ordinal))
        {
            score += 400;
        }
        else
        {
            var similarity = GetTransactionTextSimilarity(existingDescription, incomingDescription);
            if (similarity >= 0.65)
            {
                score += 250 + (int)Math.Round(similarity * 100);
            }
        }

        if (existing.Merchant is not null
            && incoming.Merchant is not null
            && string.Equals(
                NormalizeTransactionMatchText(existing.Merchant),
                NormalizeTransactionMatchText(incoming.Merchant),
                StringComparison.Ordinal))
        {
            score += 50;
        }

        if (existing.TransactedOn is not null && existing.TransactedOn == incoming.TransactedOn)
        {
            score += 20;
        }

        return score;
    }

    private static bool ReferenceAppearsInTransaction(string? reference, FinanceTransactionRecord transaction) =>
        reference is not null
        && NormalizeTransactionMatchText(transaction.Description).Contains(
            NormalizeTransactionMatchText(reference),
            StringComparison.Ordinal);

    private static bool ReferenceAppearsInTransaction(string? reference, NormalizedFinanceTransaction transaction) =>
        reference is not null
        && NormalizeTransactionMatchText(transaction.Description).Contains(
            NormalizeTransactionMatchText(reference),
            StringComparison.Ordinal);

    private static HashSet<string> GetLongTransactionTokens(params string?[] values) =>
        Regex.Matches(string.Join(" ", values.Where(value => value is not null)), @"\d{6,}")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeTransactionMatchText(string? value) =>
        Regex.Replace((value?.Trim() ?? string.Empty).ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();

    private static double GetTransactionTextSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static int GetTransactionIdentityStrength(NormalizedFinanceTransaction transaction) =>
        transaction.RecordId is not null ? 5
        : transaction.SourceTransactionId is not null ? 4
        : transaction.Reference is not null ? 3
        : GetLongTransactionTokens(transaction.Description).Count > 0 ? 2
        : 1;

    private static bool HasDuplicateStableTransactionIdentity(
        IReadOnlyList<NormalizedFinanceTransaction> transactions) =>
        transactions
            .Where(transaction => transaction.RecordId is not null)
            .GroupBy(transaction => transaction.RecordId!, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1)
        || transactions
            .Where(transaction => transaction.SourceTransactionId is not null)
            .GroupBy(transaction => transaction.SourceTransactionId!, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);

    private static FinanceTransactionRecord CreateTransactionRecord(
        string accountId,
        NormalizedFinanceTransaction transaction,
        DateTimeOffset now) =>
        new(
            $"transaction-{Guid.NewGuid():N}", accountId, transaction.PostedOn, transaction.TransactedOn,
            transaction.Amount, transaction.Currency, transaction.Direction, transaction.Description,
            transaction.Merchant, transaction.Status, transaction.Reference, transaction.SourceTransactionId,
            transaction.Labels?.FirstOrDefault(), transaction.Labels ?? Array.Empty<string>(),
            transaction.Person, transaction.Notes, transaction.Fingerprint, now, now);

    private static FinanceTransactionRecord UpdateTransactionRecord(
        FinanceTransactionRecord existing,
        NormalizedFinanceTransaction transaction,
        DateTimeOffset now)
    {
        var existingLabels = NormalizeTransactionLabels(existing.Labels, existing.Label);
        var labels = transaction.ReplaceMetadata
            ? transaction.Labels ?? Array.Empty<string>()
            : transaction.Labels ?? existingLabels;
        return existing with
        {
            PostedOn = transaction.PostedOn,
            TransactedOn = transaction.TransactedOn,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Direction = transaction.Direction,
            Description = transaction.Description,
            Merchant = transaction.Merchant,
            Status = transaction.Status,
            Reference = transaction.Reference,
            SourceTransactionId = transaction.SourceTransactionId ?? existing.SourceTransactionId,
            Label = labels.FirstOrDefault(),
            Labels = labels,
            Person = transaction.ReplaceMetadata ? transaction.Person : transaction.Person ?? existing.Person,
            Notes = transaction.ReplaceMetadata ? transaction.Notes : transaction.Notes ?? existing.Notes,
            Fingerprint = transaction.Fingerprint,
            LastSeenAtUtc = now
        };
    }

    private static bool HasSameTransactionContent(
        FinanceTransactionRecord left,
        FinanceTransactionRecord right) =>
        left.PostedOn == right.PostedOn
        && left.TransactedOn == right.TransactedOn
        && left.Amount == right.Amount
        && string.Equals(left.Currency, right.Currency, StringComparison.Ordinal)
        && string.Equals(left.Direction, right.Direction, StringComparison.Ordinal)
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && string.Equals(left.Merchant, right.Merchant, StringComparison.Ordinal)
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && string.Equals(left.Reference, right.Reference, StringComparison.Ordinal)
        && string.Equals(left.SourceTransactionId, right.SourceTransactionId, StringComparison.Ordinal)
        && string.Equals(left.Label, right.Label, StringComparison.Ordinal)
        && LabelsEqual(left.Labels, right.Labels)
        && string.Equals(left.Person, right.Person, StringComparison.Ordinal)
        && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
        && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);

    private sealed record NormalizedFinanceTransaction(
        DateOnly PostedOn,
        DateOnly? TransactedOn,
        decimal Amount,
        string Currency,
        string Direction,
        string Description,
        string? Merchant,
        string Status,
        string? Reference,
        string? SourceTransactionId,
        IReadOnlyList<string>? Labels,
        string? Person,
        string? Notes,
        bool ReplaceMetadata,
        string? RecordId,
        string Fingerprint);

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

    private static string CreateTransactionFingerprint(
        string accountId,
        DateOnly postedOn,
        DateOnly? transactedOn,
        decimal amount,
        string currency,
        string direction,
        string description,
        string? merchant,
        string? reference)
    {
        static string Normalize(string? value) =>
            Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ").ToUpperInvariant();
        var input = string.Join(
            "\n",
            accountId.ToUpperInvariant(),
            (transactedOn ?? postedOn).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            amount.ToString("0.00", CultureInfo.InvariantCulture),
            currency,
            direction,
            Normalize(description),
            Normalize(merchant),
            Normalize(reference));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
    private IEnumerable<FinanceSnapshot> ReadSnapshots() =>
        FinanceDataFile.ReadJsonLines<FinanceSnapshot>(_snapshotsPath, LineJson);

    private IEnumerable<FinanceRefreshLog> ReadLogs() =>
        FinanceDataFile.ReadJsonLines<FinanceRefreshLog>(_logPath, LineJson);

    private static async Task AppendJsonLineAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await FinanceDataFile.AppendJsonLineAsync(path, value, LineJson, cancellationToken);
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
    private const string AccountRefreshPromptRelativePath = @"finance\refresh-prompt.txt";
    private const string TransactionRefreshPromptRelativePath = @"finance\refresh-transactions-prompt.txt";
    private const string CredentialLeaseEndpoint = "http://127.0.0.1:5137/api/finance/credential-lease";
    private static readonly JsonSerializerOptions PromptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string LoadFinanceRefreshPrompt(
        string repositoryRoot,
        string promptRelativePath,
        string workflowName)
    {
        var promptPath = Path.GetFullPath(Path.Combine(repositoryRoot, promptRelativePath));
        var financeDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "finance"));
        var financeDirectoryPrefix = financeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!promptPath.StartsWith(financeDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {workflowName} prompt path is outside the finance directory.");
        }

        if (!File.Exists(promptPath))
        {
            throw new InvalidOperationException($"The {workflowName} prompt file is missing: {promptPath}");
        }

        var prompt = File.ReadAllText(promptPath, Encoding.UTF8).Trim();
        return prompt.Length > 0
            ? prompt
            : throw new InvalidOperationException($"The {workflowName} prompt file is empty: {promptPath}");
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly FinanceCredentialLeaseStore _credentialLeases;
    private bool _sequenceActive;

    public CodexFinanceRefreshLauncher(FinanceCredentialLeaseStore credentialLeases)
    {
        _credentialLeases = credentialLeases;
    }

    public CodexRefreshLaunchResult StartAccounts(IReadOnlyList<FinanceAccountSnapshot> accounts)
    {
        lock (_sync)
        {
            if (_sequenceActive)
            {
                return AlreadyRunningResult();
            }

            PruneExitedProcessesLocked();
            if (_processes.Count > 0)
            {
                return AlreadyRunningResult();
            }

            if (accounts.Count == 0)
            {
                return new CodexRefreshLaunchResult(
                    false,
                    false,
                    null,
                    "No finance accounts are configured.",
                    null,
                    Array.Empty<CodexAccountLaunchResult>());
            }

            string repositoryRoot;
            string promptTemplate;
            try
            {
                repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
                promptTemplate = LoadFinanceRefreshPrompt(
                    repositoryRoot,
                    AccountRefreshPromptRelativePath,
                    "account refresh");
            }
            catch (Exception ex)
            {
                return new CodexRefreshLaunchResult(false, false, null, null, ex.Message);
            }

            var plans = new List<CodexLaunchPlan>(accounts.Count);
            var launches = new CodexAccountLaunchResult?[accounts.Count];
            for (var index = 0; index < accounts.Count; index++)
            {
                var account = accounts[index];
                try
                {
                    plans.Add(new CodexLaunchPlan(
                        index,
                        $"accounts:{account.Id}",
                        account.Id,
                        account.Name,
                        repositoryRoot,
                        BuildAccountPrompt(promptTemplate, account),
                        account.LoginUrl));
                }
                catch (Exception ex)
                {
                    launches[index] = new CodexAccountLaunchResult(
                        account.Id,
                        account.Name,
                        false,
                        null,
                        ex.Message);
                }
            }

            return StartSequentialWorkflowLocked("account-refresh", plans, launches);
        }
    }

    public CodexRefreshLaunchResult StartTransactions(IReadOnlyList<FinanceTransactionAccount> accounts)
    {
        lock (_sync)
        {
            if (_sequenceActive)
            {
                return AlreadyRunningResult();
            }

            PruneExitedProcessesLocked();
            if (_processes.Count > 0)
            {
                return AlreadyRunningResult();
            }

            if (accounts.Count == 0)
            {
                return new CodexRefreshLaunchResult(
                    false,
                    false,
                    null,
                    "No transaction accounts are configured.",
                    null,
                    Array.Empty<CodexAccountLaunchResult>());
            }

            string repositoryRoot;
            string promptTemplate;
            try
            {
                repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
                promptTemplate = LoadFinanceRefreshPrompt(
                    repositoryRoot,
                    TransactionRefreshPromptRelativePath,
                    "transaction refresh");
            }
            catch (Exception ex)
            {
                return new CodexRefreshLaunchResult(false, false, null, null, ex.Message);
            }

            var plans = new List<CodexLaunchPlan>(accounts.Count);
            var launches = new CodexAccountLaunchResult?[accounts.Count];
            for (var index = 0; index < accounts.Count; index++)
            {
                var account = accounts[index];
                try
                {
                    plans.Add(new CodexLaunchPlan(
                        index,
                        $"transactions:{account.AccountId}",
                        account.AccountId,
                        account.AccountName,
                        repositoryRoot,
                        BuildTransactionAccountPrompt(promptTemplate, account),
                        account.LoginUrl));
                }
                catch (Exception ex)
                {
                    launches[index] = new CodexAccountLaunchResult(
                        account.AccountId,
                        account.AccountName,
                        false,
                        null,
                        ex.Message);
                }
            }

            return StartSequentialWorkflowLocked("transaction", plans, launches);
        }
    }

    private CodexRefreshLaunchResult StartSequentialWorkflowLocked(
        string workflowName,
        IReadOnlyList<CodexLaunchPlan> plans,
        CodexAccountLaunchResult?[] launches)
    {
        CodexProcessHandle? firstHandle = null;
        CodexLaunchPlan? firstPlan = null;
        var firstPlanIndex = -1;
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            try
            {
                firstHandle = StartProcess(plan);
                firstPlan = plan;
                firstPlanIndex = index;
                _processes[plan.ProcessKey] = firstHandle.Process;
                launches[plan.Order] = new CodexAccountLaunchResult(
                    plan.AccountId,
                    plan.AccountName,
                    true,
                    firstHandle.Process.Id,
                    null);
                break;
            }
            catch (Exception ex)
            {
                launches[plan.Order] = new CodexAccountLaunchResult(
                    plan.AccountId,
                    plan.AccountName,
                    false,
                    null,
                    ex.Message);
            }
        }

        if (firstHandle is null || firstPlan is null)
        {
            var failedLaunches = launches.Where(launch => launch is not null).Select(launch => launch!).ToArray();
            var error = BuildLaunchError(failedLaunches);
            return new CodexRefreshLaunchResult(
                false,
                false,
                null,
                $"No Codex {workflowName} sessions could be started.",
                error,
                failedLaunches);
        }

        var remainingPlans = plans.Skip(firstPlanIndex + 1).ToArray();
        foreach (var plan in remainingPlans)
        {
            launches[plan.Order] = new CodexAccountLaunchResult(
                plan.AccountId,
                plan.AccountName,
                false,
                null,
                null,
                true);
        }

        _sequenceActive = true;
        var firstProcessId = firstHandle.Process.Id;
        _ = RunSequentialProcessesAsync(firstPlan, firstHandle, remainingPlans);

        var launchResults = launches.Select(launch => launch!).ToArray();
        var failedCount = launchResults.Count(launch => launch.Error is not null);
        var queuedCount = remainingPlans.Length;
        var message = queuedCount == 0
            ? $"Opened the Codex {workflowName} session for {firstPlan.AccountName}."
            : $"Opened the Codex {workflowName} session for {firstPlan.AccountName} and queued {queuedCount} more. Each account will run in its own Codex instance, one at a time.";
        if (failedCount > 0)
        {
            message = $"{message} {failedCount} account session(s) could not be prepared or started.";
        }

        return new CodexRefreshLaunchResult(
            true,
            false,
            firstProcessId,
            message,
            BuildLaunchError(launchResults),
            launchResults);
    }

    private static string? BuildLaunchError(IEnumerable<CodexAccountLaunchResult> launches)
    {
        var errors = launches
            .Where(launch => launch.Error is not null)
            .Select(launch => $"{launch.AccountName}: {launch.Error}")
            .ToArray();
        return errors.Length == 0 ? null : string.Join(" ", errors);
    }

    private CodexRefreshLaunchResult AlreadyRunningResult() =>
        new(
            false,
            true,
            _processes.Values.FirstOrDefault(process => !process.HasExited)?.Id,
            "A sequential Codex finance workflow is already running.",
            null);

    private static string BuildAccountPrompt(
        string promptTemplate,
        FinanceAccountSnapshot account)
    {
        var accountContext = new
        {
            accountId = account.Id,
            accountName = account.Name,
            accountKind = account.Kind,
            institution = account.Institution,
            loginUrl = account.LoginUrl,
            currency = account.Currency,
            collector = account.Collector,
            collectorNotes = account.CollectorNotes,
            currentValues = new
            {
                cashBalance = account.CashBalance,
                balanceOwed = account.BalanceOwed,
                creditLimit = account.CreditLimit,
                creditAvailable = account.CreditAvailable,
                minimumPayment = account.MinimumPayment,
                paymentDueDate = account.PaymentDueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                minimumPaymentMet = account.MinimumPaymentMet
            }
        };

        return string.Concat(
            promptTemplate,
            "\n\n## Assigned account (authoritative)\n\n",
            "```json\n",
            JsonSerializer.Serialize(accountContext, PromptJson),
            "\n```\n\n",
            "This JSON object is your only assigned account. The launcher-appended secure credential lease is restricted to this account. ",
            "Do not browse, collect, update notes, post values or income, or verify data for any other account.");
    }

    private static string BuildTransactionAccountPrompt(
        string promptTemplate,
        FinanceTransactionAccount account)
    {
        var accountContext = new
        {
            accountId = account.AccountId,
            accountName = account.AccountName,
            institution = account.Institution,
            loginUrl = account.LoginUrl,
            refreshMode = account.RefreshMode,
            requiredStartOn = account.RequiredStartOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            requiredEndOn = account.RequiredEndOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            initialBackfillComplete = account.InitialBackfillComplete,
            existingRecordCount = account.RecordCount,
            earliestStoredPostedOn = account.EarliestPostedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            latestStoredPostedOn = account.LatestPostedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            backfillStartOn = account.BackfillStartOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            backfillEndOn = account.BackfillEndOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lastRefreshStartOn = account.LastRefreshStartOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lastRefreshEndOn = account.LastRefreshEndOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        return string.Concat(
            promptTemplate,
            "\n\n## Assigned account (authoritative)\n\n",
            "```json\n",
            JsonSerializer.Serialize(accountContext, PromptJson),
            "\n```\n\n",
            "This JSON object is your only assigned account. Do not browse, collect, update notes, post transactions, or save a checkpoint for any other account.");
    }

    private CodexProcessHandle StartProcess(CodexLaunchPlan plan)
    {
        string? leaseToken = null;
        Process? process = null;
        try
        {
            var prompt = plan.Prompt;
            if (!string.IsNullOrWhiteSpace(plan.LoginUrl))
            {
                leaseToken = _credentialLeases.Create(plan.AccountId, plan.AccountName);
                prompt = AppendCredentialLeaseInstructions(prompt, plan.AccountId, leaseToken);
            }

            process = new Process
            {
                StartInfo = BuildStartInfo(plan.RepositoryRoot, prompt),
                EnableRaisingEvents = true
            };
            if (process.Start())
            {
                return new CodexProcessHandle(process, leaseToken);
            }

            throw new InvalidOperationException("Codex could not be started.");
        }
        catch
        {
            _credentialLeases.Revoke(leaseToken);
            process?.Dispose();
            throw;
        }
    }

    private static string AppendCredentialLeaseInstructions(string prompt, string accountId, string leaseToken)
    {
        var accountEndpoint = $"{CredentialLeaseEndpoint}/{Uri.EscapeDataString(accountId)}";
        return string.Concat(
            prompt,
            "\n\n## Secure credential lease (authoritative)\n\n",
            "The Finances app created a short-lived, in-memory lease restricted to the one assigned account. ",
            "Do not inspect `nodeRepl.env` and do not ask the user to restore environment forwarding. ",
            "For each editable credential field, validate the retained assigned-tab URL and exact locator first. ",
            "Then, inside that same short block-scoped browser-control call, POST to the applicable endpoint below, ",
            "passing the exact lease token in the `X-Finance-Credential-Lease` header:\n\n",
            "- Username endpoint: `", accountEndpoint, "/username`\n",
            "- Password endpoint: `", accountEndpoint, "/password`\n",
            "- Lease token: `", leaseToken, "`\n\n",
            "Use the response body only as the immediate input to the verified locator's `fill(...)` operation. ",
            "Check `response.ok`, keep the response and value block-scoped, and never print, return, log, persist, ",
            "or place the credential value in authored source. Redeem username and password separately, only when ",
            "their exact fields are ready. The endpoint independently rejects another account ID, expired leases, ",
            "and excessive retries. A different foreground tab or application is not a blocker.");
    }

    private static ProcessStartInfo BuildStartInfo(string repositoryRoot, string prompt)
    {
        var codexCommand = ResolveCodexCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = codexCommand.FileName,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        if (codexCommand.ScriptPath is not null)
        {
            startInfo.ArgumentList.Add(codexCommand.ScriptPath);
        }

        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("gpt-5.6-terra");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("model_reasoning_effort=\"high\"");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("service_tier=\"fast\"");
        startInfo.ArgumentList.Add("--ask-for-approval");
        startInfo.ArgumentList.Add("never");
        startInfo.ArgumentList.Add("--enable");
        startInfo.ArgumentList.Add("fast_mode");
        startInfo.ArgumentList.Add("--enable");
        startInfo.ArgumentList.Add("browser_use");
        startInfo.ArgumentList.Add("--enable");
        startInfo.ArgumentList.Add("browser_use_external");
        startInfo.ArgumentList.Add("--enable");
        startInfo.ArgumentList.Add("browser_use_full_cdp_access");
        startInfo.ArgumentList.Add("--sandbox");
        // The Windows workspace-write helper can fail before a finance refresh
        // gets to its first read. This user-initiated workflow must read the
        // local credential-backed finance store and drive the configured browser.
        startInfo.ArgumentList.Add("danger-full-access");
        // Non-interactive execution exits when this account finishes (or reports
        // a genuine blocker), allowing the sequential launcher to advance.
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add(prompt);

        return startInfo;
    }

    private async Task RunSequentialProcessesAsync(
        CodexLaunchPlan firstPlan,
        CodexProcessHandle firstHandle,
        IReadOnlyList<CodexLaunchPlan> remainingPlans)
    {
        try
        {
            await WaitForSequentialProcessAsync(firstPlan, firstHandle);
            foreach (var plan in remainingPlans)
            {
                CodexProcessHandle? handle = null;
                try
                {
                    handle = StartProcess(plan);
                    lock (_sync)
                    {
                        _processes[plan.ProcessKey] = handle.Process;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Could not start queued Codex session for '{plan.AccountName}': {ex.Message}");
                    continue;
                }

                await WaitForSequentialProcessAsync(plan, handle);
            }
        }
        finally
        {
            lock (_sync)
            {
                _sequenceActive = false;
            }
        }
    }

    private async Task WaitForSequentialProcessAsync(CodexLaunchPlan plan, CodexProcessHandle handle)
    {
        var process = handle.Process;
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not wait for the Codex session for '{plan.AccountName}': {ex.Message}");
        }
        finally
        {
            _credentialLeases.Revoke(handle.CredentialLeaseToken);
            lock (_sync)
            {
                if (_processes.TryGetValue(plan.ProcessKey, out var tracked)
                    && ReferenceEquals(tracked, process))
                {
                    _processes.Remove(plan.ProcessKey);
                }
            }

            process.Dispose();
        }
    }

    private void PruneExitedProcessesLocked()
    {
        foreach (var pair in _processes.Where(pair => pair.Value.HasExited).ToList())
        {
            _processes.Remove(pair.Key);
            pair.Value.Dispose();
        }
    }

    private sealed record CodexLaunchPlan(
        int Order,
        string ProcessKey,
        string AccountId,
        string AccountName,
        string RepositoryRoot,
        string Prompt,
        string? LoginUrl);
    private sealed record CodexProcessHandle(Process Process, string? CredentialLeaseToken);
    private sealed record CodexLaunchCommand(string FileName, string? ScriptPath);

    private static CodexLaunchCommand ResolveCodexCommand()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var configuredExtension = Path.GetExtension(configured);
            if (string.Equals(configuredExtension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexLaunchCommand(configured, null);
            }

            if (string.Equals(configuredExtension, ".js", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexLaunchCommand(ResolveNodeExecutable(Path.GetDirectoryName(configured)), configured);
            }

            if (string.Equals(configuredExtension, ".cmd", StringComparison.OrdinalIgnoreCase))
            {
                var configuredScript = Path.Combine(
                    Path.GetDirectoryName(configured) ?? string.Empty,
                    "node_modules",
                    "@openai",
                    "codex",
                    "bin",
                    "codex.js");
                if (File.Exists(configuredScript))
                {
                    return new CodexLaunchCommand(ResolveNodeExecutable(Path.GetDirectoryName(configured)), configuredScript);
                }
            }

            throw new InvalidOperationException(
                "CODEX_CLI_PATH must identify codex.exe, codex.js, or the npm codex.cmd shim with its sibling package.");
        }

        var npmDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");
        var npmCodexScript = Path.Combine(
            npmDirectory,
            "node_modules",
            "@openai",
            "codex",
            "bin",
            "codex.js");
        if (File.Exists(npmCodexScript))
        {
            return new CodexLaunchCommand(ResolveNodeExecutable(npmDirectory), npmCodexScript);
        }

        throw new InvalidOperationException("The installed Codex Node entry point could not be found.");
    }

    private static string ResolveNodeExecutable(string? npmDirectory)
    {
        var npmNode = string.IsNullOrWhiteSpace(npmDirectory)
            ? null
            : Path.Combine(npmDirectory, "node.exe");
        return npmNode is not null && File.Exists(npmNode) ? npmNode : "node.exe";
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


public sealed record FinanceAccountConfig(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    decimal? PromotionalAprPercent,
    DateOnly? PromotionalAprEndsOn,
    decimal? MinimumPayment,
    DateOnly? PaymentDueDate,
    bool? MinimumPaymentMet,
    string Collector,
    string? CollectorNotes,
    string Currency,
    DateTimeOffset? LastUpdatedUtc = null);

public sealed record UserFinanceAccountRecord(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    decimal? PromotionalAprPercent,
    DateOnly? PromotionalAprEndsOn,
    decimal? MinimumPayment,
    DateOnly? PaymentDueDate,
    bool? MinimumPaymentMet,
    string Collector,
    string? CollectorNotes,
    string? Currency,
    DateTimeOffset? LastUpdatedUtc = null)
{
    public FinanceAccountConfig ToConfig() =>
        new(Id, Name, Kind, Institution, LoginUrl, CashBalance, BalanceOwed, CreditLimit, CreditAvailable, AprPercent, PromotionalAprPercent, PromotionalAprEndsOn, MinimumPayment, PaymentDueDate, MinimumPaymentMet, Collector, CollectorNotes, FinanceCurrencyService.NormalizeAccountCurrency(Currency, Name), LastUpdatedUtc);
}

public sealed record FinanceAccountSnapshot(
    string Id,
    string Name,
    string Kind,
    string Institution,
    string? LoginUrl,
    bool CredentialsConfigured,
    string Collector,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    decimal? PromotionalAprPercent,
    DateOnly? PromotionalAprEndsOn,
    decimal? EffectiveAprPercent,
    decimal? MinimumPayment,
    DateOnly? PaymentDueDate,
    bool? MinimumPaymentMet,
    decimal? UtilizationPercent,
    string Status,
    string? Message,
    string? CollectorNotes,
    string? Currency,
    DateTimeOffset? LastUpdatedUtc = null);

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

public sealed record FinanceSalaryPlanLedger(
    int Version,
    FinanceSalaryScheduleRecord? Salary,
    IReadOnlyList<FinanceBonusRecord>? Bonuses)
{
    public static FinanceSalaryPlanLedger Empty { get; } = new(
        1,
        null,
        Array.Empty<FinanceBonusRecord>());
}

public sealed record FinanceSalaryScheduleRecord(
    decimal Amount,
    string Currency,
    string Interval,
    DateOnly NextOn,
    DateTimeOffset UpdatedAtUtc);

public sealed record FinanceBonusRecord(
    string Id,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly PaidOn);

public sealed record FinanceSalaryScheduleEntry(
    decimal Amount,
    string Currency,
    decimal EnteredAmount,
    string EnteredCurrency,
    string Interval,
    DateOnly NextOn);

public sealed record FinanceBonusEntry(
    string Id,
    string Description,
    decimal Amount,
    string Currency,
    decimal EnteredAmount,
    string EnteredCurrency,
    DateOnly PaidOn);

public sealed record FinanceSalaryPlanDashboard(
    FinanceSalaryScheduleEntry? Salary,
    IReadOnlyList<FinanceBonusEntry> Bonuses);

// Transactions use a separate versioned ledger. Amount is signed: money_in is
// positive and money_out is negative. Direction remains explicit. Multi-labels, person,
// and notes are user metadata that refreshes preserve without altering raw bank data.
public sealed record FinanceTransactionLedger(
    int Version,
    IReadOnlyList<FinanceTransactionRecord>? Records,
    IReadOnlyList<FinanceTransactionSyncRecord>? SyncStates,
    IReadOnlyList<FinanceTransactionDayReconciliationRecord>? DayReconciliations)
{
    public static FinanceTransactionLedger Empty { get; } = new(
        6,
        Array.Empty<FinanceTransactionRecord>(),
        Array.Empty<FinanceTransactionSyncRecord>(),
        Array.Empty<FinanceTransactionDayReconciliationRecord>());
}

public sealed record FinanceTransactionDayReconciliationRecord(
    string AccountId,
    DateOnly PostedOn,
    int ObservedCount,
    DateTimeOffset CompletedAtUtc);

public sealed record FinanceTransactionSyncRecord(
    string AccountId,
    DateTimeOffset InitialBackfillCompletedAtUtc,
    DateOnly BackfillStartOn,
    DateOnly BackfillEndOn,
    DateOnly LastRefreshStartOn,
    DateOnly LastRefreshEndOn,
    DateTimeOffset LastSuccessfulRefreshAtUtc);

public sealed record FinanceTransactionRecord(
    string Id,
    string AccountId,
    DateOnly PostedOn,
    DateOnly? TransactedOn,
    decimal Amount,
    string Currency,
    string Direction,
    string Description,
    string? Merchant,
    string Status,
    string? Reference,
    string? SourceTransactionId,
    string? Label,
    IReadOnlyList<string>? Labels,
    string? Person,
    string? Notes,
    string Fingerprint,
    DateTimeOffset FirstRecordedAtUtc,
    DateTimeOffset LastSeenAtUtc);

public sealed record FinanceTransactionEntry(
    string Id,
    string AccountId,
    string AccountName,
    DateOnly PostedOn,
    DateOnly? TransactedOn,
    decimal Amount,
    string Currency,
    decimal EnteredAmount,
    string EnteredCurrency,
    string Direction,
    string Description,
    string? Merchant,
    string Status,
    string? Reference,
    string? SourceTransactionId,
    string? Label,
    IReadOnlyList<string> Labels,
    string? Person,
    string? Notes);

public sealed record FinanceTransactionAccount(
    string AccountId,
    string AccountName,
    string Institution,
    string? LoginUrl,
    int RecordCount,
    DateOnly? EarliestPostedOn,
    DateOnly? LatestPostedOn,
    bool InitialBackfillComplete,
    string RefreshMode,
    DateOnly RequiredStartOn,
    DateOnly RequiredEndOn,
    DateOnly? BackfillStartOn,
    DateOnly? BackfillEndOn,
    DateOnly? LastRefreshStartOn,
    DateOnly? LastRefreshEndOn,
    DateTimeOffset? LastSuccessfulRefreshAtUtc,
    string? CollectorNotes);

public sealed record FinanceTransactionsDashboard(
    int RecordCount,
    IReadOnlyList<FinanceTransactionAccount> Accounts,
    IReadOnlyList<FinanceTransactionEntry> Records);
public sealed record FinanceRecurringTransactionLedger(
    int Version,
    IReadOnlyList<FinanceRecurringTransactionRecord>? Records)
{
    public static FinanceRecurringTransactionLedger Empty { get; } = new(
        1,
        Array.Empty<FinanceRecurringTransactionRecord>());
}

public sealed record FinanceRecurringTransactionRecord(
    string Id,
    string? DetectionKey,
    string AccountId,
    string Description,
    decimal Amount,
    string Currency,
    int DayOfMonth,
    DateOnly NextOn,
    string Status,
    string Source,
    int EvidenceCount,
    DateOnly? FirstObservedOn,
    DateOnly? LastObservedOn,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FinanceRecurringTransactionEntry(
    string Id,
    string? DetectionKey,
    string AccountId,
    string AccountName,
    string Description,
    decimal Amount,
    string Currency,
    decimal EnteredAmount,
    string EnteredCurrency,
    int DayOfMonth,
    DateOnly NextOn,
    string Status,
    string Source,
    int EvidenceCount,
    DateOnly? FirstObservedOn,
    DateOnly? LastObservedOn);

public sealed record FinanceRecurringTransactionsDashboard(
    int RecordCount,
    int PendingCount,
    int ApprovedCount,
    int RejectedCount,
    IReadOnlyList<FinanceRecurringTransactionEntry> Records);
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
    string? Error,
    IReadOnlyList<CodexAccountLaunchResult>? AccountLaunches = null);

public sealed record CodexAccountLaunchResult(
    string AccountId,
    string AccountName,
    bool Started,
    int? ProcessId,
    string? Error,
    bool Queued = false);

public sealed record FinanceCurrencyDashboard(
    string MasterCurrency,
    IReadOnlyList<string> SupportedCurrencies,
    DateTimeOffset? RatesLastUpdatedUtc,
    DateTimeOffset? RatesFetchedAtUtc,
    bool HasCachedRates,
    bool LastRefreshSucceeded,
    string? LastRefreshError,
    string AttributionUrl);

public sealed record FinanceCurrencyStoreRecord(
    int Version,
    string MasterCurrency,
    DateTimeOffset? RatesLastUpdatedUtc,
    DateTimeOffset? RatesFetchedAtUtc,
    IReadOnlyDictionary<string, decimal>? UsdRates);

public sealed record FinanceMasterCurrencyRequest(string? Currency);

public sealed record FinanceAccountCurrencyRequest(string? Currency);

public sealed record FinanceTaxProfileDashboard(
    string CountryCode,
    string? StateCode,
    string IncomeSource,
    bool Married,
    DateOnly SalaryStartOn);

public sealed record FinanceTaxProfileStoreRecord(
    int Version,
    string? CountryCode,
    string? StateCode,
    string? IncomeSource,
    bool? Married,
    DateOnly? SalaryStartOn);

public sealed record FinanceTaxProfileRequest(
    string? CountryCode,
    string? StateCode,
    string? IncomeSource,
    bool? Married);

public sealed record FinanceUiPreferencesDashboard(
    DateOnly? HistoryStartOn,
    DateOnly? HistoryEndOn,
    bool ProjectionEnabled,
    DateOnly? ProjectionStartOn,
    DateOnly? ProjectionOn,
    IReadOnlyList<string> HiddenValueSections);

public sealed record FinanceUiPreferencesStoreRecord(
    int Version,
    DateOnly? HistoryStartOn,
    DateOnly? HistoryEndOn,
    bool ProjectionEnabled,
    DateOnly? ProjectionStartOn,
    DateOnly? ProjectionOn,
    IReadOnlyList<string>? HiddenValueSections);

public sealed record FinanceUiPreferencesRequest(
    DateOnly? HistoryStartOn,
    DateOnly? HistoryEndOn,
    bool ProjectionEnabled,
    DateOnly? ProjectionStartOn,
    DateOnly? ProjectionOn,
    IReadOnlyList<string>? HiddenValueSections);
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
    FinanceSalaryPlanDashboard SalaryPlan,
    FinanceTransactionsDashboard Transactions,
    FinanceRecurringTransactionsDashboard RecurringTransactions,
    FinanceCurrencyDashboard CurrencySettings,
    FinanceTaxProfileDashboard TaxProfile,
    FinanceRefreshStatus Refresh);

public sealed record FinanceAccountRequest(
    string? Name,
    string? Kind,
    string? Institution,
    string? LoginUrl,
    string? Username,
    string? Password,
    decimal? CashBalance,
    decimal? BalanceOwed,
    decimal? CreditLimit,
    decimal? CreditAvailable,
    decimal? AprPercent,
    decimal? MinimumPayment,
    DateOnly? PaymentDueDate,
    bool? MinimumPaymentMet,
    string? CollectorNotes,
    string? Currency);

public sealed record FinanceAccountValuesRequest(
    [property: JsonRequired] decimal? CashBalance,
    [property: JsonRequired] decimal? BalanceOwed,
    [property: JsonRequired] decimal? CreditLimit,
    [property: JsonRequired] decimal? CreditAvailable,
    [property: JsonRequired] decimal? AprPercent,
    [property: JsonRequired] decimal? MinimumPayment,
    [property: JsonRequired] DateOnly? PaymentDueDate,
    [property: JsonRequired] bool? MinimumPaymentMet,
    [property: JsonRequired] string? CollectorNotes);

public sealed record FinanceAccountRefreshCompletionRequest(
    [property: JsonRequired] string? CompletionToken);

public sealed record FinanceAccountCredentialRequest(string? Username, string? Password);

public sealed record FinanceAccountCredentialResult(
    string AccountId,
    bool CredentialsConfigured);

public sealed record FinanceAccountAprRequest(
    [property: JsonRequired] decimal? AprPercent,
    [property: JsonRequired] decimal? PromotionalAprPercent,
    [property: JsonRequired] DateOnly? PromotionalAprEndsOn);

public enum FinanceAccountValuesUpdateStatus
{
    Updated,
    Invalid,
    NotFound
}

public sealed record FinanceAccountValuesUpdateResult(
    FinanceAccountValuesUpdateStatus Status,
    FinanceAccountSnapshot? Account,
    string? CompletionToken,
    string? Error)
{
    public static FinanceAccountValuesUpdateResult Updated(FinanceAccountSnapshot account, string completionToken) =>
        new(FinanceAccountValuesUpdateStatus.Updated, account, completionToken, null);

    public static FinanceAccountValuesUpdateResult Invalid(string error) =>
        new(FinanceAccountValuesUpdateStatus.Invalid, null, null, error);

    public static FinanceAccountValuesUpdateResult NotFound() =>
        new(FinanceAccountValuesUpdateStatus.NotFound, null, null, "Account not found or is read-only.");
}

public sealed record PendingAccountRefresh(
    string CompletionToken,
    UserFinanceAccountRecord Account);

public sealed record FinanceAccountNotesRequest(string? CollectorNotes);

public sealed record FinanceAccountNotesResult(
    string AccountId,
    string CollectorNotes);

public sealed record FinanceIncomeRequest(
    string? AccountId,
    DateOnly? PostedOn,
    decimal? Amount,
    string? Currency,
    string? Kind,
    string? Description,
    string? SourceTransactionId,
    string? RecordId);

public sealed record FinanceSalaryPlanRequest(
    decimal? Amount,
    string? Currency,
    string? Interval,
    DateOnly? NextOn,
    IReadOnlyList<FinanceBonusRequest>? Bonuses);

public sealed record FinanceBonusRequest(
    string? Id,
    string? Description,
    decimal? Amount,
    string? Currency,
    DateOnly? PaidOn);

public sealed record FinanceTransactionRequest(
    string? AccountId,
    DateOnly? PostedOn,
    DateOnly? TransactedOn,
    decimal? Amount,
    string? Currency,
    string? Direction,
    string? Description,
    string? Merchant,
    string? Status,
    string? Reference,
    string? SourceTransactionId,
    string? Label,
    string? Person,
    IReadOnlyList<string>? Labels,
    string? Notes,
    bool? ReplaceMetadata,
    string? RecordId);

public sealed record FinanceTransactionBulkLabelRequest(
    IReadOnlyList<string>? TransactionIds,
    string? Label);

public sealed record FinanceTransactionBulkLabelResult(
    int RequestedCount,
    int UpdatedCount,
    string Label);

public sealed record FinanceRecurringTransactionRequest(
    string? AccountId,
    string? Description,
    decimal? Amount,
    string? Currency,
    DateOnly? NextOn);

public sealed record FinanceRecurringTransactionStatusRequest(string? Status);
public sealed record FinanceTransactionDaySnapshotRequest(
    bool Complete,
    IReadOnlyList<FinanceTransactionRequest>? Transactions);

public sealed record FinanceTransactionDaySnapshotResult(
    string AccountId,
    DateOnly PostedOn,
    int ObservedCount,
    int InsertedCount,
    int UpdatedCount,
    int UnchangedCount,
    int RemovedCount,
    IReadOnlyList<FinanceTransactionEntry> Records);

public sealed record FinanceTransactionSyncRequest(
    string? Mode,
    DateOnly? CoverageStartOn,
    DateOnly? CoverageEndOn);
