using System.Diagnostics;
using System.Text.RegularExpressions;
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
    private bool _sourceLauncherMode;
    private bool _sourceAttestedAutomaticInstall;
    private SetupResumeContext? _resumeContext;
    private string? _sourceAttestationPath;
    private MaintenanceExpectedTarget? _maintenanceTarget;
    private string _logPath = string.Empty;
    private StreamWriter? _logWriter;
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
        Closed += (_, _) => _logWriter?.Dispose();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            _sourceLauncherMode = HostedBootstrapper.IsSourceLauncher(Environment.ProcessPath);
            var replaceExisting = arguments.Any(argument =>
                argument.Equals("--replace-existing", StringComparison.OrdinalIgnoreCase));
            if (arguments.Any(argument => argument.Equals("--resume", StringComparison.OrdinalIgnoreCase)))
            {
                _resumeContext = await SetupResumeCoordinator.LoadForCurrentProcessAsync(CancellationToken.None)
                                 ?? throw new InvalidDataException(
                                     "The protected Setup reboot continuation state is missing.");
            }
            _sourceAttestedAutomaticInstall = arguments.Any(argument =>
                argument.StartsWith("--source-attestation=", StringComparison.OrdinalIgnoreCase))
                                             || _resumeContext is not null;
            if (_sourceAttestedAutomaticInstall)
                Environment.ExitCode = 1;
            if (replaceExisting && (_resumeContext is not null || !_sourceAttestedAutomaticInstall))
                throw new InvalidDataException(
                    "Existing-install replacement is allowed only for the initial authenticated source-build handoff.");
            AppendLog($"Opticon Setup {typeof(MainWindow).Assembly.GetName().Version} started.");
            AppendLog("Executable: " + (Environment.ProcessPath ?? "unavailable"));
            AppendLog("Launch inputs: " + DescribeLaunchInputs(arguments));
            if (_sourceLauncherMode)
            {
                Environment.ExitCode = 1;
                var bootstrap = HostedBootstrapper.ParseSourceLaunch(arguments, Environment.ProcessPath);
                StatusText.Text = "Verifying the pinned Opticon source release...";
                var handoffExitCode = await HostedBootstrapper.LaunchSourceOnlyAsync(bootstrap, message =>
                {
                    StatusText.Text = message;
                    AppendLog(message);
                });
                Environment.ExitCode = handoffExitCode;
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
            var encrypted = await File.ReadAllBytesAsync(_invitePath);
            var signedEnvelope = HostedInviteFile.Decrypt(_hostedFragmentKey, encrypted);
            try { _invite = HostedInviteFile.ReadWithEmbeddedValidationPolicy(signedEnvelope); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(signedEnvelope); }
            var validation = ClientInstallValidationPolicy.Normalize(_invite.ClientInstallValidation);
            _invite.ClientInstallValidation = validation;
            MachineStorageSecurity.BypassValidation =
                !validation.IsEnabled(ClientInstallValidationStep.MachineState);
            ProductSigning.BypassValidation =
                !validation.IsEnabled(ClientInstallValidationStep.PayloadAuthenticity);
            if (validation.IsEnabled(ClientInstallValidationStep.ProtectedPaths))
                HostedBootstrapper.RequireProtectedHandoff(_invitePath, AppContext.BaseDirectory);
            var sourceAttestationArgument = arguments.FirstOrDefault(value =>
                value.StartsWith("--source-attestation=", StringComparison.OrdinalIgnoreCase));
            _sourceAttestationPath = sourceAttestationArgument is null
                ? _resumeContext?.SourceAttestationPath
                : sourceAttestationArgument[21..].Trim('"');
            if (validation.IsEnabled(ClientInstallValidationStep.MachineState)
                && _resumeContext is not null && _resumeContext.InviteId != _invite.InviteId)
                throw new InvalidDataException("The protected reboot continuation belongs to a different invitation.");
            if (validation.IsEnabled(ClientInstallValidationStep.InvitationConstraints)
                && _invite.SchemaVersion != InvitationPolicy.HostedLinkSchemaVersion)
                throw new InvalidDataException(
                    "This legacy invitation is retained for history only and cannot install software. Ask for a new source-build invitation.");
            if (_invite.SchemaVersion == InvitationPolicy.HostedLinkSchemaVersion)
            {
                if (validation.IsEnabled(ClientInstallValidationStep.SourceBuildProvenance)
                    && string.IsNullOrWhiteSpace(_sourceAttestationPath))
                    throw new InvalidDataException("This source-build invitation requires an elevated build attestation.");
                await SourceBuildProvenance.ActivateForSetupAsync(
                    _sourceAttestationPath ?? string.Empty, _invitePath, _invite, AppContext.BaseDirectory,
                    validation, CancellationToken.None);
                AppendLog($"Authenticated local source build {_invite.ReleaseVersion} was reverified after elevation.");
                if (replaceExisting)
                {
                    var replacingValidatedLegacyInstallation =
                        await LegacyOpticonRemoval.PreflightLegacyInstallationIfPresentAsync();
                    var replacementPreflight = await SetupPreflight.DiscoverElevatedAsync(
                        _invite,
                        AppContext.BaseDirectory,
                        CancellationToken.None,
                        replacingValidatedLegacyInstallation);
                    ReportReplacementPreflight(replacementPreflight);
                    if (replacementPreflight.IsBlocked)
                        throw new SetupPreflightBlockedException(replacementPreflight);
                    AppendLog(
                        "Elevated replacement preflight completed without changing installed files or tasks.");
                    await LegacyOpticonRemoval.RemoveLegacyInstallationIfPresentAsync(message =>
                    {
                        StatusText.Text = message;
                        AppendLog(message);
                    });
                }
            }
            else if (sourceAttestationArgument is not null)
            {
                throw new InvalidDataException("A source-build attestation cannot be applied to a legacy invitation.");
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
            if (_sourceAttestedAutomaticInstall)
                TryRollbackSourceProvenance();
            StatusText.Text = _maintenanceMode
                ? "Maintenance could not validate this selected device."
                : "The invitation cannot be used.";
            AppendException(exception);
            if (_sourceLauncherMode || _sourceAttestedAutomaticInstall)
                ConfigureFailureAction(requireRelaunch: true);
            else
                InstallButton.IsEnabled = false;
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
            InstallResult? installResult = null;
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
                var installer = new InstallCoordinator(
                    _invite!, AppContext.BaseDirectory, progress,
                    allowTailscaleReauthentication: false,
                    resumeContext: GetContinuationContext());
                var result = await installer.InstallAsync(_cancellation.Token);
                installResult = result;
                await ClearResumeContinuationAsync();
                ApplyInstallResult(result);
            }
            AppendLog(_maintenanceMode
                ? "Setup finished successfully."
                : "Setup finished. Review any warnings above.");
            InstallButton.Content = "Close";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += (_, _) => Close();
            if (!_maintenanceMode && installResult is not null
                && CanDiscardInvitation(installResult))
            {
                try { File.Delete(_invitePath); } catch { }
            }
        }
        catch (SetupRebootRequiredException exception)
        {
            // Do not roll back authenticated source provenance here: the
            // protected journal and scheduled task are the deliberate recovery
            // mechanism for a Windows capability reboot.
            Environment.ExitCode = 3010;
            StatusText.Text = "Windows restart required. Setup will resume automatically after restart.";
            AppendLog("REBOOT REQUIRED: " + exception.Message);
            InstallButton.Content = "Close";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += (_, _) => Close();
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
                    var installer = new InstallCoordinator(
                        _invite!, AppContext.BaseDirectory, progress,
                        allowTailscaleReauthentication: true,
                        resumeContext: GetContinuationContext());
                    var result = await installer.InstallAsync(_cancellation.Token);
                    await ClearResumeContinuationAsync();
                    ApplyInstallResult(result);
                    AppendLog("Setup finished. Review any warnings above.");
                    InstallProgress.Value = 100;
                    InstallButton.Content = "Close";
                    InstallButton.IsEnabled = true;
                    InstallButton.Click -= InstallButton_Click;
                    InstallButton.Click += (_, _) => Close();
                    if (CanDiscardInvitation(result))
                    {
                        try { File.Delete(_invitePath); } catch { }
                    }
                    return;
                }
                catch (Exception retryException)
                {
                    TryRollbackSourceProvenance();
                    StatusText.Text = "Installation stopped. See the error below.";
                    AppendException(retryException);
                }
            }
            TryRollbackSourceProvenance();
            ConfigureFailureAction(_sourceAttestedAutomaticInstall);
        }
        catch (Exception exception)
        {
            TryRollbackSourceProvenance();
            StatusText.Text = "Installation stopped. See the error below.";
            AppendException(exception);
            ConfigureFailureAction(_sourceAttestedAutomaticInstall);
        }
        finally
        {
            _installationRunning = false;
        }
    }

    private void TryRollbackSourceProvenance()
    {
        if (!_sourceAttestedAutomaticInstall) return;
        try { SourceBuildProvenance.RollbackActiveInstallation(); }
        catch (Exception exception) { AppendLog("WARNING: protected source provenance rollback failed: " + exception.Message); }
    }

    private void ConfigureFailureAction(bool requireRelaunch)
    {
        InstallButton.Click -= InstallButton_Click;
        InstallButton.Click -= CloseAfterFailure_Click;
        if (requireRelaunch)
        {
            AppendLog(
                "RETRY REQUIRED: Close Setup and launch the authenticated invitation installer again. " +
                "This window will not reuse rolled-back source trust.");
            InstallButton.Content = "Close";
            InstallButton.Click += CloseAfterFailure_Click;
        }
        else
        {
            InstallButton.Content = "Try again";
            InstallButton.Click += InstallButton_Click;
        }
        InstallButton.IsEnabled = true;
    }

    private void CloseAfterFailure_Click(object sender, RoutedEventArgs e) => Close();

    private void ReportReplacementPreflight(InstallerPreflightReport report)
    {
        foreach (var finding in report.Findings)
        {
            var prefix = finding.Severity switch
            {
                InstallerPreflightSeverity.Blocked => "Blocked",
                InstallerPreflightSeverity.Repair => "Planned repair",
                _ => "Preflight"
            };
            var message = $"{prefix}: {finding.Area} — {finding.Detail}";
            StatusText.Text = message;
            AppendLog(message);
        }
    }

    private void MarkAutomaticInstallSucceeded()
    {
        if (_sourceAttestedAutomaticInstall)
            Environment.ExitCode = 0;
    }

    private void ApplyInstallResult(InstallResult result)
    {
        foreach (var warning in result.Warnings)
            AppendLog($"DEFERRED REPAIR: {warning.Operation} — {warning.Detail}");
        if (result.RemoteDesktopReady)
        {
            MarkAutomaticInstallSucceeded();
            StatusText.Text = result.HasWarnings
                ? "Remote desktop is ready. Some management components need repair; see the log."
                : "Connected. This machine is ready.";
            return;
        }
        if (result.MeshConnected && result.AgentReady && result.SshRecoveryReady)
        {
            MarkAutomaticInstallSucceeded();
            StatusText.Text =
                "Mesh and SSH recovery are ready. Direct remote desktop needs repair; see the log.";
            return;
        }
        if (result.MeshConnected)
        {
            // The device is reachable on the private mesh but does not yet
            // have a proven interactive recovery channel. Preserve the build
            // and invitation for repair, and make the incomplete exit status
            // visible to the source launcher/automation.
            StatusText.Text =
                "The private mesh is connected, but remote access still needs repair; see the log.";
            return;
        }
        throw new InvalidOperationException(
            "Setup finished without establishing the private Opticon mesh.");
    }

    private static bool HasUsableRecoveryChannel(InstallResult result) =>
        result.RemoteDesktopReady
        || (result.MeshConnected && result.AgentReady && result.SshRecoveryReady);

    private static bool CanDiscardInvitation(InstallResult result) =>
        HasUsableRecoveryChannel(result) && !result.HasWarnings;

    private async Task ClearResumeContinuationAsync()
    {
        if (_resumeContext is null) return;
        try
        {
            await SetupResumeCoordinator.ClearAsync(CancellationToken.None);
            _resumeContext = null;
        }
        catch (Exception exception)
        {
            // Enrollment is already committed. Treat task cleanup as a
            // recoverable maintenance action rather than rolling back a valid
            // enrollment or source provenance.
            AppendLog("WARNING: completed Setup could not remove its reboot continuation: " + exception.Message);
        }
    }

    private SetupResumeContext? GetContinuationContext()
    {
        if (_resumeContext is not null) return _resumeContext;
        if (!_sourceAttestedAutomaticInstall || _invite is null
            || string.IsNullOrWhiteSpace(_sourceAttestationPath)
            || string.IsNullOrWhiteSpace(_invitePath)
            || string.IsNullOrWhiteSpace(_hostedFragmentKey)
            || string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return null;
        return new SetupResumeContext(
            _invite.InviteId,
            Path.GetFullPath(_invitePath),
            Path.GetFullPath(_sourceAttestationPath),
            Path.GetFullPath(Environment.ProcessPath),
            _hostedFragmentKey);
    }

    private string ResolveInvitePath()
    {
        if (_resumeContext is not null)
        {
            _hostedFragmentKey = _resumeContext.InviteKey;
            return _resumeContext.InvitePath;
        }
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
        throw new InvalidDataException(
            "Plaintext and legacy local invitation files are no longer accepted. Return to the Opticon invitation page and download a current authenticated installer.");
    }

    private void AppendLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {SanitizeForLog(message)}{Environment.NewLine}";
        LogText.AppendText(entry);
        LogText.ScrollToEnd();
        if (_logWriter is null) return;
        try { _logWriter.Write(entry); }
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
            var directory = HostedBootstrapper.CreateProtectedHandoffDirectory();
            _logPath = Path.Combine(directory, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            var stream = new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            _logWriter = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
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
            Clipboard.SetText(_logPath);
            StatusText.Text = "The protected setup-log path was copied. Open it after Setup exits.";
        }
        catch (Exception exception)
        {
            AppendLog("The setup log path could not be copied: " + exception.Message);
        }
    }
}
