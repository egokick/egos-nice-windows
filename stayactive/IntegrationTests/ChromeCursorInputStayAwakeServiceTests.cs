using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using StayActive;

namespace stayactive.IntegrationTests;

public sealed class ChromeCursorInputStayAwakeServiceTests
{
    [Fact]
    public void AppSettings_DefaultCloneAndJsonRoundTrip_PreserveFeatureState()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.ChromeCursorInputStayAwakeEnabled);

        var settings = new AppSettings
        {
            ChromeCursorInputStayAwakeEnabled = true
        };

        var clone = settings.Clone();
        Assert.True(clone.ChromeCursorInputStayAwakeEnabled);

        clone.ChromeCursorInputStayAwakeEnabled = false;
        Assert.True(settings.ChromeCursorInputStayAwakeEnabled);

        var roundTripped = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings));
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.ChromeCursorInputStayAwakeEnabled);
    }

    [Fact]
    public void DeriveExtensionIdFromManifest_UsesChromePublicKeyAlgorithm()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var manifestPath = Path.Combine(temporaryDirectory.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            """
            {
              "manifest_version": 3,
              "key": "AQIDBA=="
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var extensionId =
            ChromeCursorInputStayAwakeService.DeriveExtensionIdFromManifest(manifestPath);

        Assert.Equal("jpgekhehobljhpbdbpkllgleehcjgmjl", extensionId);
        Assert.Equal(32, extensionId.Length);
        Assert.All(extensionId, character => Assert.InRange(character, 'a', 'p'));
    }

    [Fact]
    public void EnsureRegistered_WritesOneOriginManifestAndUsesGoogleChromeRegistryAbstraction()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var stateDirectory = Path.Combine(temporaryDirectory.Path, "state");
        var executablePath = Path.Combine(temporaryDirectory.Path, "StayActive.exe");
        var registry = new RecordingRegistry();
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(
            registry,
            launcher,
            executablePath,
            stateDirectory,
            extensionDirectory);

        service.EnsureRegistered();

        var registration = Assert.Single(registry.Registrations);
        Assert.Equal(ChromeCursorInputStayAwakeService.NativeHostName, registration.HostName);
        Assert.Equal(service.NativeHostManifestPath, registration.ManifestPath);

        using var manifest = JsonDocument.Parse(File.ReadAllText(service.NativeHostManifestPath));
        var root = manifest.RootElement;
        Assert.Equal(
            ChromeCursorInputStayAwakeService.NativeHostName,
            root.GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(executablePath), root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());

        var allowedOrigin = Assert.Single(
            root.GetProperty("allowed_origins")
                .EnumerateArray()
                .Select(element => element.GetString()));
        Assert.Equal($"chrome-extension://{service.ExtensionId}/", allowedOrigin);
        Assert.DoesNotContain("edge-extension://", allowedOrigin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-browser-extension://", allowedOrigin, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void OpenExtensionSetup_UsesGoogleChromeAndSelectsBundledManifest()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var registry = new RecordingRegistry();
        var launcher = new RecordingProcessLauncher();
        var service = CreateService(
            registry,
            launcher,
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            Path.Combine(temporaryDirectory.Path, "state"),
            extensionDirectory);

        service.OpenExtensionSetup();

        Assert.Single(registry.Registrations);
        Assert.Equal(
            new[]
            {
                "OpenGoogleChromeExtensionsPage",
                $"SelectFileInExplorer:{Path.Combine(extensionDirectory, "manifest.json")}"
            },
            launcher.Calls);
    }

    [Fact]
    public void StatusRecording_WhitelistsValuesAndEnforcesRecentHeartbeatWindow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(
            new RecordingRegistry(),
            new RecordingProcessLauncher(),
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            Path.Combine(temporaryDirectory.Path, "state"),
            extensionDirectory,
            () => now);

        Assert.True(service.TryRecordStatus("pulsed", "scheduled_pulse"));
        Assert.Equal(
            new ChromeCursorInputStatus("pulsed", "scheduled_pulse", now),
            service.GetLastStatus());
        Assert.True(service.HasRecentHeartbeat);

        Assert.True(service.TryRecordStatus("waiting", "private-or-untrusted-detail"));
        var scrubbedStatus = service.GetLastStatus();
        Assert.Equal("waiting", scrubbedStatus.Status);
        Assert.Null(scrubbedStatus.Detail);
        Assert.Equal(now, scrubbedStatus.UpdatedUtc);
        Assert.Equal(now, service.LastHeartbeatUtc);

        Assert.True(
            service.TryRecordStatus(
                "pulse_skipped",
                "target_foreground_visualized"));
        Assert.Equal(
            new ChromeCursorInputStatus(
                "pulse_skipped",
                "target_foreground_visualized",
                now),
            service.GetLastStatus());

        Assert.True(
            service.TryRecordStatus(
                "pulse_skipped",
                "target_foreground_marker_unavailable"));
        Assert.Equal(
            new ChromeCursorInputStatus(
                "pulse_skipped",
                "target_foreground_marker_unavailable",
                now),
            service.GetLastStatus());

        Assert.True(service.TryRecordStatus("pulsed", "visual_marker_shown"));
        Assert.Equal(
            new ChromeCursorInputStatus("pulsed", "visual_marker_shown", now),
            service.GetLastStatus());

        Assert.True(service.TryRecordStatus("pulsed", "visual_marker_unavailable"));
        Assert.Equal(
            new ChromeCursorInputStatus("pulsed", "visual_marker_unavailable", now),
            service.GetLastStatus());

        Assert.False(service.TryRecordStatus("arbitrary-extension-status", "scheduled_pulse"));
        Assert.Equal(
            new ChromeCursorInputStatus(
                "pulsed",
                "visual_marker_unavailable",
                now),
            service.GetLastStatus());

        now += ChromeCursorInputStayAwakeService.RecentHeartbeatWindow
            + TimeSpan.FromMilliseconds(1);
        Assert.False(service.HasRecentHeartbeat);

        now -= TimeSpan.FromMinutes(1);
        Assert.False(service.HasRecentHeartbeat);
    }

    [Fact]
    public void GetLastStatus_FailsClosedForMalformedOrUnknownStatusFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var stateDirectory = Path.Combine(temporaryDirectory.Path, "state");
        var service = CreateService(
            new RecordingRegistry(),
            new RecordingProcessLauncher(),
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            stateDirectory,
            extensionDirectory);
        Directory.CreateDirectory(stateDirectory);

        File.WriteAllText(service.StatusPath, """{"status":"unexpected","detail":"scheduled_pulse"}""");
        Assert.Equal(ChromeCursorInputStatus.Unknown, service.GetLastStatus());

        File.WriteAllText(service.StatusPath, "{not-json");
        Assert.Equal(ChromeCursorInputStatus.Unknown, service.GetLastStatus());
    }

    [Fact]
    public void Heartbeat_IsUpdatedWithoutOverwritingLastStatusEvent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(
            new RecordingRegistry(),
            new RecordingProcessLauncher(),
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            Path.Combine(temporaryDirectory.Path, "state"),
            extensionDirectory,
            () => now);

        Assert.True(service.TryRecordStatus("waiting", "no_eligible_tab"));
        var statusEvent = service.GetLastStatus();
        Assert.Equal(now, statusEvent.UpdatedUtc);
        Assert.Equal(now, service.LastHeartbeatUtc);

        now += TimeSpan.FromSeconds(5);
        Assert.True(service.TryTouchHeartbeat());

        Assert.Equal(statusEvent, service.GetLastStatus());
        Assert.Equal(now, service.LastHeartbeatUtc);
        Assert.True(service.HasRecentHeartbeat);
    }

    [Fact]
    public void NativeMessaging_Run_ReadsLittleEndianFramesAndReturnsCurrentState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var service = CreateService(
            new RecordingRegistry(),
            new RecordingProcessLauncher(),
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            Path.Combine(temporaryDirectory.Path, "state"),
            extensionDirectory);
        var expectedEnabled = SettingsStore.Load().ChromeCursorInputStayAwakeEnabled;

        using var input = CreateNativeInput(
            new { type = "getState", extensionVersion = "1.0.0" },
            new
            {
                type = "status",
                status = "pulsed",
                detail = "background_pulse",
                extensionVersion = "1.0.0"
            },
            new { type = "not-supported" });
        using var output = new MemoryStream();

        ChromeCursorInputNativeMessagingHost.Run(input, output, service);

        var responses = ReadNativeOutput(output);
        Assert.Equal(2, responses.Count);
        Assert.Equal("state", responses[0].GetProperty("type").GetString());
        Assert.Equal(expectedEnabled, responses[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("error", responses[1].GetProperty("type").GetString());
        Assert.Equal("unsupported-message", responses[1].GetProperty("code").GetString());

        var status = service.GetLastStatus();
        Assert.Equal("pulsed", status.Status);
        Assert.Equal("background_pulse", status.Detail);
        Assert.NotNull(status.UpdatedUtc);
        Assert.NotNull(service.LastHeartbeatUtc);
    }

    [Fact]
    public void NativeMessaging_Run_RejectsInvalidFrameLengths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var extensionDirectory = CreateExtensionDirectory(temporaryDirectory.Path);
        var service = CreateService(
            new RecordingRegistry(),
            new RecordingProcessLauncher(),
            Path.Combine(temporaryDirectory.Path, "StayActive.exe"),
            Path.Combine(temporaryDirectory.Path, "state"),
            extensionDirectory);
        using var output = new MemoryStream();
        using var negativeLength = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0xff });

        Assert.Throws<InvalidDataException>(
            () => ChromeCursorInputNativeMessagingHost.Run(negativeLength, output, service));

        var oversizedHeader = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            oversizedHeader,
            ChromeCursorInputNativeMessagingHost.MaximumMessageBytes + 1);
        using var oversized = new MemoryStream(oversizedHeader);
        Assert.Throws<InvalidDataException>(
            () => ChromeCursorInputNativeMessagingHost.Run(oversized, output, service));
    }

    private static ChromeCursorInputStayAwakeService CreateService(
        IChromeCursorInputRegistry registry,
        IChromeCursorInputProcessLauncher launcher,
        string executablePath,
        string stateDirectory,
        string extensionDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        return new ChromeCursorInputStayAwakeService(
            registry,
            launcher,
            executablePath,
            Path.GetDirectoryName(executablePath)!,
            stateDirectory,
            extensionDirectory,
            utcNow);
    }

    private static string CreateExtensionDirectory(string root)
    {
        var extensionDirectory = Path.Combine(root, "ChromeCursorInputStayAwakeExtension");
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(
            Path.Combine(extensionDirectory, "manifest.json"),
            """
            {
              "manifest_version": 3,
              "name": "Test extension",
              "version": "1.0.0",
              "key": "AQIDBA=="
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return extensionDirectory;
    }

    private static MemoryStream CreateNativeInput(params object[] messages)
    {
        var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[sizeof(int)];
        foreach (var message in messages)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message);
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            stream.Write(header);
            stream.Write(payload);
        }

        stream.Position = 0;
        return stream;
    }

    private static IReadOnlyList<JsonElement> ReadNativeOutput(MemoryStream stream)
    {
        stream.Position = 0;
        var messages = new List<JsonElement>();
        Span<byte> header = stackalloc byte[sizeof(int)];
        while (stream.Position < stream.Length)
        {
            Assert.Equal(header.Length, stream.Read(header));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            Assert.InRange(
                payloadLength,
                1,
                ChromeCursorInputNativeMessagingHost.MaximumMessageBytes);

            var payload = new byte[payloadLength];
            Assert.Equal(payload.Length, stream.Read(payload));
            using var document = JsonDocument.Parse(payload);
            messages.Add(document.RootElement.Clone());
        }

        return messages;
    }

    private sealed class RecordingRegistry : IChromeCursorInputRegistry
    {
        public List<(string HostName, string ManifestPath)> Registrations { get; } = [];

        public void RegisterGoogleChromeNativeMessagingHost(string hostName, string manifestPath)
        {
            Registrations.Add((hostName, manifestPath));
        }
    }

    private sealed class RecordingProcessLauncher : IChromeCursorInputProcessLauncher
    {
        public List<string> Calls { get; } = [];

        public void OpenGoogleChromeExtensionsPage()
        {
            Calls.Add("OpenGoogleChromeExtensionsPage");
        }

        public void SelectFileInExplorer(string path)
        {
            Calls.Add($"SelectFileInExplorer:{path}");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"StayActive.ChromeCursorInput.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
