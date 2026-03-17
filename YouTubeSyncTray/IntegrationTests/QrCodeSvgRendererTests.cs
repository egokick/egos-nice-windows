using YouTubeSyncTray;

namespace YouTubeSyncTray.IntegrationTests;

public sealed class QrCodeSvgRendererTests
{
    [Fact]
    public void CreateSvg_ReturnsNull_WhenValueIsMissing()
    {
        Assert.Null(QrCodeSvgRenderer.CreateSvg(null));
        Assert.Null(QrCodeSvgRenderer.CreateSvg("   "));
    }

    [Fact]
    public void CreateSvg_ReturnsSvgMarkup_WhenValueIsPresent()
    {
        var svg = QrCodeSvgRenderer.CreateSvg("http://192.168.137.1/");

        Assert.NotNull(svg);
        Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</svg>", svg, StringComparison.OrdinalIgnoreCase);
    }
}
