using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class LocalBrowserHostTests
{
    [Fact]
    public void Normalize_ReturnsDefault_WhenValueIsMissing()
    {
        Assert.Equal(LocalBrowserHost.DefaultValue, LocalBrowserHost.Normalize(null));
        Assert.Equal(LocalBrowserHost.DefaultValue, LocalBrowserHost.Normalize("   "));
    }

    [Fact]
    public void Normalize_UsesPlainHostname_WhenValueIsValid()
    {
        Assert.Equal("egokick.com", LocalBrowserHost.Normalize("Egokick.com"));
        Assert.Equal("127.0.0.1", LocalBrowserHost.Normalize("127.0.0.1"));
    }

    [Fact]
    public void Normalize_StripsScheme_AndPath_FromAbsoluteUrl()
    {
        Assert.Equal("egokick.com", LocalBrowserHost.Normalize("http://Egokick.com/sync"));
        Assert.Equal("tom.localhost", LocalBrowserHost.Normalize("https://tom.localhost:48173/"));
    }

    [Fact]
    public void Normalize_FallsBack_WhenValueIsNotAHostname()
    {
        Assert.Equal(LocalBrowserHost.DefaultValue, LocalBrowserHost.Normalize("not a host"));
        Assert.Equal(LocalBrowserHost.DefaultValue, LocalBrowserHost.Normalize("folder/name"));
    }
}
