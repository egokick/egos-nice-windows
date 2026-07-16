using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class RemoteAdminConsoleLauncherTests
{
    [Fact]
    public void Open_UsesTheConfiguredHttpsAdministrationConsole()
    {
        var launcher = new CapturingLauncher();
        var service = new RemoteAdminConsoleLauncher(
            () => Preferences("https://remotes.example.test/admin"),
            launcher);

        var result = service.Open();

        Assert.True(result.Succeeded);
        Assert.Equal("https://remotes.example.test/admin", launcher.Uri!.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void Open_RejectsAnUnsafeConsoleUrl()
    {
        var launcher = new CapturingLauncher();
        var service = new RemoteAdminConsoleLauncher(
            () => Preferences("http://remotes.example.test/admin"),
            launcher);

        var result = service.Open();

        Assert.False(result.Succeeded);
        Assert.Null(launcher.Uri);
        Assert.Contains("HTTPS", result.Message);
    }

    private static RemoteClientPreferences Preferences(string adminConsoleUrl)
    {
        return new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotes.example.test",
            adminConsoleUrl,
            "https://mesh.example.test",
            "Local PC",
            "Test lab");
    }

    private sealed class CapturingLauncher : IExternalUriLauncher
    {
        public Uri? Uri { get; private set; }

        public void Open(Uri uri)
        {
            Uri = uri;
        }
    }
}
