using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class ChromiumBrowserLocatorTests
{
    [Fact]
    public void TryGetProfileAvatarPath_UsesGaiaPictureFileNameFromLocalState()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        var userDataPath = Path.Combine(root, "User Data");
        var profilePath = Path.Combine(userDataPath, "Default");
        Directory.CreateDirectory(profilePath);

        try
        {
            var avatarPath = Path.Combine(profilePath, "Edge Profile Picture.png");
            File.WriteAllText(avatarPath, "edge-avatar");
            File.WriteAllText(
                Path.Combine(userDataPath, "Local State"),
                """
                {
                  "profile": {
                    "info_cache": {
                      "Default": {
                        "gaia_picture_file_name": "Edge Profile Picture.png"
                      }
                    }
                  }
                }
                """);

            var found = ChromiumBrowserLocator.TryGetProfileAvatarPath(userDataPath, "Default", out var resolvedPath);

            Assert.True(found);
            Assert.Equal(avatarPath, resolvedPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryGetProfileAvatarPath_FallsBackToProfilePictureScanWhenLocalStateIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "YouTubeSyncTray.Tests", Guid.NewGuid().ToString("N"));
        var userDataPath = Path.Combine(root, "User Data");
        var profilePath = Path.Combine(userDataPath, "Profile 1");
        Directory.CreateDirectory(profilePath);

        try
        {
            var iconPath = Path.Combine(profilePath, "Edge Profile.ico");
            var pngPath = Path.Combine(profilePath, "Edge Profile Picture.png");
            File.WriteAllText(iconPath, "icon-avatar");
            File.WriteAllText(pngPath, "png-avatar");

            var found = ChromiumBrowserLocator.TryGetProfileAvatarPath(userDataPath, "Profile 1", out var resolvedPath);

            Assert.True(found);
            Assert.Equal(pngPath, resolvedPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
