using System.Windows;
using System.Windows.Controls;

namespace Taildesk.Setup;

/// <summary>
/// The source-only launcher is intentionally a local trust anchor. The private
/// invitation fragment never appears in an archive or command file; it is read
/// directly from the link the recipient pastes into this modal dialog.
/// </summary>
internal static class SourceLauncherPrompt
{
    internal static string ReadInvitationUrl()
    {
        var input = new TextBox
        {
            MinWidth = 520,
            Margin = new Thickness(0, 8, 0, 12),
            TextWrapping = TextWrapping.NoWrap
        };
        var continueButton = new Button
        {
            Content = "Continue",
            Width = 100,
            IsDefault = true,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 100,
            IsCancel = true
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "Paste the complete Opticon invitation link from the browser. " +
                   "Its private fragment is used only to decrypt this device's invitation.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560
        });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Opticon source installation",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 620,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        continueButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            dialog.DialogResult = true;
        };
        input.Focus();
        if (dialog.ShowDialog() != true)
            throw new OperationCanceledException("The source-only installation was canceled before an invitation link was provided.");
        return input.Text.Trim();
    }
}
