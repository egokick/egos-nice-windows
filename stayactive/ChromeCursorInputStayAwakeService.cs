using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace StayActive;

internal sealed record ChromeCursorInputStatus(
    string Status,
    string? Detail,
    DateTimeOffset? UpdatedUtc)
{
    public static ChromeCursorInputStatus Unknown { get; } =
        new("unknown", null, null);
}

internal sealed class ChromeCursorInputStayAwakeService
{
    internal const string NativeHostName = "com.stayactive.chrome_cursor_input";
    internal static readonly TimeSpan RecentHeartbeatWindow = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "attached",
        "disabled",
        "detached",
        "error",
        "pulsed",
        "pulse_skipped",
        "waiting"
    };

    private static readonly HashSet<string> AllowedDetails = new(StringComparer.Ordinal)
    {
        "attach_failed",
        "background_pulse",
        "debugger_detached",
        "debugger_replaced",
        "dispatch_failed",
        "evaluation_failed",
        "invalid_state_message",
        "multiple_eligible_tabs",
        "native_host_unavailable",
        "native_state_disabled",
        "navigation",
        "no_eligible_tab",
        "scheduled_pulse",
        "state_timeout",
        "tab_closed",
        "tab_navigating",
        "target_changed",
        "target_foreground",
        "target_foreground_marker_unavailable",
        "target_foreground_visualized",
        "user_cancelled",
        "visual_marker_shown",
        "visual_marker_unavailable"
    };

    private static readonly object StatusFileLock = new();

    private readonly IChromeCursorInputRegistry _registry;
    private readonly IChromeCursorInputProcessLauncher _processLauncher;
    private readonly string _applicationExecutablePath;
    private readonly string _applicationBaseDirectory;
    private readonly string _stateDirectory;
    private readonly string? _extensionDirectoryOverride;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Lazy<ChromeExtensionIdentity> _extensionIdentity;

    public ChromeCursorInputStayAwakeService()
        : this(
            new CurrentUserChromeCursorInputRegistry(),
            new GoogleChromeCursorInputProcessLauncher(),
            Application.ExecutablePath,
            AppContext.BaseDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StayActive",
                "chrome-cursor-input"))
    {
    }

    internal ChromeCursorInputStayAwakeService(
        IChromeCursorInputRegistry registry,
        IChromeCursorInputProcessLauncher processLauncher,
        string applicationExecutablePath,
        string applicationBaseDirectory,
        string stateDirectory,
        string? extensionDirectoryOverride = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _applicationExecutablePath = Path.GetFullPath(
            applicationExecutablePath ?? throw new ArgumentNullException(nameof(applicationExecutablePath)));
        _applicationBaseDirectory = Path.GetFullPath(
            applicationBaseDirectory ?? throw new ArgumentNullException(nameof(applicationBaseDirectory)));
        _stateDirectory = Path.GetFullPath(
            stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory)));
        _extensionDirectoryOverride = string.IsNullOrWhiteSpace(extensionDirectoryOverride)
            ? null
            : Path.GetFullPath(extensionDirectoryOverride);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _extensionIdentity = new Lazy<ChromeExtensionIdentity>(
            ResolveExtensionIdentity,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ExtensionDirectory => _extensionIdentity.Value.Directory;

    public string ExtensionId => _extensionIdentity.Value.Id;

    internal string ExtensionOrigin => $"chrome-extension://{ExtensionId}/";

    internal string NativeHostManifestPath =>
        Path.Combine(_stateDirectory, $"{NativeHostName}.json");

    internal string StatusPath => Path.Combine(_stateDirectory, "status.json");

    internal string HeartbeatPath => Path.Combine(_stateDirectory, "heartbeat.json");

    public DateTimeOffset? LastHeartbeatUtc
    {
        get
        {
            lock (StatusFileLock)
            {
                return ReadLastHeartbeatUtcCore();
            }
        }
    }

    public bool HasRecentHeartbeat
    {
        get
        {
            var heartbeatUtc = LastHeartbeatUtc;
            if (heartbeatUtc is null)
            {
                return false;
            }

            var age = _utcNow() - heartbeatUtc.Value;
            return age >= TimeSpan.Zero && age <= RecentHeartbeatWindow;
        }
    }

    public void EnsureRegistered()
    {
        var identity = _extensionIdentity.Value;
        Directory.CreateDirectory(_stateDirectory);

        var nativeHostManifest = new NativeHostManifest(
            NativeHostName,
            "StayActive Chrome cursor input bridge",
            _applicationExecutablePath,
            "stdio",
            [$"chrome-extension://{identity.Id}/"]);
        var json = JsonSerializer.Serialize(nativeHostManifest, JsonOptions) + Environment.NewLine;
        WriteTextAtomicallyIfChanged(NativeHostManifestPath, json);

        _registry.RegisterGoogleChromeNativeMessagingHost(
            NativeHostName,
            NativeHostManifestPath);
    }

    public ChromeCursorInputStatus GetLastStatus()
    {
        lock (StatusFileLock)
        {
            return ReadLastStatusCore();
        }
    }

    public void OpenExtensionSetup()
    {
        EnsureRegistered();
        _processLauncher.OpenGoogleChromeExtensionsPage();
        _processLauncher.SelectFileInExplorer(
            Path.Combine(ExtensionDirectory, "manifest.json"));
    }

    internal bool TryRecordStatus(string status, string? detail)
    {
        if (!AllowedStatuses.Contains(status))
        {
            return false;
        }

        // Heartbeat and status are intentionally separate. Repeated native
        // messages prove liveness without changing the last status event.
        TryTouchHeartbeat();

        var privacySafeDetail = detail is not null && AllowedDetails.Contains(detail)
            ? detail
            : null;
        var record = new ChromeCursorInputStatus(status, privacySafeDetail, _utcNow());
        var json = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;

        lock (StatusFileLock)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                WriteTextAtomically(StatusPath, json);
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryTouchHeartbeat()
    {
        lock (StatusFileLock)
        {
            try
            {
                var record = new NativeMessageHeartbeat(_utcNow());
                var json = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
                Directory.CreateDirectory(_stateDirectory);
                WriteTextAtomically(HeartbeatPath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static string DeriveExtensionIdFromManifest(string manifestPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(manifestPath, Encoding.UTF8),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(keyElement.GetString()))
        {
            throw new InvalidDataException(
                "The Chrome cursor input extension manifest must contain a stable public 'key'.");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(keyElement.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The Chrome cursor input extension manifest contains an invalid public 'key'.",
                exception);
        }

        if (publicKey.Length == 0)
        {
            throw new InvalidDataException(
                "The Chrome cursor input extension manifest contains an empty public 'key'.");
        }

        var digest = SHA256.HashData(publicKey);
        var id = new char[32];
        for (var index = 0; index < 16; index++)
        {
            id[index * 2] = (char)('a' + (digest[index] >> 4));
            id[(index * 2) + 1] = (char)('a' + (digest[index] & 0x0f));
        }

        return new string(id);
    }

    private ChromeExtensionIdentity ResolveExtensionIdentity()
    {
        foreach (var directory in EnumerateExtensionDirectoryCandidates())
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var id = DeriveExtensionIdFromManifest(manifestPath);
                return new ChromeExtensionIdentity(Path.GetFullPath(directory), id);
            }
            catch (InvalidDataException) when (_extensionDirectoryOverride is null)
            {
                // A similarly named development directory is not this extension.
            }
            catch (JsonException) when (_extensionDirectoryOverride is null)
            {
                // Keep looking for the bundled extension if a dev copy is incomplete.
            }
            catch (IOException) when (_extensionDirectoryOverride is null)
            {
                // Keep looking for the bundled extension if a dev copy is temporarily unavailable.
            }
        }

        throw new DirectoryNotFoundException(
            "The bundled Chrome cursor input extension could not be found. "
            + "Reinstall StayActive or run it from the repository build output.");
    }

    private IEnumerable<string> EnumerateExtensionDirectoryCandidates()
    {
        if (_extensionDirectoryOverride is not null)
        {
            yield return _extensionDirectoryOverride;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateCandidateRoots(_applicationBaseDirectory))
        {
            foreach (var relativePath in ExtensionRelativePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var level = 0; directory is not null && level < 7; level++, directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }

    private static readonly string[] ExtensionRelativePaths =
    [
        "ChromeCursorInputExtension",
        "ChromeCursorInputStayAwakeExtension",
        "chrome-cursor-input-extension",
        "chrome-cursor-input-stay-awake",
        Path.Combine("extensions", "chrome-cursor-input-stay-awake"),
        Path.Combine("stayactive", "ChromeCursorInputExtension"),
        Path.Combine("stayactive", "ChromeCursorInputStayAwakeExtension"),
        Path.Combine("stayactive", "chrome-cursor-input-extension"),
        Path.Combine("stayactive", "chrome-cursor-input-stay-awake"),
        Path.Combine("stayactive", "extensions", "chrome-cursor-input-stay-awake")
    ];

    private static void WriteTextAtomicallyIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path)
                && string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch
        {
            // A replacement write below is the authoritative operation.
        }

        WriteTextAtomically(path, content);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The destination must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup; never replace a successful atomic write with a cleanup failure.
            }
        }
    }

    private sealed record ChromeExtensionIdentity(string Directory, string Id);

    private sealed record NativeMessageHeartbeat(DateTimeOffset HeartbeatUtc);

    private ChromeCursorInputStatus ReadLastStatusCore()
    {
        try
        {
            if (!File.Exists(StatusPath))
            {
                return ChromeCursorInputStatus.Unknown;
            }

            var status = JsonSerializer.Deserialize<ChromeCursorInputStatus>(
                File.ReadAllText(StatusPath, Encoding.UTF8),
                JsonOptions);
            if (status is null || !AllowedStatuses.Contains(status.Status))
            {
                return ChromeCursorInputStatus.Unknown;
            }

            var detail = status.Detail is not null && AllowedDetails.Contains(status.Detail)
                ? status.Detail
                : null;
            return status with { Detail = detail };
        }
        catch
        {
            return ChromeCursorInputStatus.Unknown;
        }
    }

    private DateTimeOffset? ReadLastHeartbeatUtcCore()
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<NativeMessageHeartbeat>(
                File.ReadAllText(HeartbeatPath, Encoding.UTF8),
                JsonOptions)?.HeartbeatUtc;
        }
        catch
        {
            return null;
        }
    }

    private sealed record NativeHostManifest(
        string Name,
        string Description,
        string Path,
        string Type,
        [property: JsonPropertyName("allowed_origins")]
        string[] AllowedOrigins);
}

