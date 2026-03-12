using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopilotScreenshotProvider;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            SetDpiAwareness();

            var bounds = SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            var screenshotsDir = @"C:\source\screenshots";
            Directory.CreateDirectory(screenshotsDir);

            var filePath = Path.Combine(
                screenshotsDir,
                $"CopilotScreenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            bitmap.Save(filePath, ImageFormat.Png);
            Clipboard.SetText(filePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Copilot Screenshot Provider", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SetDpiAwareness()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            return;
        }

        SetProcessDPIAware();
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
