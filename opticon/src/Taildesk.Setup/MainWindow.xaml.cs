using System.Diagnostics;
using System.Text.RegularExpressions;
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
    private bool _maintenanceMode;
    private MaintenanceExpectedTarget? _maintenanceTarget;
    private string _logPath = string.Empty;
    private static readonly Regex InviteFileSecretPattern = new(
        "(Install-Opticon-[A-Za-z0-9_-]{24,128}--)[A-Za-z0-9_-]{32,128}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InviteArgumentSecretPattern = new(
        "(--invite-key=)[^\\s]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FragmentSecretPattern = new(
        "(#[A-Za-z0-9_-]{0,23})[A-Za-z0-9_-]{32,128}",
        RegexOptions.CultureInvariant);

    public MainWindow()
    {
        InitializeComponent();
        InitializeLog();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            AppendLog($"Opticon Setup {typeof(MainWindow).Assembly.GetName().Version} started.");
            AppendLog("Executable: " + (Environment.ProcessPath ?? "unavailable"));
            AppendLog("Launch inputs: " + DescribeLaunchInputs(arguments));
            if (arguments.Length == 0 && HostedBootstrapper.TryParse(Environment.ProcessPath, out var bootstrap))
            {
                StatusText.Text = "Starting the signed Opticon installer?";
                await HostedBootstrapper.LaunchSetupAsync(bootstrap, message =>
                {
                    StatusText.Text = message;
                    AppendLog(message);
                });
                Close();
                return;
            }
            _maintenanceMode = arguments
                .Any(argument => argument.Equals("--maintenance", StringComparison.OrdinalIgnoreCase));
            if (_maintenanceMode)
            {
                _maintenanceTarget = MaintenanceExpectedTarget.Parse(arguments);
                Title = "Opticon Agent maintenance";
                var target = await MaintenanceBootstrapCoordinator.LoadTargetSummaryAsync(_maintenanceTarget);
                DeviceNameText.Text = target.DeviceName;
                RoleText.Text = target.Role == DeviceRole.ManagedOnly
                    ? "Remote control target only"
                    : "Can manage other machines and be managed";
                CoordinatorText.Text = target.CoordinatorUrl;
                ExpiresText.Text = $"Installed Agent {target.CurrentVersion}; signed release required";
                InstallButton.Content = "Run Agent maintenance";
                AppendLog("Existing enrollment loaded. Device identity and credentials will not be replaced.");
                AppendLog("Maintenance updates only the Opticon Agent through the fail-safe Guardian.");
                AppendLog("Tailscale and RustDesk remain installed and running as recovery lifelines.");
                await RunInstallAsync();
                return;
            }

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
            StatusText.Text = _maintenanceMode
                ? "Maintenance could not validate this selected device."
                : "The invitation cannot be used.";
            AppendException(exception);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        if ((!_maintenanceMode && _invite is null) || _installationRunning)
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
            if (_maintenanceMode)
            {
                var maintenance = new MaintenanceBootstrapCoordinator(
                    AppContext.BaseDirectory, progress,
                    _maintenanceTarget ?? throw new InvalidOperationException("Maintenance target arguments were not validated."));
                await maintenance.RunAsync(_cancellation.Token);
                InstallProgress.Value = 100;
                StatusText.Text = "The signed Opticon Agent update is committed.";
            }
            else
            {
                var installer = new InstallCoordinator(_invite!, AppContext.BaseDirectory, progress);
                await installer.InstallAsync(_cancellation.Token);
                StatusText.Text = "Connected. This machine is ready.";
            }
            AppendLog("Setup finished successfully.");
            InstallButton.Content = "Close";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += (_, _) => Close();
            if (!_maintenanceMode)
            {
                try { File.Delete(_invitePath); } catch { }
            }
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
                    var installer = new InstallCoordinator(_invite!, AppContext.BaseDirectory, progress, allowTailscaleReauthentication: true);
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
                    AppendException(retryException);
                }
            }
            InstallButton.Content = "Try again";
            InstallButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Installation stopped. See the error below.";
            AppendException(exception);
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
        var hostedPath = hosted is null
            ? Environment.GetEnvironmentVariable(HostedBootstrapper.InvitePathEnvironmentVariable)
            : hosted[16..].Trim('"');
        if (!string.IsNullOrWhiteSpace(hostedPath))
        {
            var keyArgument = arguments.FirstOrDefault(value => value.StartsWith("--invite-key=", StringComparison.OrdinalIgnoreCase));
            _hostedFragmentKey = keyArgument is null
                ? Environment.GetEnvironmentVariable(HostedBootstrapper.InviteKeyEnvironmentVariable) ?? string.Empty
                : keyArgument[13..].Trim('"');
            Environment.SetEnvironmentVariable(HostedBootstrapper.InvitePathEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(HostedBootstrapper.InviteKeyEnvironmentVariable, null);
            if (string.IsNullOrWhiteSpace(_hostedFragmentKey))
                throw new InvalidDataException("The hosted invitation is missing its private link key.");
            return File.Exists(hostedPath) ? Path.GetFullPath(hostedPath) : throw new FileNotFoundException("The encrypted hosted invitation was not downloaded.");
        }
        var argument = arguments.FirstOrDefault(value => value.StartsWith("--invite=", StringComparison.OrdinalIgnoreCase));
        if (argument is null && HostedBootstrapper.IsPublishedBootstrap(Environment.ProcessPath))
            throw new FileNotFoundException(
                "This installer lost its invitation identity while downloading. Return to the Opticon invitation page and download it again.");
        var path = argument is null ? Path.Combine(AppContext.BaseDirectory, "invite.tdinvite") : argument[9..].Trim('"');
        return File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException("invite.tdinvite was not found next to Opticon Setup.");
    }

    private void AppendLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {SanitizeForLog(message)}{Environment.NewLine}";
        LogText.AppendText(entry);
        LogText.ScrollToEnd();
        if (string.IsNullOrWhiteSpace(_logPath)) return;
        try { File.AppendAllText(_logPath, entry); }
        catch { OpenLogButton.IsEnabled = false; }
    }

    private void AppendException(Exception exception)
    {
        AppendLog("ERROR: " + exception.Message);
        AppendLog(exception.ToString());
        DetailsExpander.IsExpanded = true;
    }

    private void InitializeLog()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Opticon", "Logs", "Setup");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(_logPath, string.Empty);
            LogPathText.Text = _logPath;
            LogPathText.ToolTip = _logPath;
        }
        catch
        {
            _logPath = string.Empty;
            LogPathText.Text = "A persistent log file could not be created.";
            OpenLogButton.IsEnabled = false;
        }
    }

    private static string DescribeLaunchInputs(IEnumerable<string> arguments)
    {
        var descriptions = arguments.Select(argument =>
        {
            var separator = argument.IndexOf('=');
            return separator > 0 ? argument[..separator] + "=[provided]" : argument;
        }).ToList();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostedBootstrapper.InvitePathEnvironmentVariable)))
            descriptions.Add(HostedBootstrapper.InvitePathEnvironmentVariable + "=[provided]");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostedBootstrapper.InviteKeyEnvironmentVariable)))
            descriptions.Add(HostedBootstrapper.InviteKeyEnvironmentVariable + "=[provided]");
        return descriptions.Count == 0 ? "none" : string.Join(", ", descriptions);
    }

    private string SanitizeForLog(string value)
    {
        if (!string.IsNullOrWhiteSpace(_hostedFragmentKey))
            value = value.Replace(_hostedFragmentKey, "[private-key-redacted]", StringComparison.Ordinal);
        value = InviteFileSecretPattern.Replace(value, "$1[private-key-redacted]");
        value = InviteArgumentSecretPattern.Replace(value, "$1[private-key-redacted]");
        return FragmentSecretPattern.Replace(value, "$1[private-key-redacted]");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogText.Text);
            StatusText.Text = "Detailed setup log copied.";
        }
        catch (Exception exception)
        {
            AppendLog("The setup log could not be copied: " + exception.Message);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logPath)) throw new FileNotFoundException("No persistent setup log is available.");
            Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppendLog("The setup log file could not be opened: " + exception.Message);
        }
    }
}
