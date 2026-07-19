using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class LibraryWebServerTests
{
    [Fact]
    public void GetFileRevisionToken_ChangesWhenMutableMediaIsReplaced()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"YouTubeSyncTrayTests-{Guid.NewGuid():N}");
        var path = Path.Combine(testRoot, "video.mp4");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(path, "first");
            var firstRevision = LibraryWebServer.GetFileRevisionToken(path);

            File.WriteAllText(path, "replacement-media");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var replacementRevision = LibraryWebServer.GetFileRevisionToken(path);

            Assert.NotEqual(firstRevision, replacementRevision);
            Assert.Equal(replacementRevision, LibraryWebServer.GetFileRevisionToken(path));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
