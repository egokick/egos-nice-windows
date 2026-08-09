using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Taildesk.Setup;

internal static class SdkRequirementDialog
{
    internal static bool Show(string version, string architecture, string url)
    {
        var window = new Window
        {
            Title = "Opticon SDK required",
            Width = 720,
            Height = 330,
            MinWidth = 600,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = true
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Opticon requires exact .NET SDK {version} and its .NET {version[..version.LastIndexOf('.')]} runtimes for Windows {architecture}.",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Copy the official Microsoft URL below, install the SDK outside this elevated Setup window, then return and choose Retry.",
            Margin = new Thickness(0, 12, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        var urlBox = new TextBox
        {
            Text = url,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(8),
            MinHeight = 58
        };
        panel.Children.Add(urlBox);
        var status = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray,
            Text = "Setup will not open the URL or run an SDK installer while elevated."
        };
        panel.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var copy = new Button { Content = "Copy URL", MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 6, 12, 6) };
        var retry = new Button { Content = "Retry", MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        var exit = new Button { Content = "Exit Setup", MinWidth = 100, Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(url);
                status.Text = "The official Microsoft URL was copied.";
            }
            catch (Exception exception)
            {
                status.Text = "Windows could not copy the URL: " + exception.Message;
                urlBox.Focus();
                urlBox.SelectAll();
            }
        };
        retry.Click += (_, _) => window.DialogResult = true;
        exit.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(copy);
        buttons.Children.Add(retry);
        buttons.Children.Add(exit);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            urlBox.Focus();
            urlBox.SelectAll();
        };
        return window.ShowDialog() == true;
    }
}