internal static class ChromeCursorInputNativeMessagingHost
{
    internal const int MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions ProtocolJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryRun(string[] args)
    {
        ChromeCursorInputStayAwakeService service;
        try
        {
            service = new ChromeCursorInputStayAwakeService();
            if (args.Length == 0
                || !string.Equals(args[0], service.ExtensionOrigin, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            SetStandardStreamsToBinaryMode();
            Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), service);
        }
        catch
        {
            // Native messaging stdout is a framed protocol. Never write diagnostics to it.
        }

        return true;
    }

    private static void SetStandardStreamsToBinaryMode()
    {
        const int standardInputFileDescriptor = 0;
        const int standardOutputFileDescriptor = 1;
        const int binaryMode = 0x8000;

        if (SetMode(standardInputFileDescriptor, binaryMode) == -1
            || SetMode(standardOutputFileDescriptor, binaryMode) == -1)
        {
            throw new InvalidOperationException(
                "The Chrome native messaging streams could not be switched to binary mode.");
        }
    }

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "_setmode")]
    private static extern int SetMode(int fileDescriptor, int mode);

    internal static void Run(
        Stream input,
        Stream output,
        ChromeCursorInputStayAwakeService service)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(service);

        while (TryReadMessage(input, out var message))
        {
            using (message)
            {
                if (message.RootElement.ValueKind != JsonValueKind.Object
                    || !message.RootElement.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    WriteMessage(output, new { type = "error", code = "invalid-message" });
                    continue;
                }

                switch (typeElement.GetString())
                {
                    case "getState":
                        service.TryTouchHeartbeat();
                        WriteMessage(
                            output,
                            new
                            {
                                type = "state",
                                enabled = SettingsStore.Load().ChromeCursorInputStayAwakeEnabled
                            });
                        break;

                    case "status":
                        HandleStatusMessage(message.RootElement, service);
                        break;

                    default:
                        WriteMessage(output, new { type = "error", code = "unsupported-message" });
                        break;
                }
            }
        }
    }

    private static void HandleStatusMessage(
        JsonElement message,
        ChromeCursorInputStayAwakeService service)
    {
        if (!message.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? detail = null;
        if (message.TryGetProperty("detail", out var detailElement))
        {
            if (detailElement.ValueKind == JsonValueKind.String)
            {
                detail = detailElement.GetString();
            }
            else if (detailElement.ValueKind != JsonValueKind.Null)
            {
                return;
            }
        }

        var status = statusElement.GetString()!;
        service.TryRecordStatus(status, detail);
    }

    private static bool TryReadMessage(Stream input, out JsonDocument message)
    {
        message = null!;
        Span<byte> header = stackalloc byte[sizeof(int)];
        var headerBytesRead = ReadAtMost(input, header);
        if (headerBytesRead == 0)
        {
            return false;
        }

        if (headerBytesRead != header.Length)
        {
            throw new InvalidDataException("The native messaging frame header is incomplete.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The native messaging frame length is invalid.");
        }

        var payload = new byte[length];
        if (ReadAtMost(input, payload) != payload.Length)
        {
            throw new InvalidDataException("The native messaging frame payload is incomplete.");
        }

        message = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        return true;
    }

    private static int ReadAtMost(Stream input, Span<byte> buffer)
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = input.Read(buffer[totalBytesRead..]);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }

    private static void WriteMessage<T>(Stream output, T message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The native messaging response is too large.");
        }

        Span<byte> header = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }
}

