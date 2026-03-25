using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class ChromiumManagedBrowser
{
    private const string InitialUrl = "https://www.youtube.com/playlist?list=WL";
    private static readonly TimeSpan DebugEndpointTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DebugEndpointRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> AuthCookieNames =
    [
        "SAPISID",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "SID",
        "SSID",
        "APISID",
        "HSID"
    ];

    private readonly YoutubeSyncPaths _paths;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = DebugEndpointRequestTimeout
    };

    public ChromiumManagedBrowser(YoutubeSyncPaths paths)
    {
        _paths = paths;
    }

    public bool Supports(BrowserCookieSource browser) =>
        ChromiumBrowserLocator.SupportsManagedProfile(browser);

    public async Task PrimeProfileAsync(
        BrowserCookieSource browser,
        string profile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Supports(browser))
        {
            throw new InvalidOperationException($"Browser '{browser}' is not supported for managed Chromium automation.");
        }

        profile = NormalizeProfile(profile);
        var browserName = DescribeBrowser(browser);
        var executablePath = ChromiumBrowserLocator.GetExecutablePath(browser);
        var userDataDir = GetManagedUserDataDir(browser);
        Directory.CreateDirectory(Path.Combine(userDataDir, profile));

        progress?.Report($"Opening managed {browserName} profile '{profile}' in normal mode.");
        progress?.Report("Sign into YouTube in the browser window that opens, then close that browser window when you are finished.");

        using var process = LaunchBrowser(
            executablePath,
            userDataDir,
            profile,
            debugPort: null,
            enableRemoteDebugging: false);

        using var cancellationRegistration = cancellationToken.Register(() => TryStopBrowser(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryStopBrowser(process);
            }
        }
    }

    public async Task<TResult> RunWithAuthenticatedSessionAsync<TResult>(
        BrowserCookieSource browser,
        string profile,
        IProgress<string>? progress,
        Func<ChromiumBrowserSession, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        if (!Supports(browser))
        {
            throw new InvalidOperationException($"Browser '{browser}' is not supported for managed Chromium automation.");
        }

        profile = NormalizeProfile(profile);
        var browserName = DescribeBrowser(browser);
        var executablePath = ChromiumBrowserLocator.GetExecutablePath(browser);
        var userDataDir = GetManagedUserDataDir(browser);
        Directory.CreateDirectory(Path.Combine(userDataDir, profile));

        var debugPort = GetFreePort();
        using var process = LaunchBrowser(
            executablePath,
            userDataDir,
            profile,
            debugPort,
            enableRemoteDebugging: true);
        using var cancellationRegistration = cancellationToken.Register(() => TryStopBrowser(process));

        try
        {
            progress?.Report($"Opened {browserName} for managed YouTube access. Waiting for the debugging endpoint...");
            await WaitForPageTargetAsync(debugPort, cancellationToken);

            var session = new ChromiumBrowserSession(this, debugPort, browserName);
            _ = await WaitForAuthenticatedCookiesAsync(session, progress, cancellationToken);
            return await action(session, cancellationToken);
        }
        finally
        {
            TryStopBrowser(process);
        }
    }

    internal async Task<IReadOnlyList<ChromiumCookie>> ReadCookiesAsync(int debugPort, CancellationToken cancellationToken)
    {
        var webSocketUrl = await GetPageWebSocketUrlAsync(debugPort, cancellationToken);
        await using var cdpClient = await ChromiumDevToolsClient.ConnectAsync(webSocketUrl, cancellationToken);
        var response = await cdpClient.SendCommandAsync("Storage.getCookies", null, cancellationToken);

        if (!response.TryGetProperty("result", out var result) || !result.TryGetProperty("cookies", out var cookiesElement))
        {
            return [];
        }

        var cookies = new List<ChromiumCookie>();
        foreach (var element in cookiesElement.EnumerateArray())
        {
            var domain = element.GetProperty("domain").GetString();
            var name = element.GetProperty("name").GetString();
            var value = element.GetProperty("value").GetString();
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name) || value is null)
            {
                continue;
            }

            cookies.Add(new ChromiumCookie(
                Domain: domain,
                Name: name,
                Value: value,
                Path: element.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? "/" : "/",
                Secure: element.TryGetProperty("secure", out var secureElement) && secureElement.GetBoolean(),
                Expires: ParseExpiry(element),
                HttpOnly: element.TryGetProperty("httpOnly", out var httpOnlyElement) && httpOnlyElement.GetBoolean()));
        }

        return cookies;
    }

    internal async Task NavigateAsync(int debugPort, string url, CancellationToken cancellationToken)
    {
        var webSocketUrl = await GetPageWebSocketUrlAsync(debugPort, cancellationToken);
        await using var cdpClient = await ChromiumDevToolsClient.ConnectAsync(webSocketUrl, cancellationToken);
        _ = await cdpClient.SendCommandAsync("Page.enable", null, cancellationToken);
        _ = await cdpClient.SendCommandAsync("Page.navigate", new { url }, cancellationToken);
    }

    internal async Task<T> EvaluateValueAsync<T>(int debugPort, string expression, CancellationToken cancellationToken)
    {
        var webSocketUrl = await GetPageWebSocketUrlAsync(debugPort, cancellationToken);
        await using var cdpClient = await ChromiumDevToolsClient.ConnectAsync(webSocketUrl, cancellationToken);
        _ = await cdpClient.SendCommandAsync("Runtime.enable", null, cancellationToken);
        var response = await cdpClient.SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                awaitPromise = true,
                returnByValue = true
            },
            cancellationToken);

        if (!response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("result", out var remoteObject)
            || !remoteObject.TryGetProperty("value", out var valueElement))
        {
            throw new InvalidOperationException("The browser did not return a value for the requested automation step.");
        }

        var value = valueElement.Deserialize<T>();
        if (value is null)
        {
            throw new InvalidOperationException("The browser returned an empty value for the requested automation step.");
        }

        return value;
    }

    private string GetManagedUserDataDir(BrowserCookieSource browser) =>
        Path.Combine(_paths.BrowserProfilesPath, DescribeBrowser(browser));

    private static string NormalizeProfile(string profile) =>
        string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();

    private static string DescribeBrowser(BrowserCookieSource browser) =>
        browser switch
        {
            BrowserCookieSource.Chrome => "chrome",
            BrowserCookieSource.Edge => "edge",
            _ => "chromium"
        };

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private Process LaunchBrowser(
        string executablePath,
        string userDataDir,
        string profile,
        int? debugPort,
        bool enableRemoteDebugging)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = _paths.RootPath,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };

        if (enableRemoteDebugging && debugPort is int port)
        {
            startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
            startInfo.ArgumentList.Add("--remote-allow-origins=*");
        }

        startInfo.ArgumentList.Add($"--user-data-dir={userDataDir}");
        startInfo.ArgumentList.Add($"--profile-directory={profile}");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(InitialUrl);

        var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to launch {Path.GetFileName(executablePath)}.");
        }

        return process;
    }

    private async Task WaitForPageTargetAsync(int debugPort, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DebugEndpointTimeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _ = await GetPageWebSocketUrlAsync(debugPort, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(500, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Timed out waiting for the Chromium debugging endpoint: {lastError?.Message}");
    }

    private async Task<IReadOnlyList<ChromiumCookie>> WaitForAuthenticatedCookiesAsync(
        ChromiumBrowserSession session,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SignInTimeout;
        var prompted = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ChromiumCookie> cookies;
            try
            {
                cookies = await session.ReadCookiesAsync(cancellationToken);
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            if (cookies.Any(cookie => AuthCookieNames.Contains(cookie.Name)))
            {
                return cookies;
            }

            if (!prompted)
            {
                progress?.Report($"Sign into YouTube in the app-managed {session.BrowserName} window that just opened. The action will continue automatically.");
                prompted = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for YouTube sign-in in the app-managed {session.BrowserName} profile. Keep the opened browser window, sign in there once, then retry.");
    }

    private async Task<string> GetPageWebSocketUrlAsync(int debugPort, CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync($"http://127.0.0.1:{debugPort}/json/list", cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        string? fallback = null;
        foreach (var target in document.RootElement.EnumerateArray())
        {
            if (!target.TryGetProperty("type", out var typeElement) || !string.Equals(typeElement.GetString(), "page", StringComparison.Ordinal))
            {
                continue;
            }

            if (!target.TryGetProperty("webSocketDebuggerUrl", out var wsElement))
            {
                continue;
            }

            var webSocketUrl = wsElement.GetString();
            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                continue;
            }

            fallback ??= webSocketUrl;
            var pageUrl = target.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
            if (pageUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                || pageUrl.Contains("google.com", StringComparison.OrdinalIgnoreCase))
            {
                return webSocketUrl;
            }
        }

        return fallback ?? throw new InvalidOperationException("No browser page target is available yet.");
    }

    private static double ParseExpiry(JsonElement element)
    {
        if (!element.TryGetProperty("expires", out var expiresElement))
        {
            return 0;
        }

        return expiresElement.ValueKind switch
        {
            JsonValueKind.Number when expiresElement.TryGetDouble(out var value) && double.IsFinite(value) => Math.Max(0, value),
            JsonValueKind.Null => 0,
            _ => 0
        };
    }

    private static void TryStopBrowser(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // Some Chromium launches do not create a traditional main window.
            }

            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Browser shutdown is best-effort.
        }
    }
}

internal sealed class ChromiumBrowserSession
{
    private readonly ChromiumManagedBrowser _browser;
    private readonly int _debugPort;

    internal ChromiumBrowserSession(ChromiumManagedBrowser browser, int debugPort, string browserName)
    {
        _browser = browser;
        _debugPort = debugPort;
        BrowserName = browserName;
    }

    public string BrowserName { get; }

    public Task<IReadOnlyList<ChromiumCookie>> ReadCookiesAsync(CancellationToken cancellationToken) =>
        _browser.ReadCookiesAsync(_debugPort, cancellationToken);

    public Task NavigateAsync(string url, CancellationToken cancellationToken) =>
        _browser.NavigateAsync(_debugPort, url, cancellationToken);

    public Task<T> EvaluateValueAsync<T>(string expression, CancellationToken cancellationToken) =>
        _browser.EvaluateValueAsync<T>(_debugPort, expression, cancellationToken);
}

internal readonly record struct ChromiumCookie(
    string Domain,
    string Name,
    string Value,
    string Path,
    bool Secure,
    double Expires,
    bool HttpOnly);
