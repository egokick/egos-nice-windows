using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _refreshTimer.Stop();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.ExitRequested) return;
        e.Cancel = true;
        Hide();
        _viewModel.Status = "Running in the notification area";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsControls();
        if (!_viewModel.Config.SetupComplete)
        {
            WorkspaceTabs.SelectedIndex = 6;
            _viewModel.Status = "Complete command-center setup";
        }
        await _viewModel.InitializeAsync();
        _refreshTimer.Start();
        if (_viewModel.Config.SetupComplete) _ = _viewModel.RunSystemChecksAsync();
    }

    private async void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var index)) return;
        WorkspaceTabs.SelectedIndex = index;
        if (index == 5) await _viewModel.RunSystemChecksAsync();
    }

    private void GoToInvites_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 3;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.RefreshAsync());
    private async void RunSystemChecks_Click(object sender, RoutedEventArgs e) => await _viewModel.RunSystemChecksAsync();

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RoleCombo.SelectedIndex = _viewModel.SelectedDevice?.Role == DeviceRole.ControllerAndManaged ? 1 : 0;
    }

    private async void RemoteControl_Click(object sender, RoutedEventArgs e)
    {
        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }
        await RunAsync(() => _viewModel.LaunchRemoteControlAsync(device));
    }

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = RequireDevice();
            if (device.State == DeviceConnectionState.Offline)
                throw new InvalidOperationException($"{device.Name} is offline. Wake or power on the device and wait for it to reconnect to the private network.");
            var app = (App)System.Windows.Application.Current;
            new FileManagerWindow(device, _viewModel.GetAgentToken(device), app.Agents, app.Transfers) { Owner = this }.Show();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void UseExitNode_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.UseExitNodeAsync(RequireDevice()));
    private async void StopExitNode_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.StopUsingExitNodeAsync());

    private async void ApplyRole_Click(object sender, RoutedEventArgs e)
    {
        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }
        var role = (RoleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == nameof(DeviceRole.ControllerAndManaged)
            ? DeviceRole.ControllerAndManaged : DeviceRole.ManagedOnly;
        if (role == device.Role) return;
        var explanation = role == DeviceRole.ManagedOnly
            ? "This immediately revokes controller network access. Opticon will also rotate peer agent tokens and RustDesk passwords. Continue?"
            : "This machine will be allowed to control other Opticon machines. The controller shortcut appears at its next sign-in. Continue?";
        if (MessageBox.Show(explanation, "Change access role", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _viewModel.ChangeRoleAsync(device, role));
    }

    private async void EnableExitService_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel.SetExitAdvertisementAsync(RequireDevice(), true));

    private async void DisableExitService_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel.SetExitAdvertisementAsync(RequireDevice(), false));

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var password = _viewModel.GetRustDeskPassword(RequireDevice());
            System.Windows.Clipboard.SetText(password);
            _viewModel.Status = "Recovery password copied; clipboard clears in 60 seconds";
            ClearClipboardLater(password);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsPrimary)
        {
            ShowError(new InvalidOperationException("Only the primary command center can remove an enrolled device."));
            return;
        }

        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }

        var name = string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
        var explanation =
            $"Revoke '{name}' and remove it from Opticon?\n\n" +
            "This deletes its current Tailscale device identity, removes its saved Opticon credentials, and deletes associated invitation bundles that are still on this command center.\n\n" +
            "It does not uninstall Tailscale, RustDesk, or Opticon files from that PC. Connecting it again requires a new invitation.";
        if (device.Role == DeviceRole.ControllerAndManaged)
        {
            explanation += "\n\nBecause this device could control peers, Opticon will rotate peer agent tokens and RustDesk passwords. Offline peers will finish rotation when they reconnect.";
        }
        if (device.AdvertisesExitNode)
        {
            explanation += "\n\nIf this device is your active VPN exit, your connection may be interrupted.";
        }

        if (MessageBox.Show(
                explanation,
                "Revoke enrolled device",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(() => _viewModel.RemoveDeviceAsync(device));
    }

    private async void CreateInvite_Click(object sender, RoutedEventArgs e)
    {
        if (!CreateInviteButton.IsEnabled) return;
        var role = (InviteRoleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == nameof(DeviceRole.ControllerAndManaged)
            ? DeviceRole.ControllerAndManaged : DeviceRole.ManagedOnly;
        var allowedRoots = new[]
        {
            (Name: "Desktop", Selected: InviteRootDesktopCheck.IsChecked == true),
            (Name: "Documents", Selected: InviteRootDocumentsCheck.IsChecked == true),
            (Name: "Downloads", Selected: InviteRootDownloadsCheck.IsChecked == true),
            (Name: "Pictures", Selected: InviteRootPicturesCheck.IsChecked == true),
            (Name: "Videos", Selected: InviteRootVideosCheck.IsChecked == true)
        }.Where(root => root.Selected).Select(root => root.Name).ToArray();
        CreateInviteButton.IsEnabled = false;
        CreateInviteButton.Content = "Creating...";
        try
        {
            await RunAsync(async () =>
            {
                var result = await _viewModel.CreateInviteAsync(
                    InviteNameText.Text,
                    role,
                    exitNode: true,
                    allowedRoots);
                InviteNameText.Clear();
                System.Windows.Clipboard.SetText(result.InvitationUrl);
                MessageBox.Show($"One-click invitation link created and copied to the clipboard:\n\n{result.InvitationUrl}\n\nSend this link to the target machine. It expires in 14 days and can enroll only one machine.",
                    "Invitation link ready", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }
        finally
        {
            CreateInviteButton.Content = "Create one-click link";
            CreateInviteButton.IsEnabled = true;
        }
    }

    private void InviteGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as DataGrid)?.SelectedItem is not InviteRecord invite) return;
        if (!string.IsNullOrWhiteSpace(invite.HostedUrl)) CopyInviteUrl(invite);
        else if (File.Exists(invite.BundlePath)) RevealFile(invite.BundlePath);
    }

    private void InviteGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow) current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row)
        {
            InviteGrid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private void CopyInviteUrl_Click(object sender, RoutedEventArgs e)
    {
        if (InviteGrid.SelectedItem is not InviteRecord invite) { ShowError(new InvalidOperationException("Select an invitation first.")); return; }
        CopyInviteUrl(invite);
    }

    private void CopyInviteUrl(InviteRecord invite)
    {
        if (string.IsNullOrWhiteSpace(invite.HostedUrl)) { ShowError(new InvalidOperationException("This invitation no longer has an active URL.")); return; }
        System.Windows.Clipboard.SetText(invite.HostedUrl);
        _viewModel.Status = $"Invitation URL copied: {invite.DeviceName}";
    }

    private async void ExtendInvite_Click(object sender, RoutedEventArgs e)
    {
        if (InviteGrid.SelectedItem is not InviteRecord invite) { ShowError(new InvalidOperationException("Select an invitation first.")); return; }
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            $"How many days should be added to the current expiry for '{invite.DeviceName}'?\n\nThe URL remains the same and the one-use network key will be rotated.",
            "Extend invitation expiry", "14");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!int.TryParse(input, out var days) || days is < 1 or > InvitationPolicy.MaximumLifetimeDays)
        {
            ShowError(new InvalidOperationException($"Enter a whole number from 1 to {InvitationPolicy.MaximumLifetimeDays}."));
            return;
        }
        await RunAsync(() => _viewModel.ExtendInviteAsync(invite, days));
    }

    private async void ExpireInvite_Click(object sender, RoutedEventArgs e)
    {
        if (InviteGrid.SelectedItem is not InviteRecord invite) { ShowError(new InvalidOperationException("Select an invitation first.")); return; }
        await ExpireInviteAsync(invite);
    }

    private async void CancelInvite_Click(object sender, RoutedEventArgs e)
    {
        if (InviteGrid.SelectedItem is not InviteRecord invite)
        {
            ShowError(new InvalidOperationException("Select an invitation first."));
            return;
        }
        await ExpireInviteAsync(invite);
    }

    private async Task ExpireInviteAsync(InviteRecord invite)
    {
        if (MessageBox.Show($"Expire the invitation for '{invite.DeviceName}' now?\n\nIts URL and one-use network key will be disabled immediately.",
                "Expire invitation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _viewModel.CancelInviteAsync(invite));
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _viewModel.SavePrimarySettingsAsync(TailnetText.Text, OAuthIdText.Text, OAuthSecretText.Password,
                CoordinatorIpText.Text, InviteFolderText.Text, RustDeskPathText.Text);
            await ((App)System.Windows.Application.Current).RestartCoordinatorAsync();
            OAuthSecretText.Clear();
            _refreshTimer.Start();
        });
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.TestTailscaleApiAsync());

    private async void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("This replaces the tailnet's entire policy with Opticon's strict role policy. Existing ACL/grant rules will be removed. Continue?",
                "Replace Tailscale policy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _viewModel.ApplyTailnetPolicyAsync());
    }

    private async void TagHub_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.TagThisMachineAsHubAsync());

    private async void DetectIp_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            var result = await ProcessRunner.RunAsync(tailscale, ["ip", "-4"], TimeSpan.FromSeconds(15));
            if (!result.Succeeded) throw new InvalidOperationException(result.StandardError.Trim());
            CoordinatorIpText.Text = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        });
    }

    private void BrowseInviteFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where Opticon invitation installers are saved", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) InviteFolderText.Text = dialog.SelectedPath;
    }

    private void BrowseRustDesk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "RustDesk|rustdesk.exe|Applications|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) RustDeskPathText.Text = dialog.FileName;
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        if (!AgentClient.IsTailscaleIp(CoordinatorIpText.Text))
        {
            ShowError(new InvalidOperationException("Detect or enter this laptop's Tailscale IPv4 address first."));
            return;
        }
        try
        {
            foreach (var ruleName in new[] { "Opticon Coordinator (Tailscale only)", "Taildesk Coordinator (Tailscale only)" })
            {
                using var deletion = Process.Start(new ProcessStartInfo("netsh.exe", $"advfirewall firewall delete rule name=\"{ruleName}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                if (deletion is not null) await deletion.WaitForExitAsync();
            }
            var arguments = $"advfirewall firewall add rule name=\"Opticon Coordinator (Tailscale only)\" dir=in action=allow protocol=TCP localport=45830 localip={CoordinatorIpText.Text} remoteip=100.64.0.0/10 profile=any enable=yes";
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) throw new InvalidOperationException("Could not start the Windows firewall helper.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows did not create the coordinator firewall rule.");
            _viewModel.Log("Coordinator firewall rule created for the Tailscale interface.");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void LoadSettingsControls()
    {
        TailnetText.Text = _viewModel.Config.HeadscaleApiUrl;
        OAuthIdText.Text = _viewModel.Config.HeadscaleUserId;
        CoordinatorIpText.Text = _viewModel.Config.CoordinatorBindAddress;
        InviteFolderText.Text = _viewModel.Config.InviteOutputDirectory;
        RustDeskPathText.Text = _viewModel.Config.RustDeskPath;
        var editable = _viewModel.Config.Mode == AdminMode.Primary;
        TailnetText.IsEnabled = OAuthIdText.IsEnabled = OAuthSecretText.IsEnabled = CoordinatorIpText.IsEnabled = editable;
    }

    private DeviceRecord RequireDevice() => _viewModel.SelectedDevice ?? throw new InvalidOperationException("Select a device first.");

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private static void ShowError(Exception exception) =>
        MessageBox.Show(exception.Message, "Opticon", MessageBoxButton.OK, MessageBoxImage.Error);

    private static void RevealFile(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    private static async void ClearClipboardLater(string expected)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        try
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == expected) System.Windows.Clipboard.Clear();
            });
        }
        catch { }
    }
}
