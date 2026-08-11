using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Taildesk.Setup;

internal static class SdkRequirementDialog
{
    internal static bool Show(
        string sdkPolicy,
        string url,
        Func<CancellationToken, Task<bool>> exactSdkIsReadyAsync)
    {
        ArgumentNullException.ThrowIfNull(exactSdkIsReadyAsync);
        var window = new Window
        {
            Title = ".NET 10 SDK required",
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
            Text = $"Opticon requires a stable .NET SDK matching {sdkPolicy}.",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Copy the official Microsoft URL below and install a stable .NET 10 SDK outside this elevated Setup window. Keep this window open: Setup will detect it and resume automatically.",
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
            Text = "Waiting for a stable .NET 10 SDK..."
        };
        panel.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var copy = new Button { Content = "Copy URL", MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 6, 12, 6) };
        var retry = new Button { Content = "Check now", MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        var exit = new Button { Content = "Exit Setup", MinWidth = 100, Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        var lifetime = new CancellationTokenSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        var checking = false;

        async Task CheckAsync()
        {
            if (checking || lifetime.IsCancellationRequested) return;
            checking = true;
            retry.IsEnabled = false;
            status.Text = "Checking for a stable .NET 10 SDK...";
            try
            {
                if (await exactSdkIsReadyAsync(lifetime.Token))
                {
                    timer.Stop();
                    status.Text = "A compatible .NET 10 SDK is ready. Resuming Opticon Setup...";
                    if (window.IsVisible) window.DialogResult = true;
                }
                else
                {
                    status.Text = "A compatible .NET 10 SDK is not ready yet. Finish its installer; Opticon will keep checking automatically.";
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                status.Text = "The SDK check could not complete yet: " + exception.Message;
            }
            finally
            {
                checking = false;
                if (!lifetime.IsCancellationRequested) retry.IsEnabled = true;
            }
        }

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
        retry.Click += async (_, _) => await CheckAsync();
        exit.Click += (_, _) => window.DialogResult = false;
        timer.Tick += async (_, _) => await CheckAsync();
        buttons.Children.Add(copy);
        buttons.Children.Add(retry);
        buttons.Children.Add(exit);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            urlBox.Focus();
            urlBox.SelectAll();
            timer.Start();
            _ = CheckAsync();
        };
        window.Closed += (_, _) =>
        {
            timer.Stop();
            lifetime.Cancel();
        };
        try
        {
            return window.ShowDialog() == true;
        }
        finally
        {
            timer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
