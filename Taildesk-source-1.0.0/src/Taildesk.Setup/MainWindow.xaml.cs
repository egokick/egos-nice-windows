using System.Text.Json;
using System.Windows;
using Taildesk.Shared;

namespace Taildesk.Setup;

public partial class MainWindow : Window
{
    private InvitePayload? _invite;
    private string _invitePath = string.Empty;
    private string _hostedFragmentKey = string.Empty;
    private CancellationTokenSource? _cancellation;
    private bool _installationRunning;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _invitePath = ResolveInvitePath();
            if (!string.IsNullOrWhiteSpace(_hostedFragmentKey))
            {
                var encrypted = await File.ReadAllBytesAsync(_invitePath);
                var signedEnvelope = HostedInviteFile.Decrypt(_hostedFragmentKey, encrypted);
                try { _invite = HostedInviteFile.ReadSigned(signedEnvelope); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(signedEnvelope); }
            }
            else
            {
                await using var stream = File.OpenRead(_invitePath);
                _invite = await JsonSerializer.DeserializeAsync<InvitePayload>(stream, JsonDefaults.Options)
                          ?? throw new InvalidDataException("The invitation file is empty.");
            }
            DeviceNameText.Text = _invite.DeviceName;
            RoleText.Text = _invite.Role == DeviceRole.ManagedOnly ? "Remote control target only" : "Can manage other machines and be managed";
            CoordinatorText.Text = _invite.CoordinatorUrl;
            ExpiresText.Text = _invite.ExpiresAt.ToLocalTime().ToString("f");
            AppendLog("Invitation loaded.");
            AppendLog("The installer will add Opticon's managed network, remote-access, and agent components.");
            AppendLog("No router port forwarding is required.");
            await RunInstallAsync();
        }
        catch (Exception exception)
        {
            InstallButton.IsEnabled = false;
            StatusText.Text = "The invitation cannot be used.";
            AppendLog("ERROR: " + exception.Message);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        if (_invite is null || _installationRunning)
        {
            return;
        }

        InstallButton.IsEnabled = false;
        _cancellation = new CancellationTokenSource();
        _installationRunning = true;
        var progress = new Progress<InstallProgress>(item =>
        {
            InstallProgress.Value = item.Percent;
            StatusText.Text = item.Message;
            AppendLog(item.Message);
        });

        try
        {
            var installer = new InstallCoordinator(_invite, Path.GetDirectoryName(_invitePath)!, progress);
            await installer.InstallAsync(_cancellation.Token);
            AppendLog("Setup finished successfully.");
            InstallButton.Content = "Close";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += (_, _) => Close();
            try { File.Delete(_invitePath); } catch { }
        }
        catch (ExistingTailscaleSessionException exception)
        {
            AppendLog("NOTICE: " + exception.Message);
            var answer = MessageBox.Show(
                exception.Message + "\n\nThis disconnects the current Tailscale identity before joining the Opticon private network. Continue?",
                "Re-enroll Tailscale", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    var installer = new InstallCoordinator(_invite, Path.GetDirectoryName(_invitePath)!, progress, allowTailscaleReauthentication: true);
                    await installer.InstallAsync(_cancellation.Token);
                    AppendLog("Setup finished successfully.");
                    InstallProgress.Value = 100;
                    StatusText.Text = "Connected. This machine is ready.";
                    InstallButton.Content = "Close";
                    InstallButton.IsEnabled = true;
                    InstallButton.Click -= InstallButton_Click;
                    InstallButton.Click += (_, _) => Close();
                    try { File.Delete(_invitePath); } catch { }
                    return;
                }
                catch (Exception retryException)
                {
                    StatusText.Text = "Installation stopped. See the error below.";
                    AppendLog("ERROR: " + retryException.Message);
                }
            }
            InstallButton.Content = "Try again";
            InstallButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Installation stopped. See the error below.";
            AppendLog("ERROR: " + exception.Message);
            InstallButton.Content = "Try again";
            InstallButton.IsEnabled = true;
        }
        finally
        {
            _installationRunning = false;
        }
    }

    private string ResolveInvitePath()
    {
        var arguments = Environment.GetCommandLineArgs();
        var hosted = arguments.FirstOrDefault(value => value.StartsWith("--hosted-invite=", StringComparison.OrdinalIgnoreCase));
        if (hosted is not null)
        {
            var keyArgument = arguments.FirstOrDefault(value => value.StartsWith("--invite-key=", StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException("The hosted invitation is missing its private link key.");
            _hostedFragmentKey = keyArgument[13..].Trim('"');
            var hostedPath = hosted[16..].Trim('"');
            return File.Exists(hostedPath) ? Path.GetFullPath(hostedPath) : throw new FileNotFoundException("The encrypted hosted invitation was not downloaded.");
        }
        var argument = arguments.FirstOrDefault(value => value.StartsWith("--invite=", StringComparison.OrdinalIgnoreCase));
        var path = argument is null ? Path.Combine(AppContext.BaseDirectory, "invite.tdinvite") : argument[9..].Trim('"');
        return File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException("invite.tdinvite was not found next to Opticon Setup.");
    }

    private void AppendLog(string message)
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
    }
}
