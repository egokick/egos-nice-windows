using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
            WorkspaceTabs.SelectedIndex = 7;
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
        if (index == 1) await _viewModel.ScheduledTransferManager.RefreshAsync();
        if (index == 6) await _viewModel.RunSystemChecksAsync();
    }

    private void GoToInvites_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 4;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _viewModel.RefreshAsync());
    private async void RunSystemChecks_Click(object sender, RoutedEventArgs e) => await _viewModel.RunSystemChecksAsync();

    private async void NewScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        var editor = new ScheduledTransferEditorWindow(app.State, app.Agents) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            await RunAsync(async () =>
            {
                await _viewModel.ScheduledTransferManager.SaveAsync(editor.Result);
                _viewModel.Status = $"Scheduled {editor.Result.Name}";
            });
    }

    private async void EditScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledTransferGrid.SelectedItem is not ScheduledTransferRow row) return;
        var app = (App)System.Windows.Application.Current;
        var editor = new ScheduledTransferEditorWindow(app.State, app.Agents, row.Definition) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            await RunAsync(async () =>
            {
                await _viewModel.ScheduledTransferManager.SaveAsync(editor.Result);
                _viewModel.Status = $"Updated {editor.Result.Name}";
            });
    }

    private async void RunScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledTransferGrid.SelectedItem is not ScheduledTransferRow row) return;
        var progress = new Progress<string>(message => _viewModel.Status = message);
        await RunAsync(async () =>
        {
            var result = await _viewModel.ScheduledTransferManager.RunNowAsync(row.Definition.Id, progress);
            _viewModel.Status = $"{row.Name}: {result.Message}";
        });
    }

    private async void ToggleScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledTransferGrid.SelectedItem is not ScheduledTransferRow row) return;
        await RunAsync(async () =>
        {
            await _viewModel.ScheduledTransferManager.SetEnabledAsync(row.Definition.Id, !row.Definition.Enabled);
            _viewModel.Status = $"{row.Name} is now {(!row.Definition.Enabled ? "enabled" : "paused")}";
        });
    }

    private async void DeleteScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledTransferGrid.SelectedItem is not ScheduledTransferRow row) return;
        if (MessageBox.Show($"Delete the schedule '{row.Name}'? Its run history will be kept.", "Delete scheduled transfer",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _viewModel.ScheduledTransferManager.DeleteAsync(row.Definition.Id);
            _viewModel.Status = $"Deleted schedule {row.Name}";
        });
    }

    private async void RetryScheduledTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledHistoryGrid.SelectedItem is not ScheduledTransferHistoryRow { CanRetry: true } row) return;
        var progress = new Progress<string>(message => _viewModel.Status = message);
        await RunAsync(async () =>
        {
            var result = await _viewModel.ScheduledTransferManager.RetryAsync(row.Id, progress);
            _viewModel.Status = $"{row.Name}: {result.Message}";
        });
    }

    private void TransferGrid_PreviewMouseRightButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void TransferGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var transfer = TransferGrid.SelectedItem as TransferRow;
        ResumeTransferMenuItem.IsEnabled = transfer?.CanResume == true;
        CancelTransferMenuItem.IsEnabled = transfer?.CanCancel == true;
    }

    private void ResumeTransfer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TransferGrid.SelectedItem is not TransferRow transfer)
                throw new InvalidOperationException("Select a transfer to resume.");
            _viewModel.ResumeTransfer(transfer);
            _viewModel.Status = $"Resuming {transfer.File} on {transfer.Device}";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransferGrid.SelectedItem is not TransferRow transfer) return;
        _viewModel.CancelTransfer(transfer);
        _viewModel.Status = $"Cancelling {transfer.File} on {transfer.Device}";
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RoleCombo.SelectedIndex = _viewModel.SelectedDevice?.Role == DeviceRole.ControllerAndManaged ? 1 : 0;
    }

    private void DeviceGrid_PreviewMouseRightButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row)
        {
            DeviceGrid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private void DeviceGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RenameDeviceMenuItem.IsEnabled = _viewModel.IsPrimary
                                         && !_viewModel.Busy
                                         && DeviceGrid.SelectedItem is DeviceRecord;
    }

    private async void RenameDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsPrimary)
        {
            ShowError(new InvalidOperationException("Only the primary command center can rename an enrolled device."));
            return;
        }
        if (DeviceGrid.SelectedItem is not DeviceRecord device)
        {
            ShowError(new InvalidOperationException("Right-click a device row to rename it."));
            return;
        }

        var currentName = string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
        var prompt = new PromptWindow(
            "Rename device",
            $"Enter a new display name for '{currentName}'.",
            currentName)
        {
            Owner = this
        };
        if (prompt.ShowDialog() != true) return;

        await RunAsync(() => _viewModel.RenameDeviceAsync(device, prompt.Value));
    }

    private async void RemoteControl_Click(object sender, RoutedEventArgs e)
    {
        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }
        await RunAsync(() => _viewModel.LaunchRemoteControlAsync(device));
    }

    private async void OpenSsh_Click(object sender, RoutedEventArgs e)
    {
        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }
        await RunAsync(() => _viewModel.LaunchSshAsync(device));
    }

    private async void UpdateOpticon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = RequireDevice();
            var release = await _viewModel.FindUpdateAsync(device);
            if (release is null)
            {
                MessageBox.Show($"{device.Name} already has the newest compatible signed Opticon Agent release.",
                    "Opticon Agent update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var isLegacyMachineStateMigrationBridge = release.IsLegacyMachineStateMigrationBridge;
            if (isLegacyMachineStateMigrationBridge
                && !RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
                    UpdatePackageVerifier.ParseVersion(device.AgentVersion),
                    UpdatePackageVerifier.ParseVersion(release.Version),
                    release.LegacyMigrationSignerThumbprint))
                throw new InvalidDataException(
                    "The selected release does not match the canonical Opticon legacy machine-state bridge.");
            if (!isLegacyMachineStateMigrationBridge
                && !string.IsNullOrEmpty(release.LegacyMigrationSignerThumbprint))
                throw new InvalidDataException(
                    "A legacy migration marker is not valid for this installed Agent and release version.");

            if (release.RequiresLegacyMachineStateMigration)
            {
                MessageBox.Show(
                    $"{device.Name} runs Opticon Agent {device.AgentVersion}, which predates the protected machine-state storage contract.\n\n" +
                    $"The signed release {release.Version} intentionally refuses to adopt that legacy state during an unattended update. No candidate was staged or activated.\n\n" +
                    "The one-time signed bridge is supported only from Opticon Agent 1.1.38 to 1.1.41. " +
                    "The retired maintenance bootstrap and a fresh source-build invitation do not migrate this legacy state. " +
                    "Leave the device on its current Agent until the supported bridge is published for this exact device.",
                    "Legacy Opticon migration required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (isLegacyMachineStateMigrationBridge)
            {
                if (release.RequiresMaintenanceBootstrap)
                    throw new InvalidOperationException(
                        "The signed legacy machine-state bridge requires the guarded Opticon update API and stable Guardian already present on Agent 1.1.38. " +
                        "The retired maintenance bootstrap cannot launch this bridge.");
                var bridgeExplanation =
                    $"Run the one-time signed Opticon machine-state bridge on '{device.Name}'?\n\n" +
                    "This exact 1.1.38-to-1.1.41 release seals the existing Opticon ProgramData state with the protected ACL layout before the replacement Agent starts. " +
                    "It keeps the same device identity, Tailnet identity, Tailscale address, RustDesk configuration, credentials, routes, and remote recovery lifelines.\n\n" +
                    "The normal SYSTEM Guardian verification, repeated health checks, final commit, and automatic rollback by omission still apply. " +
                    "After the bridge commits, run Update Opticon again to install the current normal signed release.";
                if (MessageBox.Show(
                        bridgeExplanation,
                        "Run signed legacy Opticon bridge",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            else if (release.RequiresMaintenanceBootstrap)
            {
                await RunMaintenanceBootstrapAsync(
                    device,
                    release,
                    $"{device.Name} runs an Agent that predates the guarded update API.");
                return;
            }

            if (!isLegacyMachineStateMigrationBridge)
            {
                var explanation =
                    $"Update '{device.Name}' from Opticon Agent {device.AgentVersion} to {release.Version}?\n\n" +
                    "Opticon will first download and fully verify the release while the current Agent and recovery lifelines remain active. " +
                    "The stable SYSTEM Guardian, installed outside the versioned Agent, then swaps only the signed Agent/runtime directory. Tailscale, RustDesk, Guardian-owned SSH, credentials, and routes are not changed.\n\n" +
                    "The new Agent must pass repeated command-center and local checks for every applicable recovery lifeline. If it crashes, loses a lifeline, the PC reboots, or this command center cannot send the final commit, the Guardian automatically restores the previous Agent.";
                if (MessageBox.Show(explanation, "Guarded Opticon Agent update", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            var progressWindow = new UpdateProgressWindow(device.Name, device.AgentVersion, release.Version)
            {
                Owner = this,
                DataContext = _viewModel
            };
            progressWindow.Show();
            UpdateStatusDto result;
            try
            {
                result = await _viewModel.UpdateDeviceAsync(device, release);
            }
            finally
            {
                progressWindow.FinishAndClose();
                Activate();
            }
            var message = result.Phase == UpdatePhase.Committed
                ? isLegacyMachineStateMigrationBridge
                    ? $"The signed Opticon 1.1.41 machine-state bridge is healthy and committed on {device.Name}. " +
                      "Its protected ProgramData ACL transition completed in place; device identity and recovery lifelines were preserved. " +
                      "Run Update Opticon again to install the current normal signed release."
                    : $"Opticon Agent {result.TargetVersion} is healthy and committed on {device.Name}. The prior Agent remains available locally for boot-time recovery."
                : $"The candidate was not committed. {device.Name} reported {result.Phase} and remains on Opticon Agent {result.CurrentVersion}.\n\n{result.Message}";
            var requiresAttendedMaintenance = !isLegacyMachineStateMigrationBridge
                                              && result.Phase == UpdatePhase.Failed
                                              && (result.Message.Contains("download", StringComparison.OrdinalIgnoreCase)
                                                  || (result.Message.Contains("requires update guardian", StringComparison.OrdinalIgnoreCase)
                                                      && result.Message.Contains("installed is", StringComparison.OrdinalIgnoreCase)));
            var response = MessageBox.Show(
                message + (requiresAttendedMaintenance
                    ? "\n\nChoose Yes to use the signed attended-maintenance path. Opticon will copy a direct-download command, open the remote desktop, update the stable Guardian and Agent together, and visibly watch the exact candidate through commit or rollback."
                    : string.Empty),
                "Opticon Agent update",
                requiresAttendedMaintenance ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                result.Phase == UpdatePhase.Committed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (requiresAttendedMaintenance && response == MessageBoxResult.Yes)
            {
                await RunMaintenanceBootstrapAsync(
                    device,
                    release with { RequiresMaintenanceBootstrap = true },
                    result.Message.Contains("requires update guardian", StringComparison.OrdinalIgnoreCase)
                        ? $"{device.Name}'s stable Guardian must be upgraded together with the signed Agent release."
                        : $"{device.Name}'s installed Agent could not download the signed release through its legacy network path.");
            }
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void PrivacyMode2_Click(object sender, RoutedEventArgs e)
    {
        DeviceRecord device;
        try { device = RequireDevice(); }
        catch (Exception exception) { ShowError(exception); return; }
        var enabled = PrivacyMode2Toggle.IsChecked == true;
        await RunAsync(() => _viewModel.SetPrivacyMode2Async(device, enabled));
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

    private async void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var password = _viewModel.GetRustDeskPassword(RequireDevice());
            await SetClipboardTextAsync(password);
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
        // These legacy profile roots keep invitations compatible with older
        // agents. Current agents expose all accessible local volumes.
        string[] allowedRoots = ["Desktop", "Documents", "Downloads", "Pictures", "Videos"];
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
                await SetClipboardTextAsync(result.InvitationUrl);
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

    private async void InviteGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.Controls.DataGrid)?.SelectedItem is not InviteRecord invite) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(invite.HostedUrl)) await CopyInviteUrlAsync(invite);
            else if (File.Exists(invite.BundlePath)) RevealFile(invite.BundlePath);
        }
        catch (Exception exception) { ShowError(exception); }
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

    private async void CopyInviteUrl_Click(object sender, RoutedEventArgs e)
    {
        if (InviteGrid.SelectedItem is not InviteRecord invite) { ShowError(new InvalidOperationException("Select an invitation first.")); return; }
        try { await CopyInviteUrlAsync(invite); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task CopyInviteUrlAsync(InviteRecord invite)
    {
        if (string.IsNullOrWhiteSpace(invite.HostedUrl)) { ShowError(new InvalidOperationException("This invitation no longer has an active URL.")); return; }
        await SetClipboardTextAsync(invite.HostedUrl);
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

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Direct firewall elevation from the Opticon UI has been retired. " +
            "Use the signed Opticon installer or its controller repair mode so the fixed System32 tool, " +
            "publisher identity, task state, and rollback are verified together.",
            "Signed repair required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

    private Task RunMaintenanceBootstrapAsync(
        DeviceRecord device,
        OpticonUpdateRelease release,
        string reason)
    {
        _viewModel.Log(
            $"Copied PowerShell/UAC maintenance bootstraps are retired for {device.Name}; release {release.Version} was not launched.");
        MessageBox.Show(
            reason + "\n\n" +
            "The legacy copied PowerShell/UAC maintenance bootstrap has been retired because a same-user process could race its verified files before elevation. " +
            "No command was copied or started. Create a fresh hosted source-build invitation for this device instead; " +
            "the invitation pins the source and bootstrap hashes, builds under the exact .NET SDK, and installs through protected machine storage.",
            "Use a signed source-build invitation",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return Task.CompletedTask;
    }

    private DeviceRecord RequireDevice() => _viewModel.SelectedDevice ?? throw new InvalidOperationException("Select a device first.");

    private static async Task SetClipboardTextAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        const int clipboardBusy = unchecked((int)0x800401D0);
        ExternalException? last = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                System.Windows.Clipboard.SetDataObject(value, copy: true);
                return;
            }
            catch (ExternalException exception) when (exception.HResult == clipboardBusy)
            {
                last = exception;
                // SetDataObject can publish the data before OLE reports that a
                // competing clipboard owner prevented the final flush.
                try
                {
                    if (System.Windows.Clipboard.ContainsText()
                        && string.Equals(System.Windows.Clipboard.GetText(), value, StringComparison.Ordinal))
                        return;
                }
                catch (ExternalException verificationException) when (verificationException.HResult == clipboardBusy)
                {
                    last = verificationException;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, attempt * 50)), cancellationToken);
        }

        throw new InvalidOperationException(
            "Windows kept the clipboard busy for several seconds. Close any clipboard-history, synchronization, or remote-control overlay and try again; no maintenance command was started.",
            last);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private static void ShowError(Exception exception) =>
        MessageBox.Show(exception.Message, "Opticon", MessageBoxButton.OK, MessageBoxImage.Error);

    private static void RevealFile(string path)
    {
        var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var explorer = Path.Combine(windows, "explorer.exe");
        if (!File.Exists(explorer)
            || (File.GetAttributes(windows) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(explorer) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The fixed Windows Explorer executable is unavailable or unsafe.");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
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
