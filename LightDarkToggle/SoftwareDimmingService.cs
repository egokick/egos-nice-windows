using System.Runtime.InteropServices;

namespace LightDarkToggle;

internal sealed class SoftwareDimmingService : IDisposable
{
    private readonly Dictionary<string, DimmingOverlayForm> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool TrySetDimming(int dimmingPercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        dimmingPercent = Math.Clamp(dimmingPercent, 0, 90);

        try
        {
            SynchronizeOverlays();
            foreach (var overlay in _overlays.Values)
            {
                overlay.SetDimming(dimmingPercent);
            }

            return _overlays.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var overlay in _overlays.Values)
        {
            overlay.Dispose();
        }

        _overlays.Clear();
        _disposed = true;
    }

    private void SynchronizeOverlays()
    {
        var screens = Screen.AllScreens.ToDictionary(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase);
        foreach (var removedDeviceName in _overlays.Keys.Except(screens.Keys).ToArray())
        {
            _overlays[removedDeviceName].Dispose();
            _overlays.Remove(removedDeviceName);
        }

        foreach (var (deviceName, screen) in screens)
        {
            if (!_overlays.TryGetValue(deviceName, out var overlay))
            {
                overlay = new DimmingOverlayForm();
                _overlays.Add(deviceName, overlay);
            }

            overlay.Bounds = screen.Bounds;
        }
    }
}

internal sealed class DimmingOverlayForm : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    public DimmingOverlayForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "LightDarkToggle dimming overlay";
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
            return parameters;
        }
    }

    public void SetDimming(int dimmingPercent)
    {
        dimmingPercent = Math.Clamp(dimmingPercent, 0, 90);
        if (dimmingPercent == 0)
        {
            Hide();
            return;
        }

        Opacity = dimmingPercent / 100d;
        if (!Visible)
        {
            Show();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }
}