internal interface IChromeCursorInputRegistry
{
    void RegisterGoogleChromeNativeMessagingHost(string hostName, string manifestPath);
}

internal sealed class CurrentUserChromeCursorInputRegistry : IChromeCursorInputRegistry
{
    private const string GoogleChromeNativeMessagingHostsRegistryPath =
        @"Software\Google\Chrome\NativeMessagingHosts";

    public void RegisterGoogleChromeNativeMessagingHost(string hostName, string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"{GoogleChromeNativeMessagingHostsRegistryPath}\{hostName}",
            writable: true)
            ?? throw new InvalidOperationException(
                "The Google Chrome native messaging registry key could not be created.");
        key.SetValue(null, manifestPath, RegistryValueKind.String);
    }
}

internal interface IChromeCursorInputProcessLauncher
{
    void OpenGoogleChromeExtensionsPage();

    void SelectFileInExplorer(string path);
}

internal sealed class GoogleChromeCursorInputProcessLauncher : IChromeCursorInputProcessLauncher
{
    public void OpenGoogleChromeExtensionsPage()
    {
        var chromePath = FindGoogleChromeExecutable()
            ?? throw new FileNotFoundException(
                "Google Chrome is not installed for this Windows user.");

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("chrome://extensions/");
        Process.Start(startInfo);
    }

    public void SelectFileInExplorer(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        Process.Start(startInfo);
    }

    internal static string? FindGoogleChromeExecutable()
    {
        foreach (var candidate in EnumerateGoogleChromeExecutableCandidates())
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate.Trim('"'));
                if (File.Exists(fullPath)
                    && string.Equals(Path.GetFileName(fullPath), "chrome.exe", StringComparison.OrdinalIgnoreCase)
                    && IsGoogleChromeExecutable(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore malformed or inaccessible installation records.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateGoogleChromeExecutableCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                writable: false);
            if (key?.GetValue(null) is string registeredPath
                && !string.IsNullOrWhiteSpace(registeredPath))
            {
                yield return registeredPath;
            }
        }
    }

    private static bool IsGoogleChromeExecutable(string path)
    {
        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPath.Contains(
                $"{Path.DirectorySeparatorChar}Google{Path.DirectorySeparatorChar}Chrome{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        return string.Equals(versionInfo.ProductName, "Google Chrome", StringComparison.OrdinalIgnoreCase);
    }
}
