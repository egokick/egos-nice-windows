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

            if (release.RequiresMaintenanceBootstrap)
            {
                await RunMaintenanceBootstrapAsync(
                    device,
                    release,
                    $"{device.Name} runs an Agent that predates the guarded update API.");
                return;
            }

            var explanation =
                $"Update '{device.Name}' from Opticon Agent {device.AgentVersion} to {release.Version}?\n\n" +
                "Opticon will first download and fully verify the release while the current Agent and recovery lifelines remain active. " +
                "The stable SYSTEM Guardian, installed outside the versioned Agent, then swaps only the signed Agent/runtime directory. Tailscale, RustDesk, Guardian-owned SSH, credentials, and routes are not changed.\n\n" +
                "The new Agent must pass repeated command-center and local checks for every applicable recovery lifeline. If it crashes, loses a lifeline, the PC reboots, or this command center cannot send the final commit, the Guardian automatically restores the previous Agent.";
            if (MessageBox.Show(explanation, "Guarded Opticon Agent update", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

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
                ? $"Opticon Agent {result.TargetVersion} is healthy and committed on {device.Name}. The prior Agent remains available locally for boot-time recovery."
                : $"The candidate was not committed. {device.Name} reported {result.Phase} and remains on Opticon Agent {result.CurrentVersion}.\n\n{result.Message}";
            var requiresAttendedMaintenance = result.Phase == UpdatePhase.Failed
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
        if ((sender as DataGrid)?.SelectedItem is not InviteRecord invite) return;
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

    private static string BuildMaintenanceBootstrapCommand(
        OpticonUpdateRelease release,
        DeviceRecord device,
        Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new InvalidOperationException("Maintenance requires a non-empty operation ID.");
        if (string.IsNullOrWhiteSpace(device.TailnetDeviceId)
            || device.TailnetDeviceId.Length > 256
            || device.TailnetDeviceId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new InvalidOperationException(
                "The selected registry record has no valid Tailscale device identity. Refresh devices before copying maintenance.");
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp))
            throw new InvalidOperationException("The selected device has no canonical Tailscale IPv4 address.");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var url = release.DownloadUri.AbsoluteUri.Replace("'", "''", StringComparison.Ordinal);
        var size = release.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var certificateBase64 = Convert.ToBase64String(InvitationSigning.PinnedCertificate.RawData);
        return "$ErrorActionPreference='Stop';"
               + "$d=Join-Path $env:TEMP 'Opticon-Maintenance-" + suffix + "';"
               + "$z=$d+'.zip';"
               + "New-Item -ItemType Directory -Path $d -ErrorAction Stop|Out-Null;"
               + "Add-Type -AssemblyName System.Net.Http;"
               + "$ph=[Net.Http.HttpClientHandler]::new();$ph.UseProxy=$false;$ph.AllowAutoRedirect=$false;$ph.CheckCertificateRevocationList=$true;"
               + "$hc=[Net.Http.HttpClient]::new($ph);try{$hc.Timeout=[TimeSpan]::FromMinutes(20);$rs=$hc.GetStreamAsync('" + url + "').GetAwaiter().GetResult();"
               + "try{$fs=[IO.File]::Open($z,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$rs.CopyTo($fs)}finally{$fs.Dispose()}}finally{$rs.Dispose()}}finally{$hc.Dispose();$ph.Dispose()};"
               + "if((Get-Item -LiteralPath $z).Length -ne " + size + "){throw 'Downloaded Opticon bundle size mismatch.'};"
               + "$h=(Get-FileHash -LiteralPath $z -Algorithm SHA256).Hash.ToLowerInvariant();"
               + "if($h -ne '" + release.Sha256 + "'){throw 'Downloaded Opticon bundle SHA-256 mismatch.'};"
               + "Expand-Archive -LiteralPath $z -DestinationPath $d -Force;"
               + "$m=Join-Path $d 'release-manifest.json';$q=Join-Path $d 'release-manifest.sig';"
               + "if(!(Test-Path -LiteralPath $m) -or !(Test-Path -LiteralPath $q)){throw 'Signed release metadata is missing.'};"
               + "$mb=[IO.File]::ReadAllBytes($m);"
               + "$sb=[Convert]::FromBase64String([IO.File]::ReadAllText($q).Trim());"
               + "$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,[Convert]::FromBase64String('"
               + certificateBase64
               + "'));"
               + "try{$rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert);"
               + "if(!$rsa){throw 'Pinned release certificate has no RSA key.'};"
               + "try{if(!$rsa.VerifyData($mb,$sb,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw 'Release manifest signature is invalid.'}}finally{$rsa.Dispose()}}finally{$cert.Dispose()};"
               + "$j=[Text.Encoding]::UTF8.GetString($mb)|ConvertFrom-Json;"
               + "$f=@($j.files|Where-Object {$_.path -ceq 'Taildesk.Setup.exe'});"
               + "if($j.schemaVersion -ne 1 -or $j.updateProtocolVersion -ne 1 -or $f.Count -ne 1){throw 'Signed Setup declaration is missing or ambiguous.'};"
               + "if(([string]$f[0].signerThumbprint) -ne '"
               + InvitationSigning.CertificateThumbprint
               + "'){throw 'Signed Setup publisher pin is invalid.'};"
               + "$s=Join-Path $d 'Taildesk.Setup.exe';"
               + "if(!(Test-Path -LiteralPath $s)){throw 'Taildesk.Setup.exe is missing from the signed bundle.'};"
               + "if((Get-Item -LiteralPath $s).Length -ne [long]$f[0].size){throw 'Signed Setup size mismatch.'};"
               + "$sh=(Get-FileHash -LiteralPath $s -Algorithm SHA256).Hash.ToLowerInvariant();"
               + "if($sh -ne ([string]$f[0].sha256).ToLowerInvariant()){throw 'Signed Setup SHA-256 mismatch.'};"
               + "$a=@('--maintenance','--expected-tailnet-device-id="
               + device.TailnetDeviceId.Replace("'", "''", StringComparison.Ordinal)
               + "','--expected-tailscale-ip=" + device.TailscaleIp
               + "','--operation-id=" + operationId.ToString("N") + "');"
               + "Start-Process -FilePath $s -ArgumentList $a -Verb RunAs -Wait";
    }

    private async Task RunMaintenanceBootstrapAsync(
        DeviceRecord device,
        OpticonUpdateRelease release,
        string reason)
    {
        var operationId = Guid.NewGuid();
        var command = BuildMaintenanceBootstrapCommand(release, device, operationId);
        var instructions =
            reason + " Opticon will not send arbitrary commands to it.\n\n" +
            "Opticon selected this immutable role-specific bundle:\n" +
            $"{release.DownloadUri.AbsoluteUri}\nSHA-256: {release.Sha256}\n" +
            $"Operation: {operationId:N}\n\n" +
            "Choose Yes to copy a size-, SHA-256-, publisher-, Tailnet-device-, Tailscale-address-, and operation-pinned PowerShell command, snapshot recovery, open Remote into, and let this command center watch for the exact candidate for up to 30 minutes. The command bypasses ambient Windows proxies and verifies the extracted Setup signature before requesting elevation. Then, in the remote Windows session:\n" +
            "1. Open PowerShell.\n" +
            "2. Paste the copied command and press Enter.\n" +
            "3. Approve the one UAC prompt for Taildesk.Setup.exe.\n" +
            "4. Keep RustDesk, Setup, and this Opticon window open through the terminal result.\n\n" +
            "Setup must pass three protected local samples but cannot commit. This command center alone requires three authenticated external samples for the exact operation, release, architecture, IP, Tailnet identity, RustDesk, and any snapshotted SSH listener. If confirmation is lost or late, no commit is sent and the Guardian rolls back.\n\n" +
            "The one-time bootstrap keeps enrollment, Tailscale, RustDesk, routes, credentials, and Admin unchanged. Later Agent releases use the guarded update path.";
        if (MessageBox.Show(
                instructions,
                "One-time signed Agent bootstrap",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await SetClipboardTextAsync(command);
        var sshWasListening = await _viewModel.SnapshotMaintenanceSshAsync(device);
        await _viewModel.LaunchRemoteControlAsync(device);
        var progressWindow = new UpdateProgressWindow(device.Name, device.AgentVersion, release.Version)
        {
            Owner = this,
            DataContext = _viewModel
        };
        progressWindow.Show();
        UpdateStatusDto maintenanceResult;
        try
        {
            maintenanceResult = await _viewModel.ObserveMaintenanceBootstrapAsync(
                device, release, operationId, sshWasListening);
        }
        finally
        {
            progressWindow.FinishAndClose();
            Activate();
        }
        var maintenanceMessage = maintenanceResult.Phase == UpdatePhase.Committed
            ? $"Opticon Agent {maintenanceResult.TargetVersion} is externally verified and committed on {device.Name}."
            : $"The maintenance candidate was not committed. {device.Name} reported {maintenanceResult.Phase}.\n\n{maintenanceResult.Message}";
        MessageBox.Show(
            maintenanceMessage,
            "One-time signed Agent bootstrap",
            MessageBoxButton.OK,
            maintenanceResult.Phase == UpdatePhase.Committed ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
