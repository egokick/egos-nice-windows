using QRCoder;

namespace YouTubeSyncTray;

internal static class QrCodeSvgRenderer
{
    public static string? CreateSvg(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value.Trim(), QRCodeGenerator.ECCLevel.Q);
        var qrCode = new SvgQRCode(data);
        return qrCode.GetGraphic(12);
    }
}
