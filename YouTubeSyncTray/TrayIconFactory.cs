using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace YouTubeSyncTray;

internal static class TrayIconFactory
{
    public static Icon CreatePlayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var redBrush = new SolidBrush(Color.FromArgb(220, 34, 34));
        using var whiteBrush = new SolidBrush(Color.White);
        graphics.FillEllipse(redBrush, 3, 3, 26, 26);
        graphics.FillPolygon(whiteBrush, [
            new Point(13, 10),
            new Point(13, 22),
            new Point(23, 16)
        ]);

        return CreateIcon(bitmap);
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
