using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllText_ReplacesThePrimaryAndRetainsThePreviousCompleteVersion()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"YouTubeSyncTrayTests-{Guid.NewGuid():N}");
        var path = Path.Combine(testRoot, "state.json");

        try
        {
            AtomicFile.WriteAllText(path, "{\"version\":1}");
            AtomicFile.WriteAllText(path, "{\"version\":2}");

            Assert.Equal("{\"version\":2}", File.ReadAllText(path));
            Assert.Equal("{\"version\":1}", File.ReadAllText(path + ".bak"));
            Assert.Empty(Directory.GetFiles(testRoot, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void TryReadJson_UsesTheLastKnownGoodBackupWhenThePrimaryIsMalformed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"YouTubeSyncTrayTests-{Guid.NewGuid():N}");
        var path = Path.Combine(testRoot, "state.json");

        try
        {
            AtomicFile.WriteAllText(path, "{\"Version\":1}");
            AtomicFile.WriteAllText(path, "{\"Version\":2}");
            File.WriteAllText(path, "{truncated");

            Assert.True(AtomicFile.TryReadJson<TestState>(path, out var recovered));
            Assert.Equal(1, recovered.Version);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private sealed class TestState
    {
        public int Version { get; set; }
    }
}
