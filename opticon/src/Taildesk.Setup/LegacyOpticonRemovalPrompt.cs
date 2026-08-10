using System.Windows;
using System.Windows.Controls;

namespace Taildesk.Setup;

/// <summary>
/// Deliberately requires typed acknowledgement.  The destructive button is
/// never the default action, so Enter cannot accept this dialog accidentally.
/// </summary>
internal static class LegacyOpticonRemovalPrompt
{
    internal static bool Confirm(LegacyOpticonRemoval.RemovalPlan plan)
    {
        var confirmation = new TextBox
        {
            MinWidth = 460,
            Margin = new Thickness(0, 8, 0, 12),
            TextWrapping = TextWrapping.NoWrap
        };
        var remove = new Button
        {
            Content = "Remove legacy Opticon",
            Width = 170,
            IsEnabled = false,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 100,
            IsCancel = true,
            IsDefault = true
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);

        var details = new TextBlock
        {
            Text = BuildMessage(plan),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 590
        };
        var instruction = new TextBlock
        {
            Text = $"To permanently remove it, type exactly: {LegacyOpticonRemoval.ConfirmationPhrase}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 590
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(details);
        panel.Children.Add(new TextBlock { Height = 10 });
        panel.Children.Add(instruction);
        panel.Children.Add(confirmation);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Remove legacy Opticon 1.1.38",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 650,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        confirmation.TextChanged += (_, _) => remove.IsEnabled =
            string.Equals(confirmation.Text, LegacyOpticonRemoval.ConfirmationPhrase, StringComparison.Ordinal);
        remove.Click += (_, _) => dialog.DialogResult = true;
        confirmation.Focus();
        return dialog.ShowDialog() == true;
    }

    private static string BuildMessage(LegacyOpticonRemoval.RemovalPlan plan)
    {
        var directories = string.Join(Environment.NewLine, plan.DirectoriesToRemove.Select(path => "• " + path));
        var tasks = plan.TaskNames.Count == 0
            ? "No recognized Opticon scheduled tasks are currently registered."
            : string.Join(Environment.NewLine, plan.TaskNames.Select(name => "• " + name));
        return "The signed source launcher verified a legacy Opticon 1.1.38 installation. " +
               "This is a permanent, local removal so the new protected source build can start cleanly.\n\n" +
               "It will remove only these verified Opticon directories:\n" + directories + "\n\n" +
               "It will stop and delete only these verified Opticon scheduled tasks:\n" + tasks + "\n\n" +
               "Agent 1.1.38 used those tasks rather than an Opticon Windows service; no Windows service is stopped or deleted.\n\n" +
               "Tailscale and RustDesk are deliberately preserved: this does not stop, uninstall, reconfigure, " +
               "or delete either product, its services, credentials, sessions, or data.\n\n" +
               "If any file, link, task, or version does not match the recognized legacy Opticon shape, removal stops before deleting files.";
    }
}
